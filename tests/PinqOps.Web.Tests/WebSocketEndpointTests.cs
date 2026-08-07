using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Tests.Web;

/// <summary>
/// Drives a real socket through the real <c>Program</c>.
///
/// What matters here is that a WebSocket route is not a hole in the authorization
/// model: the handshake is refused for an unauthenticated caller and for one whose
/// scope is too low, <em>before</em> any socket is accepted, and it succeeds for a
/// principal that authenticated with nothing but the subprotocol header.
/// </summary>
[Collection(TestServerCollection.Name)]
public sealed class WebSocketEndpointTests : IClassFixture<WebSocketEndpointTests.AppFixture>
{
    private const string PingPath = "/api/ws/ping";

    private const string ConsolePath = "/api/ws/containers/web/console";

    private readonly AppFixture _app;

    public WebSocketEndpointTests(AppFixture app) => _app = app;

    // ---- the handshake is authorized like any other request ------------------

    /// <summary>
    /// A plain GET, with no upgrade headers at all. The status says which layer
    /// answered: 401 and 403 come from authorization, which runs before the socket
    /// is accepted, and 400 is the endpoint itself saying "this is a WebSocket" —
    /// which only an admitted caller ever gets to see.
    /// </summary>
    [Fact]
    public async Task TheHandshakeIsRefusedWithoutAToken()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, await _app.Get(PingPath, token: null));
    }

    [Fact]
    public async Task TheHandshakeIsRefusedBelowAdmin()
    {
        Assert.Equal(HttpStatusCode.Forbidden, await _app.Get(PingPath, _app.ViewerSession));
        Assert.Equal(HttpStatusCode.Forbidden, await _app.Get(PingPath, _app.DeployerSession));
    }

    [Fact]
    public async Task AnAdmitedCallerReachesTheEndpoint()
    {
        Assert.Equal(HttpStatusCode.BadRequest, await _app.Get(PingPath, _app.AdminSession));
    }

    /// <summary>
    /// The console is <c>docker exec</c> held open, and its one-shot equivalent
    /// (<c>POST /api/docker/containers/{id}/exec</c>) is admin-only. It is also the
    /// one route where the method says nothing about what it does — running code is
    /// a write everywhere else, but opening a socket is a GET — so the scope
    /// classification is the only thing standing between the coarse read default
    /// and a prompt inside the container.
    ///
    /// <para>These principals own nothing here, so the ownership gate would refuse
    /// them too — this pins the end-to-end answer, not which layer gives it. That
    /// the gate cannot be relied on is asserted where it can be stated exactly, in
    /// <c>ApiScopesTests.RequiredFor_ContainerConsole_IsAdmin_LikeExec</c>: a
    /// deploy-scoped caller manages any container marked public.</para>
    /// </summary>
    [Fact]
    public async Task TheConsoleHandshakeIsRefusedBelowAdmin()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, await _app.Get(ConsolePath, token: null));
        Assert.Equal(HttpStatusCode.Forbidden, await _app.Get(ConsolePath, _app.ViewerSession));
        Assert.Equal(HttpStatusCode.Forbidden, await _app.Get(ConsolePath, _app.DeployerSession));
    }

    [Fact]
    public async Task TheConsoleHandshakeReachesTheEndpointForAnAdmin()
    {
        Assert.Equal(HttpStatusCode.BadRequest, await _app.Get(ConsolePath, _app.AdminSession));
    }

    // ---- a real socket ------------------------------------------------------

    /// <summary>
    /// The whole point of the subprotocol scheme: a client that can set no headers
    /// but this one still authenticates, and goes through the same scope table as
    /// everything else.
    /// </summary>
    [Fact]
    public async Task ASocketAuthenticatesWithTheSubprotocolAlone()
    {
        using var socket = await _app.Connect(_app.AdminSession);

        Assert.Equal(WebSocketState.Open, socket.State);
        Assert.Equal("Hello", await Exchange(socket, "Hello"));
    }

    [Fact]
    public async Task ASocketIsRefusedForATokenBelowAdmin()
    {
        var failure = await Record.ExceptionAsync(() => _app.Connect(_app.DeployerSession));

        Assert.NotNull(failure);
    }

    [Fact]
    public async Task ASocketIsRefusedWithoutAToken()
    {
        var failure = await Record.ExceptionAsync(() => _app.Connect(token: null));

        Assert.NotNull(failure);
    }

    /// <summary>An API token works too, so an agent can use the same channel.</summary>
    [Fact]
    public async Task AnAdminApiTokenOpensASocket()
    {
        using var socket = await _app.Connect(_app.AdminToken);

        Assert.Equal("from an agent", await Exchange(socket, "from an agent"));
    }

    // ---- the limits ---------------------------------------------------------

    /// <summary>
    /// A message past the cap closes the socket with a status that says which limit
    /// it hit, rather than the server quietly buffering whatever it is sent.
    /// </summary>
    [Fact]
    public async Task AMessagePastTheCapClosesTheSocket()
    {
        using var socket = await _app.Connect(_app.AdminSession);
        var oversized = new string('x', WebSocketChannel.MaximumMessageBytes + 1024);

        await socket.SendAsync(
            Encoding.UTF8.GetBytes(oversized), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        var closed = await ReadUntilClose(socket);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, closed);
    }

    /// <summary>Right at the cap is still allowed — the boundary is inclusive.</summary>
    [Fact]
    public async Task AMessageAtTheCapIsAccepted()
    {
        using var socket = await _app.Connect(_app.AdminSession);
        var atLimit = new string('x', WebSocketChannel.MaximumMessageBytes);

        Assert.Equal(atLimit, await Exchange(socket, atLimit));
    }

    /// <summary>This channel is text-only; a binary frame is refused by status
    /// rather than silently decoded as if it were text.</summary>
    [Fact]
    public async Task ABinaryFrameClosesTheSocket()
    {
        using var socket = await _app.Connect(_app.AdminSession);

        await socket.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);

        Assert.Equal(WebSocketCloseStatus.InvalidMessageType, await ReadUntilClose(socket));
    }

    // ---- the audit trail ----------------------------------------------------

    /// <summary>
    /// A socket is an admin-scoped read, so the ordinary audit rule already covers
    /// it — no special case, which is exactly what reusing the request pipeline
    /// buys. The line lands when the connection ends, so this waits for it.
    /// </summary>
    [Fact]
    public async Task AConnectionIsWrittenToTheAuditLog()
    {
        using (var socket = await _app.Connect(_app.AdminSession))
        {
            await Exchange(socket, "audited");
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }

        Assert.True(
            await _app.AuditMentions("GET " + PingPath),
            "the socket should have left an audit line.");
    }

    // ---- helpers ------------------------------------------------------------

    private static async Task<string> Exchange(WebSocket socket, string message)
    {
        await socket.SendAsync(
            Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        var buffer = new byte[8 * 1024];
        using var received = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return string.Empty;
            }

            received.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(received.GetBuffer(), 0, (int)received.Length);
            }
        }
    }

    /// <summary>Drains whatever the server sends until it closes, and reports why.</summary>
    private static async Task<WebSocketCloseStatus?> ReadUntilClose(WebSocket socket)
    {
        var buffer = new byte[8 * 1024];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (socket.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            var result = await socket.ReceiveAsync(buffer, deadline.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }
        }

        return socket.CloseStatus;
    }

    public sealed class AppFixture : WebApplicationFactory<Program>, IAsyncLifetime
    {
        private readonly string _directory;
        private readonly string _auditLog;

        public HttpClient Client { get; private set; } = null!;

        public string AdminSession { get; private set; } = "";

        public string ViewerSession { get; private set; } = "";

        public string DeployerSession { get; private set; } = "";

        public string AdminToken { get; }

        public AppFixture()
        {
            _directory = Path.Combine(Path.GetTempPath(), "pinqops-ws-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _auditLog = Path.Combine(_directory, "audit.jsonl");
            Environment.SetEnvironmentVariable("PINQOPS_UI_CONFIG", Path.Combine(_directory, "ui.json"));
            Environment.SetEnvironmentVariable("PINQOPS_AUDIT_LOG", _auditLog);

            var store = new UiConfigStore(Path.Combine(_directory, "ui.json"));
            store.Update(config =>
            {
                config.Users.Add(Account("boss", "admin-password-01", UserRoles.Admin));
                config.Users.Add(Account("viewer1", "viewer-password-1", UserRoles.Viewer));
                config.Users.Add(Account("deployer1", "deploy-password-1", UserRoles.Deployer));
            });

            var tokens = new ApiTokenStore(Path.Combine(_directory, "tokens.json"));
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

        public async Task<HttpStatusCode> Get(string path, string? token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (token is not null)
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await Client.SendAsync(request);
            return response.StatusCode;
        }

        /// <summary>
        /// Opens a socket carrying <paramref name="token"/> the way a browser must:
        /// in the subprotocol list, because it can set nothing else.
        /// </summary>
        public async Task<WebSocket> Connect(string? token)
        {
            var client = Server.CreateWebSocketClient();
            if (token is not null)
            {
                client.ConfigureRequest = request =>
                    request.Headers["Sec-WebSocket-Protocol"] = $"{WebSocketChannel.BearerSubprotocol}, {token}";
            }

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            return await client.ConnectAsync(new Uri("ws://localhost" + PingPath), deadline.Token);
        }

        /// <summary>
        /// Whether the audit log mentions <paramref name="action"/>. The line is
        /// written when the connection ends, which is after the client's close
        /// returns, so this polls briefly rather than reading once and racing it.
        /// </summary>
        public async Task<bool> AuditMentions(string action)
        {
            for (var attempt = 0; attempt < 40; attempt++)
            {
                if (File.Exists(_auditLog))
                {
                    using var stream = new FileStream(
                        _auditLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    if ((await reader.ReadToEndAsync()).Contains(action, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                await Task.Delay(50);
            }

            return false;
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
