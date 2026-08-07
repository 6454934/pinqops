using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PinqOps;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Tests.Web;

/// <summary>
/// The preview routes, through the real <c>Program</c>.
///
/// <para>Every other route addressed by an app id goes through
/// <see cref="EndpointHelpers.ResolveApp(UiConfigStore, HttpContext)"/>, which
/// fetches the visibility rule from the request so no handler has to remember it.
/// These two did not: the listing walked every app in the config, and teardown
/// called <c>AppResolver.Resolve</c> itself. That helper's <c>canView</c> argument
/// is optional, so leaving it out compiled and went on behaving exactly as it had
/// before teams existed — and teardown is destructive. Someone outside the granted
/// team could destroy the preview containers, volumes and compose project of an app
/// the settings page will not even list for them.</para>
/// </summary>
[Collection(TestServerCollection.Name)]
public sealed class PreviewVisibilityTests : IClassFixture<PreviewVisibilityTests.AppFixture>
{
    private readonly AppFixture _app;

    // Rebuilt before every test: a teardown that is allowed to run deletes the
    // preview, and whether that happened before or after another test in this class
    // is not something the class should depend on.
    public PreviewVisibilityTests(AppFixture app)
    {
        _app = app;
        _app.EnsurePreview();
    }

    /// <summary>With nothing granted, the listing looks as it did before teams existed.</summary>
    [Fact]
    public async Task AnUnclaimedAppsPreviewsAreListedForEveryone()
    {
        Assert.Contains(AppFixture.AppId, await _app.PreviewApps(_app.DeployerSession));
        Assert.Contains(AppFixture.AppId, await _app.PreviewApps(_app.AdminSession));
    }

    [Fact]
    public async Task AnAppClaimedByAnotherTeamHasItsPreviewsHidden()
    {
        await _app.Grant(AppFixture.OtherTeam);
        try
        {
            Assert.DoesNotContain(AppFixture.AppId, await _app.PreviewApps(_app.DeployerSession));
            // The admin is never filtered, which is what keeps a mis-grant repairable.
            Assert.Contains(AppFixture.AppId, await _app.PreviewApps(_app.AdminSession));
        }
        finally
        {
            await _app.Revoke(AppFixture.OtherTeam);
        }
    }

    [Fact]
    public async Task AClaimedAppStaysVisibleToItsOwnTeam()
    {
        await _app.Grant(AppFixture.Team);
        try
        {
            Assert.Contains(AppFixture.AppId, await _app.PreviewApps(_app.DeployerSession));
        }
        finally
        {
            await _app.Revoke(AppFixture.Team);
        }
    }

    /// <summary>
    /// The one that matters: hiding the row is not the point, refusing the action is.
    /// The preview directory still being on disk afterwards is what proves nothing
    /// was destroyed on the way to the refusal.
    /// </summary>
    [Fact]
    public async Task APreviewOfAnAppClaimedByAnotherTeamCannotBeTornDown()
    {
        await _app.Grant(AppFixture.OtherTeam);
        try
        {
            var (status, body) = await _app.Teardown(AppFixture.PullRequest, _app.DeployerSession);

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Contains($"Unknown app '{AppFixture.AppId}'", body, StringComparison.Ordinal);
            Assert.True(Directory.Exists(_app.PreviewDirectory), "the preview was torn down despite the refusal");
        }
        finally
        {
            await _app.Revoke(AppFixture.OtherTeam);
        }
    }

    /// <summary>
    /// An app the caller may see is torn down as it always was — the refusal above
    /// has to be about the grant and not about the route having stopped working.
    /// A pull request with nothing on disk keeps this from destroying the fixture.
    /// </summary>
    [Fact]
    public async Task APreviewOfAVisibleAppIsStillTornDown()
    {
        var (status, body) = await _app.Teardown(AppFixture.UnusedPullRequest, _app.DeployerSession);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain("Unknown app", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// What made this reachable was an optional argument: a call site that omits
    /// <c>canView</c> compiles, and the omission looks like every other call. One
    /// place supplies it, so nowhere else may call the resolver directly — the rule
    /// this asserts is the reason the gap cannot come back under a different name.
    /// </summary>
    [Fact]
    public void OnlyTheRequestHelperResolvesAnAppByHand()
    {
        var callers = Directory
            .EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("AppResolver.Resolve", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal(["EndpointHelpers.cs"], callers);
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pinqops.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src");
    }

    public sealed class AppFixture : WebApplicationFactory<Program>, IAsyncLifetime
    {
        public const string Team = "platform";
        public const string OtherTeam = "payments";
        public const string AppId = "acme-shop";
        public const int PullRequest = 7;

        /// <summary>A pull request with no directory, so tearing it down destroys nothing.</summary>
        public const int UnusedPullRequest = 99;

        private readonly string _directory;
        private readonly string _composeFile;

        public HttpClient Client { get; private set; } = null!;

        public string AdminSession { get; private set; } = "";

        public string DeployerSession { get; private set; } = "";

        public string PreviewDirectory => PreviewManager.PreviewDirectory(_composeFile, PullRequest);

        public AppFixture()
        {
            _directory = Path.Combine(Path.GetTempPath(), "pinqops-previews-" + Guid.NewGuid().ToString("N"));
            _composeFile = Path.Combine(_directory, "apps", AppId, "docker-compose.yml");
            Directory.CreateDirectory(_directory);
            Environment.SetEnvironmentVariable("PINQOPS_UI_CONFIG", Path.Combine(_directory, "ui.json"));
            Environment.SetEnvironmentVariable("PINQOPS_AUDIT_LOG", Path.Combine(_directory, "audit.jsonl"));

            new UiConfigStore(Path.Combine(_directory, "ui.json")).Update(config =>
            {
                config.Users.Add(Account("boss", "admin-password-01", UserRoles.Admin));
                config.Users.Add(Account("deployer1", "deploy-password-1", UserRoles.Deployer));
                config.Apps.Add(new AppConnection
                {
                    Id = AppId,
                    RepoUrl = "https://github.com/acme/shop",
                    ComposeFile = _composeFile,
                    RunnerDirectory = Path.Combine(_directory, "runners", AppId),
                });
            });

            EnsurePreview();
        }

        /// <summary>
        /// One preview on disk, which is what the listing enumerates and what
        /// teardown deletes.
        /// </summary>
        public void EnsurePreview()
        {
            Directory.CreateDirectory(PreviewDirectory);
            File.WriteAllText(Path.Combine(PreviewDirectory, "docker-compose.yml"), "services: {}\n");
            File.WriteAllText(Path.Combine(PreviewDirectory, ".env"), "PINQOPS_HOST_PORT=8107\n");
        }

        private static UserAccount Account(string username, string password, string role) => new()
        {
            Username = username,
            PasswordHash = PasswordHasher.Hash(password),
            Role = role,
        };

        public async Task InitializeAsync()
        {
            Client = CreateClient();
            AdminSession = await Login("boss", "admin-password-01");
            DeployerSession = await Login("deployer1", "deploy-password-1");

            await AsAdmin(HttpMethod.Post, "/api/teams", new { id = Team, name = Team });
            await AsAdmin(HttpMethod.Post, $"/api/teams/{Team}/members", new { principal = "deployer1" });
            await AsAdmin(HttpMethod.Post, "/api/teams", new { id = OtherTeam, name = OtherTeam });
            await AsAdmin(HttpMethod.Post, $"/api/teams/{OtherTeam}/members", new { principal = "someone-else" });
        }

        public Task Grant(string teamId) =>
            AsAdmin(HttpMethod.Post, "/api/grants", new
            {
                kind = ResourceKinds.App,
                environmentId = ManagedEnvironment.LocalId,
                resourceId = AppId,
                teamId,
                access = GrantAccess.Manage,
            });

        public Task Revoke(string teamId) =>
            AsAdmin(
                HttpMethod.Delete,
                $"/api/grants?kind={ResourceKinds.App}&env={ManagedEnvironment.LocalId}"
                + $"&resourceId={AppId}&teamId={teamId}",
                body: null);

        /// <summary>The app id of every row the preview listing returned.</summary>
        public async Task<IReadOnlyList<string>> PreviewApps(string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/previews");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            return [.. document.RootElement.GetProperty("items").EnumerateArray()
                .Select(row => row.GetProperty("appId").GetString() ?? string.Empty)];
        }

        public async Task<(HttpStatusCode Status, string Body)> Teardown(int pullRequest, string token)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"/api/previews/{AppId}/{pullRequest}/teardown");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Client.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        private async Task AsAdmin(HttpMethod method, string path, object? body)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminSession);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            using var response = await Client.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        private async Task<string> Login(string username, string password)
        {
            using var response = await Client.PostAsJsonAsync("/api/auth/login", new { username, password });
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            return document.RootElement.GetProperty("token").GetString()!;
        }

        public new async Task DisposeAsync()
        {
            await base.DisposeAsync();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
