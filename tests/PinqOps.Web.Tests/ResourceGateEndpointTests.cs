using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Tests.Web;

/// <summary>
/// The gate, through the real <c>Program</c>: a grant actually opens a governed
/// route, revoking it closes again, and neither ever widens what the scope policy
/// already decided.
/// </summary>
[Collection(TestServerCollection.Name)]
public sealed class ResourceGateEndpointTests : IClassFixture<ResourceGateEndpointTests.AppFixture>
{
    /// <summary>A container nobody owns — which, before teams, meant admin-only.</summary>
    private const string Container = "unowned-container";

    private static string Logs(string container) => $"/api/docker/containers/{container}/logs";

    private readonly AppFixture _app;

    public ResourceGateEndpointTests(AppFixture app) => _app = app;

    /// <summary>
    /// The behaviour that must not have changed: with no grant anywhere, a deployer
    /// still cannot reach a container it does not own.
    /// </summary>
    [Fact]
    public async Task WithoutAGrantADeployerIsStillRefused()
    {
        Assert.Equal(HttpStatusCode.Forbidden, await _app.Get(Logs("never-granted"), _app.DeployerSession));
    }

    [Fact]
    public async Task AnAdminIsNeverRefused()
    {
        Assert.NotEqual(HttpStatusCode.Forbidden, await _app.Get(Logs("never-granted"), _app.AdminSession));
    }

    /// <summary>
    /// The whole point of the change. Granting the container to a team the deployer
    /// is in opens the route; revoking it closes again, in the same process, with no
    /// restart.
    /// </summary>
    [Fact]
    public async Task AGrantOpensTheRouteAndRevokingItClosesAgain()
    {
        Assert.Equal(HttpStatusCode.Forbidden, await _app.Get(Logs(Container), _app.DeployerSession));

        await _app.GrantContainer(Container);
        // Admitted means the gate let it through — the handler still fails, because
        // there is no docker here, but it is no longer a 403.
        Assert.NotEqual(HttpStatusCode.Forbidden, await _app.Get(Logs(Container), _app.DeployerSession));

        await _app.RevokeContainer(Container);
        Assert.Equal(HttpStatusCode.Forbidden, await _app.Get(Logs(Container), _app.DeployerSession));
    }

    /// <summary>
    /// A grant names the environment as well as the resource, so one on the local
    /// host does not hand over a same-named container on another.
    /// </summary>
    [Fact]
    public async Task AGrantOnOneEnvironmentDoesNotOpenAnother()
    {
        const string Shared = "shared-name";
        await _app.GrantContainer(Shared);
        try
        {
            Assert.NotEqual(HttpStatusCode.Forbidden, await _app.Get(Logs(Shared), _app.DeployerSession));
            // An unknown environment is refused by the environment middleware before
            // the gate is reached, which is itself the guarantee being checked here:
            // there is no way to address another host and inherit this grant.
            Assert.Equal(
                HttpStatusCode.NotFound,
                await _app.Get(Logs(Shared) + "?env=staging", _app.DeployerSession));
        }
        finally
        {
            await _app.RevokeContainer(Shared);
        }
    }

    /// <summary>
    /// A grant cannot widen what stage one refused. The viewer's role stops it at
    /// the scope policy, before any team is consulted.
    /// </summary>
    [Fact]
    public async Task AGrantDoesNotLetAViewerPastTheScopePolicy()
    {
        const string Viewable = "viewer-granted";
        await _app.GrantContainer(Viewable, forViewer: true);
        try
        {
            Assert.Equal(HttpStatusCode.Forbidden, await _app.Get(Logs(Viewable), _app.ViewerSession));
        }
        finally
        {
            await _app.RevokeContainer(Viewable);
        }
    }

    /// <summary>
    /// A member of another team gets nothing from a grant that names neither of its
    /// teams.
    /// </summary>
    [Fact]
    public async Task AGrantToAnotherTeamDoesNothing()
    {
        const string Elsewhere = "other-teams-container";
        await _app.GrantContainer(Elsewhere, teamId: AppFixture.OtherTeam);
        try
        {
            Assert.Equal(HttpStatusCode.Forbidden, await _app.Get(Logs(Elsewhere), _app.DeployerSession));
        }
        finally
        {
            await _app.RevokeContainer(Elsewhere, AppFixture.OtherTeam);
        }
    }

    public sealed class AppFixture : WebApplicationFactory<Program>, IAsyncLifetime
    {
        public const string Team = "platform";
        public const string OtherTeam = "payments";

        private readonly string _directory;

        public HttpClient Client { get; private set; } = null!;

        public string AdminSession { get; private set; } = "";

        public string DeployerSession { get; private set; } = "";

        public string ViewerSession { get; private set; } = "";

        public AppFixture()
        {
            _directory = Path.Combine(Path.GetTempPath(), "pinqops-gate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Environment.SetEnvironmentVariable("PINQOPS_UI_CONFIG", Path.Combine(_directory, "ui.json"));
            Environment.SetEnvironmentVariable("PINQOPS_AUDIT_LOG", Path.Combine(_directory, "audit.jsonl"));

            new UiConfigStore(Path.Combine(_directory, "ui.json")).Update(config =>
            {
                config.Users.Add(Account("boss", "admin-password-01", UserRoles.Admin));
                config.Users.Add(Account("deployer1", "deploy-password-1", UserRoles.Deployer));
                config.Users.Add(Account("viewer1", "viewer-password-1", UserRoles.Viewer));
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
            ViewerSession = await Login("viewer1", "viewer-password-1");

            // Built through the API, so the test exercises the real path rather than
            // seeding a file the running store has already cached.
            await CreateTeam(Team, "deployer1");
            await CreateTeam(OtherTeam, "someone-else");
            await AddMember(Team, "viewer1");
        }

        private async Task CreateTeam(string id, string member)
        {
            await AsAdmin(HttpMethod.Post, "/api/teams", new { id, name = id });
            await AddMember(id, member);
        }

        private Task AddMember(string teamId, string principal) =>
            AsAdmin(HttpMethod.Post, $"/api/teams/{teamId}/members", new { principal, role = "member" });

        public Task GrantContainer(string container, bool forViewer = false, string teamId = Team) =>
            AsAdmin(HttpMethod.Post, "/api/grants", new
            {
                kind = ResourceKinds.Container,
                environmentId = ManagedEnvironment.LocalId,
                resourceId = container,
                teamId,
                access = forViewer ? GrantAccess.View : GrantAccess.Manage,
            });

        public Task RevokeContainer(string container, string teamId = Team) =>
            AsAdmin(
                HttpMethod.Delete,
                $"/api/grants?kind={ResourceKinds.Container}&env={ManagedEnvironment.LocalId}"
                + $"&resourceId={Uri.EscapeDataString(container)}&teamId={teamId}",
                body: null);

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

        public async Task<HttpStatusCode> Get(string path, string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Client.SendAsync(request);
            return response.StatusCode;
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
