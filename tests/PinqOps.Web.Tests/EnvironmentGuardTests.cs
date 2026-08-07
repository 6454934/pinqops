using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Tests.Web;

/// <summary>
/// The environment gate, through the real <c>Program</c>.
///
/// <para>Two answers are the whole subject: a host pinned read-only refuses
/// everything that changes it, and a route that can only ever act on this server
/// refuses to be aimed at another one. Both are decided in the middleware, before
/// any handler runs, so both are observable from an ordinary request — which is
/// what these drive.</para>
/// </summary>
[Collection(TestServerCollection.Name)]
public sealed class EnvironmentGuardTests : IClassFixture<EnvironmentGuardTests.AppFixture>
{
    /// <summary>The console for a container that need not exist: the gate answers first.</summary>
    private const string ConsolePath = "/api/ws/containers/web/console";

    private const string StacksPath = "/api/stacks";

    /// <summary>A family that has always been refused a remote host, to compare against.</summary>
    private const string LocalOnlyFamilyPath = "/api/backups";

    private readonly AppFixture _app;

    public EnvironmentGuardTests(AppFixture app) => _app = app;

    /// <summary>
    /// Pinning a host read-only has to close the container console on it.
    ///
    /// <para>The console is a shell inside a container on that host: every line
    /// typed into it runs there, which is the definition of a change. Because
    /// opening one is a GET — a socket handshake rather than a POST — it read as
    /// harmless and was let through, while <c>POST
    /// /api/docker/containers/{id}/exec</c>, the same capability in one-shot form,
    /// was refused. An operator who pinned production read-only got exactly half of
    /// what they asked for, and the half they lost was the interactive one.</para>
    /// </summary>
    [Fact]
    public async Task AConsoleOnAReadOnlyHostIsRefused() =>
        Assert.Equal(
            HttpStatusCode.Forbidden,
            await _app.Status($"{ConsolePath}?env={AppFixture.ReadOnlyEnvironmentId}", _app.AdminSession));

    /// <summary>
    /// A grant recorded against a host has to be revocable wherever that host is.
    ///
    /// <para>The revoke named the host in <c>env</c> — the same parameter the gate
    /// reads as "aim this request at that host". So revoking a grant on a host pinned
    /// read-only was refused as a change to that host, and one on a host since
    /// de-registered was refused as unknown, while creating either was never gated
    /// that way. An admin could hand out access they could not take back.</para>
    /// </summary>
    [Theory]
    [InlineData(AppFixture.ReadOnlyEnvironmentId)]
    [InlineData("de-registered-last-year")]
    public async Task AGrantIsRevocableWhereverItsHostIs(string environmentId)
    {
        await _app.Send(HttpMethod.Post, "/api/teams", _app.AdminSession, new { id = "platform", name = "Platform" });
        var grant = new
        {
            kind = "container",
            environmentId,
            resourceId = "payments-api",
            teamId = "platform",
            access = "manage",
        };

        Assert.Equal(HttpStatusCode.OK, await _app.Send(HttpMethod.Post, "/api/grants", _app.AdminSession, grant));
        Assert.Equal(
            HttpStatusCode.OK,
            await _app.Send(
                HttpMethod.Delete,
                $"/api/grants?kind=container&environmentId={environmentId}&resourceId=payments-api&teamId=platform",
                _app.AdminSession));
    }

    /// <summary>
    /// The listing had the same collision, so a grant on a de-registered host could
    /// not even be found before it could not be revoked.
    /// </summary>
    [Fact]
    public async Task GrantsOnAHostThatIsGoneCanStillBeListed() =>
        Assert.Equal(
            HttpStatusCode.OK,
            await _app.Status("/api/grants?environmentId=de-registered-last-year", _app.AdminSession));

    /// <summary>
    /// Only the read-only pin closes the console, so the console is not lost on every
    /// other host. The 400 is the endpoint itself saying "this is a WebSocket", which
    /// only a caller the gate admitted ever gets to see.
    /// </summary>
    [Fact]
    public async Task AConsoleOnAWritableHostStillOpens() =>
        Assert.Equal(
            HttpStatusCode.BadRequest,
            await _app.Status($"{ConsolePath}?env={AppFixture.WritableEnvironmentId}", _app.AdminSession));

    /// <summary>
    /// A stack request that names another host must be refused, not quietly served
    /// from this one.
    ///
    /// <para>Stacks are this server's own files driven by this server's own daemon,
    /// but every stack route authorizes against the grants recorded for
    /// <c>?env=</c>. Naming a remote host checked that host's grants and then handed
    /// back the local stack — its compose file and its dotenv, secrets and all —
    /// while a write tore down the local project and left an audit line naming the
    /// host it never touched.</para>
    /// </summary>
    [Fact]
    public async Task AStackRequestNamingAnotherHostIsRefused()
    {
        var stacksElsewhere = $"{StacksPath}?env={AppFixture.WritableEnvironmentId}";

        Assert.Equal(HttpStatusCode.BadRequest, await _app.Status(stacksElsewhere, _app.AdminSession));
        // Word for word what the other server-only families answer, so an operator
        // reads one refusal rather than two.
        Assert.Equal(
            await _app.Body($"{LocalOnlyFamilyPath}?env={AppFixture.WritableEnvironmentId}", _app.AdminSession),
            await _app.Body(stacksElsewhere, _app.AdminSession));
    }

    /// <summary>
    /// Naming no environment is asking for this server's own daemon, which is where
    /// stacks have always lived. Refusing a remote host must not cost anyone the
    /// stacks they actually have.
    /// </summary>
    [Fact]
    public async Task StacksOnThisServerAreStillServed() =>
        Assert.Equal(HttpStatusCode.OK, await _app.Status(StacksPath, _app.AdminSession));

    public sealed class AppFixture : WebApplicationFactory<Program>, IAsyncLifetime
    {
        public const string ReadOnlyEnvironmentId = "frozen";

        public const string WritableEnvironmentId = "staging";

        private readonly string _directory;

        public HttpClient Client { get; private set; } = null!;

        public string AdminSession { get; private set; } = "";

        public AppFixture()
        {
            _directory = Path.Combine(Path.GetTempPath(), "pinqops-envguard-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Environment.SetEnvironmentVariable("PINQOPS_UI_CONFIG", Path.Combine(_directory, "ui.json"));
            Environment.SetEnvironmentVariable("PINQOPS_AUDIT_LOG", Path.Combine(_directory, "audit.jsonl"));

            new UiConfigStore(Path.Combine(_directory, "ui.json")).Update(config =>
            {
                config.Users.Add(Account("boss", "admin-password-01", UserRoles.Admin));
                config.Environments.Add(Remote(ReadOnlyEnvironmentId, "10.0.0.7", readOnly: true));
                config.Environments.Add(Remote(WritableEnvironmentId, "10.0.0.8", readOnly: false));
            });
        }

        private static UserAccount Account(string username, string password, string role) => new()
        {
            Username = username,
            PasswordHash = PasswordHasher.Hash(password),
            Role = role,
        };

        /// <summary>
        /// A host that is not this one. No key is stored for either, because nothing
        /// here connects to them — the gate answers before docker is ever reached.
        /// </summary>
        private static ManagedEnvironment Remote(string id, string hostAddress, bool readOnly) => new()
        {
            Id = id,
            Name = id,
            Transport = ManagedEnvironment.TransportSsh,
            Host = hostAddress,
            User = "deploy",
            ReadOnly = readOnly,
        };

        public async Task InitializeAsync()
        {
            Client = CreateClient();
            AdminSession = await Login("boss", "admin-password-01");
        }

        /// <summary>The status of a plain GET, which is the whole answer these tests want.</summary>
        public async Task<HttpStatusCode> Status(string path, string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Client.SendAsync(request);
            return response.StatusCode;
        }

        /// <summary>The status of a write, for the routes these tests have to change.</summary>
        public async Task<HttpStatusCode> Send(HttpMethod method, string path, string token, object? body = null)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

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
