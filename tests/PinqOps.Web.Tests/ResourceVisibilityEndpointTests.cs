using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PinqOps.Backups;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Tests.Web;

/// <summary>
/// The listings, through the real <c>Program</c>.
///
/// <para>An endpoint filter guards one resource; a listing returns a set, so each
/// of these handlers has to filter its own rows and no static assertion can prove
/// they do. These drive the three that can be reached without a docker daemon —
/// <c>/api/environments</c>, the app list in <c>/api/settings</c>, and
/// <c>/api/backups</c>. The container listing and the domain listing use the same
/// <see cref="ResourceVisibility"/> and are covered by its own tests; reaching them
/// end to end needs a live daemon and the proxy's fixed <c>/opt/pinqops/proxy</c>
/// directory respectively, neither of which exists here.</para>
/// </summary>
[Collection(TestServerCollection.Name)]
public sealed class ResourceVisibilityEndpointTests : IClassFixture<ResourceVisibilityEndpointTests.AppFixture>
{
    private readonly AppFixture _app;

    public ResourceVisibilityEndpointTests(AppFixture app) => _app = app;

    /// <summary>
    /// The property that makes teams safe to ship: with nothing granted, every
    /// listing looks exactly as it did before teams existed.
    /// </summary>
    [Theory]
    [InlineData("/api/environments", "items")]
    [InlineData("/api/settings", "apps")]
    [InlineData("/api/backups", "items")]
    public async Task WithNothingClaimedADeployerSeesTheSameRowsAsAnAdmin(string path, string property)
    {
        var forAdmin = await _app.Rows(path, property, _app.AdminSession);
        var forDeployer = await _app.Rows(path, property, _app.DeployerSession);

        Assert.NotEmpty(forAdmin);
        Assert.Equal(forAdmin, forDeployer);
    }

    [Theory]
    [InlineData("/api/environments", "items", ResourceKinds.Environment, "local")]
    [InlineData("/api/settings", "apps", ResourceKinds.App, AppFixture.AppId)]
    [InlineData("/api/backups", "items", ResourceKinds.BackupTarget, AppFixture.BackupTargetId)]
    public async Task ClaimingARowHidesItFromEveryoneOutsideTheTeam(
        string path, string property, string kind, string resourceId)
    {
        await _app.Grant(kind, resourceId, AppFixture.OtherTeam);
        try
        {
            Assert.DoesNotContain(resourceId, await _app.Rows(path, property, _app.DeployerSession));
            // The admin is never filtered, which is what keeps a mis-grant repairable.
            Assert.Contains(resourceId, await _app.Rows(path, property, _app.AdminSession));
        }
        finally
        {
            await _app.Revoke(kind, resourceId, AppFixture.OtherTeam);
        }

        Assert.Contains(resourceId, await _app.Rows(path, property, _app.DeployerSession));
    }

    [Theory]
    [InlineData("/api/environments", "items", ResourceKinds.Environment, "local")]
    [InlineData("/api/settings", "apps", ResourceKinds.App, AppFixture.AppId)]
    [InlineData("/api/backups", "items", ResourceKinds.BackupTarget, AppFixture.BackupTargetId)]
    public async Task AClaimedRowStaysVisibleToItsOwnTeam(
        string path, string property, string kind, string resourceId)
    {
        await _app.Grant(kind, resourceId, AppFixture.Team);
        try
        {
            Assert.Contains(resourceId, await _app.Rows(path, property, _app.DeployerSession));
        }
        finally
        {
            await _app.Revoke(kind, resourceId, AppFixture.Team);
        }
    }

    /// <summary>
    /// Hiding an environment from the switcher is not the same as taking it away.
    ///
    /// <para>The grant filtered the listing and stopped there, so the host stayed
    /// fully addressable: anyone who knew the id could put it in <c>?env=</c> and
    /// operate on it. That inverts what granting an environment to a team is for —
    /// the row is the least of what it is meant to control.</para>
    /// </summary>
    [Fact]
    public async Task AnEnvironmentClaimedByAnotherTeamCannotBeAddressed()
    {
        await _app.Grant(ResourceKinds.Environment, "local", AppFixture.OtherTeam);
        try
        {
            Assert.Equal(
                HttpStatusCode.NotFound,
                await _app.Status("/api/me?env=local", _app.DeployerSession));

            // The admin is never filtered, which is what keeps a mis-grant repairable.
            Assert.Equal(HttpStatusCode.OK, await _app.Status("/api/me?env=local", _app.AdminSession));
        }
        finally
        {
            await _app.Revoke(ResourceKinds.Environment, "local", AppFixture.OtherTeam);
        }

        Assert.Equal(HttpStatusCode.OK, await _app.Status("/api/me?env=local", _app.DeployerSession));
    }

    /// <summary>
    /// A request that names no environment is asking for this server's own daemon,
    /// which is what the whole dashboard runs on. Refusing that would lock someone
    /// out of the product rather than out of a host, and it is not what granting a
    /// remote environment means — so the gate applies only to a request that names
    /// one.
    /// </summary>
    [Fact]
    public async Task NamingNoEnvironmentIsStillTheServersOwn()
    {
        await _app.Grant(ResourceKinds.Environment, "local", AppFixture.OtherTeam);
        try
        {
            Assert.Equal(HttpStatusCode.OK, await _app.Status("/api/me", _app.DeployerSession));
        }
        finally
        {
            await _app.Revoke(ResourceKinds.Environment, "local", AppFixture.OtherTeam);
        }
    }

    /// <summary>An environment nobody has claimed stays addressable by everyone, as it always was.</summary>
    [Fact]
    public async Task AnUnclaimedEnvironmentIsAddressable() =>
        Assert.Equal(HttpStatusCode.OK, await _app.Status("/api/me?env=local", _app.DeployerSession));

    /// <summary>
    /// An id that does not exist and one the caller may not use answer the same
    /// way, so the refusal does not disclose which it was.
    /// </summary>
    [Fact]
    public async Task AnUnknownEnvironmentIsIndistinguishableFromAForbiddenOne()
    {
        var unknown = await _app.Body("/api/me?env=does-not-exist", _app.DeployerSession);

        await _app.Grant(ResourceKinds.Environment, "local", AppFixture.OtherTeam);
        try
        {
            var forbidden = await _app.Body("/api/me?env=local", _app.DeployerSession);

            Assert.Equal(
                unknown.Replace("does-not-exist", "local", StringComparison.Ordinal),
                forbidden);
        }
        finally
        {
            await _app.Revoke(ResourceKinds.Environment, "local", AppFixture.OtherTeam);
        }
    }

    public sealed class AppFixture : WebApplicationFactory<Program>, IAsyncLifetime
    {
        public const string Team = "platform";
        public const string OtherTeam = "payments";
        public const string AppId = "acme-shop";
        public const string BackupTargetId = "vol-data";

        private readonly string _directory;

        public HttpClient Client { get; private set; } = null!;

        public string AdminSession { get; private set; } = "";

        public string DeployerSession { get; private set; } = "";

        public AppFixture()
        {
            _directory = Path.Combine(Path.GetTempPath(), "pinqops-visible-" + Guid.NewGuid().ToString("N"));
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
                    ComposeFile = Path.Combine(_directory, "apps", AppId, "docker-compose.yml"),
                    RunnerDirectory = Path.Combine(_directory, "runners", AppId),
                });
            });

            new BackupConfigStore(Path.Combine(_directory, "backups.json")).Save(new BackupConfig
            {
                Targets =
                [
                    new BackupTarget
                    {
                        Id = BackupTargetId,
                        Kind = "volume",
                        Name = "data",
                        Engine = "volume",
                        Schedule = "daily",
                    },
                ],
            });
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

        public Task Grant(string kind, string resourceId, string teamId) =>
            AsAdmin(HttpMethod.Post, "/api/grants", new
            {
                kind,
                environmentId = ManagedEnvironment.LocalId,
                resourceId,
                teamId,
                access = GrantAccess.Manage,
            });

        public Task Revoke(string kind, string resourceId, string teamId) =>
            AsAdmin(
                HttpMethod.Delete,
                $"/api/grants?kind={kind}&env={ManagedEnvironment.LocalId}"
                + $"&resourceId={Uri.EscapeDataString(resourceId)}&teamId={teamId}",
                body: null);

        /// <summary>
        /// The <c>id</c> of every row a listing returned. Comparing ids rather than
        /// whole objects keeps these tests about who sees what, not about the shape
        /// of each payload.
        /// </summary>
        public async Task<IReadOnlyList<string>> Rows(string path, string property, string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            return [.. document.RootElement.GetProperty(property).EnumerateArray()
                .Select(row => row.GetProperty("id").GetString() ?? string.Empty)];
        }

        /// <summary>The status of a plain GET, for the answers that are the point.</summary>
        public async Task<HttpStatusCode> Status(string path, string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Client.SendAsync(request);
            return response.StatusCode;
        }

        /// <summary>The raw body, for comparing one refusal against another.</summary>
        public async Task<string> Body(string path, string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Client.SendAsync(request);
            return await response.Content.ReadAsStringAsync();
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
