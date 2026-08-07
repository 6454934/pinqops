using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Tests.Web;

/// <summary>
/// Drives the real Program through a test server and checks that every principal
/// gets the authorization the scope policies promise: admin-only routes refuse a
/// viewer, a deployer, and read/deploy tokens; deploy routes refuse a viewer; the
/// metadata-driven container ownership gate refuses a deployer a container it does
/// not own; and the fail-closed fallback refuses an unknown route.
/// </summary>
[Collection(TestServerCollection.Name)]
public sealed class AuthorizationEndpointTests : IClassFixture<AuthorizationEndpointTests.AppFixture>
{
    private readonly AppFixture _app;

    public AuthorizationEndpointTests(AppFixture app) => _app = app;

    // ---- admin-only routes: 403 for anyone below admin -----------------------

    public static IEnumerable<object[]> AdminOnlyRoutes() => new[]
    {
        new object[] { "GET", "/api/users" },
        new object[] { "GET", "/api/audit" },
        new object[] { "GET", "/api/audit/verify" },
        new object[] { "GET", "/api/tokens" },
        new object[] { "GET", "/api/notifications" },
        new object[] { "GET", "/api/alerts/channels" },
        new object[] { "GET", "/api/runner/logs?unit=x" },
        new object[] { "GET", "/api/apps/redis/credentials" },
        new object[] { "POST", "/api/tokens" },
        new object[] { "POST", "/api/settings" },
        new object[] { "POST", "/api/users" },
        new object[] { "POST", "/api/docker/prune" },
        new object[] { "POST", "/api/docker/containers/some-name/exec" },
        new object[] { "POST", "/api/backups/restore" },
        new object[] { "POST", "/api/alerts/rules" },
        // Granting is how access is widened, so a principal that is not an admin
        // must not be able to grant itself anything — including a deploy token,
        // which the coarse write default would otherwise be the only thing stopping.
        new object[] { "POST", "/api/teams" },
        new object[] { "DELETE", "/api/teams/platform" },
        new object[] { "POST", "/api/teams/platform/members" },
        new object[] { "POST", "/api/grants" },
        new object[] { "DELETE", "/api/grants?kind=app&resourceId=shop&teamId=platform" },
    };

    [Theory]
    [MemberData(nameof(AdminOnlyRoutes))]
    public async Task AdminOnly_Refuses_Viewer_Deployer_And_LowTokens(string method, string path)
    {
        Assert.Equal(HttpStatusCode.Forbidden, await Status(method, path, _app.ViewerSession));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(method, path, _app.DeployerSession));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(method, path, _app.ReadToken));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(method, path, _app.DeployToken));
    }

    [Theory]
    [MemberData(nameof(AdminOnlyRoutes))]
    public async Task AdminOnly_Admits_Admin_And_AdminToken(string method, string path)
    {
        // Admitted means the authorization layer let it through — the handler may
        // still 400/404/500 (no docker, a required body, a made-up id), but never 403.
        Assert.NotEqual(HttpStatusCode.Forbidden, await Status(method, path, _app.AdminSession));
        Assert.NotEqual(HttpStatusCode.Forbidden, await Status(method, path, _app.AdminToken));
    }

    // ---- deploy routes: viewer refused, deployer admitted --------------------

    [Theory]
    [InlineData("POST", "/api/deploy/rollback")]
    [InlineData("POST", "/api/apps/redis/install")]
    [InlineData("POST", "/api/setup/create-workflow")]
    [InlineData("POST", "/api/backups/run/db-x")]
    public async Task Deploy_Refuses_Viewer_And_ReadToken(string method, string path)
    {
        Assert.Equal(HttpStatusCode.Forbidden, await Status(method, path, _app.ViewerSession));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(method, path, _app.ReadToken));
    }

    [Theory]
    [InlineData("POST", "/api/deploy/rollback")]
    [InlineData("POST", "/api/setup/create-workflow")]
    public async Task Deploy_Admits_Deployer_And_Admin(string method, string path)
    {
        Assert.NotEqual(HttpStatusCode.Forbidden, await Status(method, path, _app.DeployerSession));
        Assert.NotEqual(HttpStatusCode.Forbidden, await Status(method, path, _app.DeployToken));
        Assert.NotEqual(HttpStatusCode.Forbidden, await Status(method, path, _app.AdminSession));
    }

    // ---- read routes: any authenticated principal admitted -------------------

    [Theory]
    [InlineData("/api/me")]
    [InlineData("/api/settings")]
    [InlineData("/api/alerts")]
    [InlineData("/api/environments")]
    // The dashboard needs these to know what to offer, so they are read-scoped and
    // the handlers strip what the caller may not see.
    [InlineData("/api/teams")]
    [InlineData("/api/grants")]
    public async Task Read_Admits_Viewer(string path)
    {
        Assert.Equal(HttpStatusCode.OK, await Status("GET", path, _app.ViewerSession));
        Assert.Equal(HttpStatusCode.OK, await Status("GET", path, _app.ReadToken));
    }

    // ---- container ownership gate (metadata, not path matching) --------------

    [Fact]
    public async Task ContainerLogs_ScopeThenOwnership()
    {
        const string path = "/api/docker/containers/unowned-container/logs";
        // viewer is below the deploy scope the route needs -> refused by the policy.
        Assert.Equal(HttpStatusCode.Forbidden, await Status("GET", path, _app.ViewerSession));
        // deployer clears the scope but owns nothing -> refused by the ownership gate.
        Assert.Equal(HttpStatusCode.Forbidden, await Status("GET", path, _app.DeployerSession));
        // admin manages everything -> ownership passes (the handler then fails on
        // the absent docker daemon, which is not a 403).
        Assert.NotEqual(HttpStatusCode.Forbidden, await Status("GET", path, _app.AdminSession));
    }

    // ---- anonymous + unauthenticated + fail-closed fallback ------------------

    [Fact]
    public async Task Anonymous_Handshake_NeedsNoToken()
    {
        Assert.Equal(HttpStatusCode.OK, await Status("GET", "/api/auth/state", null));
        Assert.Equal(HttpStatusCode.OK, await Status("GET", "/", null));
    }

    [Fact]
    public async Task Protected_WithoutToken_Is401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, await Status("GET", "/api/me", null));
        Assert.Equal(HttpStatusCode.Unauthorized, await Status("GET", "/api/users", null));
    }

    [Fact]
    public async Task InvalidToken_Is401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, await Status("GET", "/api/me", "pot_not_a_real_token"));
        Assert.Equal(HttpStatusCode.Unauthorized, await Status("GET", "/api/me", "not-even-token-shaped"));
    }

    [Fact]
    public async Task UnknownRoute_FailsClosed_NotServed()
    {
        // The deny-all fallback policy applies to anything without its own policy:
        // an unauthenticated caller gets 401, an authenticated one 403 — never 200.
        var anon = await Status("GET", "/api/this-route-does-not-exist", null);
        var admin = await Status("GET", "/api/this-route-does-not-exist", _app.AdminSession);
        Assert.Equal(HttpStatusCode.Unauthorized, anon);
        Assert.Equal(HttpStatusCode.Forbidden, admin);
    }

    // ---- no endpoint loosened or tightened -----------------------------------

    private static readonly HashSet<(string, string)> Anonymous = new()
    {
        ("GET", "/api/auth/state"), ("POST", "/api/auth/setup"), ("POST", "/api/auth/login"),
        // The second step of a login, which by definition runs before there is a
        // session. It takes a challenge a correct password minted, not a credential
        // of its own.
        ("POST", "/api/auth/login/2fa"),
        // Accepting an invitation happens before the account exists.
        ("GET", "/api/auth/invite"),
        ("POST", "/api/auth/invite/accept"),
    };

    [Fact]
    public void EveryEndpoint_DeclaredPolicy_MatchesTheOldRequiredScope()
    {
        var source = _app.Services.GetRequiredService<EndpointDataSource>();
        var rows = new List<string>();
        var mismatches = new List<string>();

        foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>())
        {
            var template = "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/');
            if (template != "/" && !template.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var method = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "GET";

            string declared;
            if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            {
                declared = "anonymous";
            }
            else
            {
                declared = endpoint.Metadata.GetMetadata<IAuthorizeData>()?.Policy switch
                {
                    ApiAuthorization.AdminPolicy => "admin",
                    ApiAuthorization.DeployPolicy => "deploy",
                    ApiAuthorization.ReadPolicy => "read",
                    _ => "(none)",
                };
            }

            var expected = template == "/" || Anonymous.Contains((method, template))
                ? "anonymous"
                : ApiScopes.RequiredFor(method, template);

            rows.Add($"{method,-6} {template,-52} {declared}");
            if (declared != expected)
            {
                mismatches.Add($"{method} {template}: declared '{declared}', old required '{expected}'");
            }
        }

        rows.Sort(StringComparer.Ordinal);
        var scratch = Environment.GetEnvironmentVariable("PINQOPS_AUTHZ_TABLE_OUT");
        if (scratch is not null)
        {
            File.WriteAllLines(scratch, rows);
        }

        Assert.Empty(mismatches);
        // 142 /api routes + the anonymous dashboard root. The count is the point:
        // a new endpoint has to be added here deliberately, so one cannot appear
        // without someone having looked at what scope it landed on.
        //
        // 122 -> 127: the secret vault added GET/POST /api/secrets,
        // POST /api/secrets/{scope}/{name}/rotate, GET .../reveal and
        // DELETE /api/secrets/{scope}/{name}. All five are admin — the writes by
        // the coarse default, the reads because ApiScopes' admin-read table lists
        // the whole family, which also puts every reveal in the audit log.
        //
        // 127 -> 128: GET /api/ws/ping, the WebSocket connectivity diagnostic.
        // Admin, classified per route rather than as an /api/ws/ prefix — the
        // WebSocket routes that follow are not all admin.
        //
        // 128 -> 136: teams and grants. Every write is admin by the coarse default
        // and stays there deliberately — granting is how access is widened, so a
        // non-admin must not be able to grant itself anything. The two reads stay at
        // "read" and their handlers strip what the caller may not see, the same
        // stance /api/docker/ownership already takes.
        //
        // 136 -> 137: POST /api/domains/{domain}/settings, the response headers and
        // rate limit for a domain. Admin, like every other /api/domains write —
        // separate from POST /api/domains because changing a header must not
        // re-resolve the app or risk repointing the route.
        //
        // 137 -> 139: POST and DELETE /api/domains/{domain}/dns, which write and
        // remove the address record that makes a domain resolve here. Admin, like
        // every other /api/domains write. Separate from the DNS preflight, which has
        // always worked without a provider and still does.
        //
        // 139 -> 140: POST /api/proxy/edge, which turns running-behind-a-CDN on and
        // refetches the CDN's address ranges. Admin, like every other proxy write.
        //
        // 140 -> 143: POST /api/proxy/enroll, /unenroll and /republish — handing an
        // app's host port to the proxy, taking it back, and making a running proxy's
        // published ports match the config again. Admin, like every other proxy write.
        //
        // 143 -> 145: GET and POST /api/deploy/readiness, the HTTP check that has to
        // answer before a deploy is called a success. The read is "read"; the write
        // is admin rather than deploy, because turning it off disables a gate that
        // stands between a broken release and a green deploy — the same reasoning
        // that keeps /api/alerts/ writes admin-only.
        //
        // 145 -> 147: GET and POST /api/deploy/scale, how many copies of an app run
        // and how the proxy spreads requests between them. Admin for the write: it
        // repoints live routes and changes how much of the server the app uses.
        //
        // 147 -> 149: GET and POST /api/deploy/bluegreen, whether a release starts
        // the new version alongside the old one instead of replacing it. Admin: it
        // doubles what the app costs to run and changes what a rollback does.
        //
        // 149 -> 155: the scheduled jobs — list, run history, create, edit, delete
        // and run-now. Admin throughout, reads included: a job is an arbitrary
        // command in a container of the operator's choosing, and its definition
        // says more about the infrastructure than the container list does.
        //
        // 155 -> 161: the image lifecycle — inspect, history, pull (start and poll),
        // tag and remove. The two reads are admin because an image's inspect carries
        // its build-time environment and its history carries the Dockerfile commands
        // verbatim; the writes are admin like every other docker write that is not a
        // container action.
        //
        // 161 -> 167: volumes — inspect, browse, download a file, create, remove and
        // prune. The three reads are admin because what is inside a volume is the
        // application's own data; the listing itself stays at "read", which is what
        // the Storage page shows.
        //
        // 167 -> 171: the private registries — list, add, sign in and remove. Admin
        // throughout, reads included: the list says which registries this server
        // holds credentials for and under which account.
        //
        // 171 -> 173: GET and POST /api/docker/images/updates — what the hourly
        // registry check found, and a way to run it now. Admin like the rest of the
        // image family: the answer names every image running on this server.
        //
        // 173 -> 174: GET /api/ws/containers/{id}/console, a line-oriented shell in
        // a running container. Admin, and gated on the container like every other
        // per-container route.
        //
        // 174 -> 180: hand-written compose stacks — list, read, save, up, down, pull
        // and remove. Admin throughout: a stack file is arbitrary containers,
        // arbitrary bind mounts and arbitrary published ports on this server.
        //
        // 181 -> 185: offsite backup copies — read and write the bucket settings,
        // list a target's copies, and fetch one back. The settings are admin to read
        // as well as to write: they say where every backup on this server is copied
        // to and under whose account.
        //
        // 185 -> 188: the managed databases — what is installed, what it connects
        // with, and moving one to a newer version. Admin including the reads: a
        // connection string is a credential.
        //
        // 188 -> 193: object storage buckets — list, create, delete, browse one, and
        // mint a link to an object. Admin throughout: a presigned link is a
        // credential in a URL.
        //
        // 193 -> 197: point-in-time recovery for PostgreSQL — read the window and
        // the settings, turn it on, take a base backup, and plan a recovery. Admin
        // throughout: this is the archive an entire database can be rebuilt from.
        //
        // 197 -> 200: collected container logs — what is kept and what it costs,
        // changing it, and searching the archive. Admin: a container's output is
        // whatever the application printed.
        //
        // 200 -> 202: the traffic summary and its switch. Admin: it names every
        // domain this server answers for and every route within them.
        //
        // 202 -> 206: the mail relay — reading it, changing it, sending a test
        // through it, and working out the DNS records that decide whether what it
        // sends arrives. Admin: the settings name the relay host and the account
        // this server signs in to it as.
        //
        // 206 -> 213: two-factor. The second step of a login (anonymous — it takes
        // a challenge a correct password minted), and the six that manage it. Five
        // of those are self-service at "read": a second factor belongs to the
        // account rather than to the role, so a viewer has to be able to protect
        // their own login. Requiring it org-wide is admin.
        //
        // 213 -> 218: invitations. Three admin routes to send, list and withdraw
        // one, and two anonymous ones the invitee uses — accepting happens before
        // the account exists, so it cannot be behind one.
        //
        // 218 -> 219: POST /api/proxy/ports/release, the exit for a port entry
        // whose target app was removed from the dashboard — both enroll routes
        // resolve an app, so a stale entry had no other way out. Admin, like every
        // other proxy write.
        //
        // 219 -> 223: per-domain TLS — status (read), CSR, custom upload and
        // revert to ACME. Admin throughout: a certificate and its private key are
        // credentials for the host.
        //
        // 223 -> 224: GET /api/domains/provision/{jobId} — poll a server-minted
        // HTTPS provision job (phase / result), same shape as install/deploy jobs.
        Assert.Equal(224, rows.Count);
    }

    // ---- the resource gate is declared as route metadata --------------------

    /// <summary>
    /// Exactly which routes are gated, on what kind of resource, where they get its
    /// id, and at what access. Strictly more than the template list this replaced:
    /// a route that keeps its gate but quietly changes what it governs — or drops
    /// from manage to view — now fails here too.
    /// </summary>
    [Fact]
    public void ExactlyTheGovernedRoutes_CarryResourceGateMetadata()
    {
        var governed = _app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => (Endpoint: endpoint, Gate: endpoint.Metadata.GetMetadata<ResourceGateMetadata>()))
            .Where(entry => entry.Gate is not null)
            .Select(entry =>
                $"{Template(entry.Endpoint)} {entry.Gate!.Kind} {entry.Gate.Source} {entry.Gate.Access}")
            .OrderBy(row => row, StringComparer.Ordinal)
            .ToList();

        var expected = new[]
        {
            "/api/apps/{id}/uninstall container CatalogAppRouteId manage",
            "/api/docker/containers/{id}/action container RouteId manage",
            "/api/docker/containers/{id}/commit container RouteId manage",
            "/api/docker/containers/{id}/exec container RouteId manage",
            "/api/docker/containers/{id}/inspect container RouteId manage",
            "/api/docker/containers/{id}/logs container RouteId manage",
            "/api/docker/containers/{id}/owner container RouteId manage",
            "/api/docker/containers/{id}/remove container RouteId manage",
            "/api/docker/containers/{id}/rename container RouteId manage",
            "/api/docker/containers/{id}/restart-policy container RouteId manage",
            "/api/docker/containers/{id}/top container RouteId manage",
            // Twice at manage: the save and the delete share a template.
            "/api/stacks/{id} stack RouteId manage",
            "/api/stacks/{id} stack RouteId manage",
            "/api/stacks/{id} stack RouteId view",
            "/api/stacks/{id}/down stack RouteId manage",
            "/api/stacks/{id}/pull stack RouteId manage",
            "/api/stacks/{id}/up stack RouteId manage",
            "/api/ws/containers/{id}/console container RouteId manage",
        };

        Assert.Equal(expected, governed);
    }

    /// <summary>
    /// Routes that a non-admin can reach and that name a particular resource must
    /// either be gated or be written down here with a reason.
    ///
    /// <para>Admin-only routes are outside this: an admin passes every resource
    /// gate by construction, so adding one to an admin-only route governs nothing.
    /// What matters is the routes a viewer or a deployer can reach while naming one
    /// resource out of many — those are where an ungated route means reaching
    /// somebody else's.</para>
    ///
    /// <para>The point is not the list; it is that every exception has to be typed
    /// out with a reason before the build goes green.</para>
    /// </summary>
    [Fact]
    public void EveryRouteThatNamesAResource_IsGatedOrExplicitlyNot()
    {
        var ungoverned = _app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<ResourceGateMetadata>() is null)
            .Where(endpoint => Template(endpoint).Contains('{', StringComparison.Ordinal))
            .Where(endpoint => Method(endpoint) is not null)
            .Select(endpoint => $"{Method(endpoint)} {Template(endpoint)}")
            .Where(row => !row.StartsWith("GET /api/ws/", StringComparison.Ordinal))
            .Where(row => ApiScopes.RequiredFor(row.Split(' ')[0], row.Split(' ')[1]) != "admin")
            .OrderBy(row => row, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(IntentionallyUngoverned, ungoverned);
    }

    /// <summary>
    /// Non-admin routes that name a resource and are deliberately not gated. Each
    /// line is a decision, not an oversight.
    /// </summary>
    private static readonly string[] IntentionallyUngoverned =
    [
        // Ordinal order, which puts every GET before every POST.

        // The id is 8 random bytes minted by the server for one install, and the
        // response is a phase and that install's own output. Nothing about it names
        // a resource the caller could otherwise not reach.
        "GET /api/apps/install/{jobId}",

        // The offsite copies of one target: the same listing as its local snapshots
        // and narrowed the same way, by the target existing only in a config an
        // admin writes. It belongs with the work that gives backups their own
        // resource treatment, not ahead of it.
        "GET /api/backups/{targetId}/offsite",

        // Same shape as the install job: a server-minted id, and a log of the
        // rollback the caller just started.
        "GET /api/deploy/job/{jobId}",

        // And again: 8 random bytes minted for one pull, answering with that pull's
        // phase and output. Starting a pull is admin; watching one you were handed
        // the id for names nothing the caller could not otherwise reach.
        "GET /api/docker/images/pull/{jobId}",

        // A docker network is not a resource kind pinqops grants; inspecting one
        // returns its subnet and which containers are attached, which the container
        // listing already shows.
        "GET /api/docker/networks/{name}/inspect",

        // Server-minted provision job id; response is that job's phase and result.
        // Starting provision is the same admin-gated domain write the caller already did.
        "GET /api/domains/provision/{jobId}",

        // Running a backup is already narrowed by the target existing in a config
        // only an admin can write. The backupTarget kind is declared and the
        // listing is filtered; gating the run belongs with the work that gives
        // backups their own resource treatment.
        "POST /api/backups/run/{id}",

        // Tearing down a preview is scoped by {appId}, and an app the caller cannot
        // see does not resolve — AppResolver refuses it as unknown. The gate would
        // duplicate that.
        "POST /api/previews/{appId}/{pr:int}/teardown",
    ];

    private static string Template(RouteEndpoint endpoint) =>
        "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/');

    private static string? Method(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.FirstOrDefault();

    private async Task<HttpStatusCode> Status(string method, string path, string? bearer)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (method is "POST" or "PUT")
        {
            request.Content = JsonContent.Create(new { });
        }

        using var response = await _app.Client.SendAsync(request);
        return response.StatusCode;
    }

    public sealed class AppFixture : WebApplicationFactory<Program>, IAsyncLifetime
    {
        private readonly string _dir;

        public HttpClient Client { get; private set; } = null!;
        public string AdminSession { get; private set; } = "";
        public string ViewerSession { get; private set; } = "";
        public string DeployerSession { get; private set; } = "";
        public string ReadToken { get; }
        public string DeployToken { get; }
        public string AdminToken { get; }

        public AppFixture()
        {
            _dir = Path.Combine(Path.GetTempPath(), "pinqops-authz-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Environment.SetEnvironmentVariable("PINQOPS_UI_CONFIG", Path.Combine(_dir, "ui.json"));
            Environment.SetEnvironmentVariable("PINQOPS_AUDIT_LOG", Path.Combine(_dir, "audit.jsonl"));

            var store = new UiConfigStore(Path.Combine(_dir, "ui.json"));
            store.Update(config =>
            {
                config.Users.Add(Account("boss", "admin-password-01", UserRoles.Admin));
                config.Users.Add(Account("viewer1", "viewer-password-1", UserRoles.Viewer));
                config.Users.Add(Account("deployer1", "deploy-password-1", UserRoles.Deployer));
            });

            var tokens = new ApiTokenStore(Path.Combine(_dir, "tokens.json"));
            ReadToken = tokens.Create("read-tok", "read", DateTimeOffset.UtcNow).Plaintext;
            DeployToken = tokens.Create("deploy-tok", "deploy", DateTimeOffset.UtcNow).Plaintext;
            AdminToken = tokens.Create("admin-tok", "admin", DateTimeOffset.UtcNow).Plaintext;
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
            ViewerSession = await Login("viewer1", "viewer-password-1");
            DeployerSession = await Login("deployer1", "deploy-password-1");
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
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
