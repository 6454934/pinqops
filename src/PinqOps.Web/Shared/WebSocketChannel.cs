using System.Net.WebSockets;
using System.Text;

namespace PinqOps.Web;

/// <summary>
/// The dashboard's WebSocket plumbing: one place that accepts a socket, bounds it,
/// and hands the handler a text-message pipe.
///
/// <para><b>How a socket authenticates.</b> A browser cannot set an
/// <c>Authorization</c> header on a WebSocket handshake — the API takes no
/// arguments beyond the URL and the subprotocol list — and the token must not go in
/// the query string, where it would land in every access log and proxy trace on the
/// way. So the token rides the subprotocol list, which is a header:
/// <c>Sec-WebSocket-Protocol: pinqops.bearer, &lt;token&gt;</c>. A non-browser
/// client that can set headers may use <c>Authorization</c> as usual.</para>
///
/// <para>Because <see cref="EndpointHelpers.ReadBearerToken"/> reads that header
/// too, a WebSocket route goes through the <em>same</em> scope resolution, the same
/// <c>ApiScopes</c> table, the same authorization policies and the same audit line
/// as every other <c>/api</c> route. There is no second authorization path to keep
/// in step — which is the whole point of doing it this way.</para>
///
/// <para><b>Why every socket is bounded.</b> An open socket is a held thread's
/// worth of server state that no request timeout applies to. All three limits below
/// exist so a forgotten browser tab, a wedged proxy or a hostile client cannot
/// accumulate: a message larger than the cap, a connection that says nothing for
/// too long, and a connection that simply never ends are each closed with a status
/// that says which happened.</para>
/// </summary>
public sealed class WebSocketChannel
{
    /// <summary>
    /// The first entry of the subprotocol list, marking the second as the token.
    /// Echoed back on accept, because a browser closes the connection when the
    /// server selects none of the subprotocols it offered.
    /// </summary>
    public const string BearerSubprotocol = "pinqops.bearer";

    /// <summary>
    /// The largest single message either direction. 64 KB matches Kestrel's request
    /// body cap, so a socket cannot be used to get around it.
    /// </summary>
    public const int MaximumMessageBytes = 64 * 1024;

    /// <summary>How long a socket may say nothing before it is closed.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a socket may live at all. A console left open overnight is closed
    /// rather than held forever; the client can reconnect.
    /// </summary>
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromHours(2);

    private readonly ILogger<WebSocketChannel> _logger;

    public WebSocketChannel(ILogger<WebSocketChannel> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// The limits applied to sockets this channel accepts. Overridable so the
    /// timeouts can be exercised in a test that finishes in milliseconds — waiting
    /// out a five-minute idle timeout is not a test anyone would keep, and an
    /// untested timeout that silently never fires is exactly the bug it exists to
    /// prevent.
    /// </summary>
    public WebSocketLimits Limits { get; init; } = WebSocketLimits.Default;

    /// <summary>
    /// The bearer token carried in the subprotocol list, or null when the request
    /// carries none. Shaped as <c>pinqops.bearer, &lt;token&gt;</c>; anything else
    /// is ignored rather than guessed at.
    /// </summary>
    public static string? TokenFrom(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var header = request.Headers.SecWebSocketProtocol.ToString();
        if (header.Length == 0)
        {
            return null;
        }

        var entries = header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return entries.Length >= 2 && string.Equals(entries[0], BearerSubprotocol, StringComparison.Ordinal)
            ? entries[1]
            : null;
    }

    /// <summary>
    /// Accepts the socket and runs <paramref name="handle"/> against it. Authorization
    /// has already happened — this is reached only for a caller the route's policy
    /// admitted.
    /// </summary>
    public async Task<IResult> Run(HttpContext context, Func<WebSocketSession, Task> handle)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(handle);

        if (!context.WebSockets.IsWebSocketRequest)
        {
            // Someone opened the URL in a browser tab. Say so, rather than leaving a
            // bare 404-looking failure that reads as "the feature is missing".
            return EndpointHelpers.Error(400, "This endpoint is a WebSocket — connect to it with a WebSocket client.");
        }

        // Only echo the subprotocol when it was offered: selecting one the client
        // never listed is a protocol violation, and a header-capable client
        // authenticating with Authorization offers none.
        var offeredBearer = context.WebSockets.WebSocketRequestedProtocols
            .Contains(BearerSubprotocol, StringComparer.Ordinal);

        using var socket = offeredBearer
            ? await context.WebSockets.AcceptWebSocketAsync(BearerSubprotocol)
            : await context.WebSockets.AcceptWebSocketAsync();

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        lifetime.CancelAfter(Limits.MaximumDuration);

        var session = new WebSocketSession(
            socket,
            context.Items["user"] as string ?? AuditLog.Anonymous,
            context.Items["scope"] as string ?? "read",
            lifetime.Token,
            Limits);

        try
        {
            await handle(session);
        }
        catch (OperationCanceledException)
        {
            // The client went away or the socket outlived MaximumDuration. Both are
            // ordinary endings, not failures.
        }
        catch (WebSocketException exception)
        {
            // A half-open connection, a proxy that dropped the tunnel. Worth a line
            // — sockets that never establish are usually a reverse-proxy problem —
            // but never worth failing a request that has already returned 101.
            _logger.LogInformation(exception, "WebSocket on {Path} ended abnormally", context.Request.Path);
        }
        finally
        {
            await session.Close();
        }

        // The 101 was written at accept time; there is no body left to produce.
        return Results.Empty;
    }
}

/// <summary>What bounds one socket. See <see cref="WebSocketChannel"/> for why each exists.</summary>
public sealed record WebSocketLimits(int MaximumMessageBytes, TimeSpan IdleTimeout, TimeSpan MaximumDuration)
{
    public static readonly WebSocketLimits Default = new(
        WebSocketChannel.MaximumMessageBytes, WebSocketChannel.IdleTimeout, WebSocketChannel.MaximumDuration);
}

/// <summary>
/// One accepted socket, as a text-message pipe. Every limit
/// <see cref="WebSocketChannel"/> documents is enforced here, so a handler is a
/// plain receive/send loop and cannot forget one.
/// </summary>
public sealed class WebSocketSession
{
    private const int ReceiveChunkBytes = 4 * 1024;

    private readonly WebSocket _socket;
    private readonly WebSocketLimits _limits;
    private readonly byte[] _buffer = new byte[ReceiveChunkBytes];

    private WebSocketCloseStatus _closeStatus = WebSocketCloseStatus.NormalClosure;
    private string _closeReason = "Closed.";

    internal WebSocketSession(
        WebSocket socket, string user, string scope, CancellationToken cancelled, WebSocketLimits limits)
    {
        _socket = socket;
        User = user;
        Scope = scope;
        Cancelled = cancelled;
        _limits = limits;
    }

    /// <summary>The principal that opened the socket, as the audit line names it.</summary>
    public string User { get; }

    /// <summary>The API scope that principal holds.</summary>
    public string Scope { get; }

    /// <summary>Cancelled when the client goes away or the socket outlives its maximum.</summary>
    public CancellationToken Cancelled { get; }

    /// <summary>
    /// The next text message, or null when the socket is finished — the peer closed
    /// it, it went idle, it outlived its maximum, or it broke a limit. A handler
    /// loops on this and needs no other termination condition.
    /// </summary>
    public async Task<string?> Receive()
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(Cancelled);
        idle.CancelAfter(_limits.IdleTimeout);

        using var message = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(_buffer, idle.Token);
            }
            catch (OperationCanceledException)
            {
                // Two different endings share this catch, and they must not be
                // reported as the same thing: an idle socket is the server's
                // decision and the client deserves to know why.
                if (!Cancelled.IsCancellationRequested)
                {
                    Closing(WebSocketCloseStatus.NormalClosure, "Idle for too long.");
                }

                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                Closing(WebSocketCloseStatus.InvalidMessageType, "This channel carries text messages only.");
                return null;
            }

            // Checked as the fragments arrive rather than after the last one, so a
            // sender cannot make the server buffer an unbounded message before it is
            // rejected.
            if (message.Length + result.Count > _limits.MaximumMessageBytes)
            {
                Closing(
                    WebSocketCloseStatus.MessageTooBig,
                    $"A message may be at most {_limits.MaximumMessageBytes} bytes.");
                return null;
            }

            message.Write(_buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            }
        }
    }

    /// <summary>Sends one text message. Silent when the socket is already gone.</summary>
    public async Task Send(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (_socket.State != WebSocketState.Open)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(message);
        if (bytes.Length > _limits.MaximumMessageBytes)
        {
            // Truncating would hand the client a malformed line and no way to know
            // it was cut; closing says what happened.
            Closing(WebSocketCloseStatus.MessageTooBig, "The server tried to send a message that was too large.");
            return;
        }

        await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, Cancelled);
    }

    private void Closing(WebSocketCloseStatus status, string reason)
    {
        _closeStatus = status;
        _closeReason = reason;
    }

    /// <summary>
    /// Closes the socket with whatever status the session ended on. Failures here
    /// are ignored on purpose: the connection is already over, and the only thing a
    /// throw would achieve is losing the handler's own outcome.
    /// </summary>
    internal async Task Close()
    {
        if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _socket.CloseAsync(_closeStatus, _closeReason, deadline.Token);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }
}

/// <summary>Registration for the WebSocket channel, kept out of <c>Program.cs</c>.</summary>
public static class WebSocketChannelExtensions
{
    public static IServiceCollection AddWebSocketChannel(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddSingleton<WebSocketChannel>();
    }

    /// <summary>
    /// Enables WebSockets. Registered inside the audit middleware so a socket is
    /// recorded like any other request, and before routing so the accept feature is
    /// there when an endpoint asks for it.
    /// </summary>
    public static IApplicationBuilder UseWebSocketChannel(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseWebSockets(new WebSocketOptions
        {
            // Keeps an idle-but-wanted connection alive through proxies that drop
            // silent tunnels, well inside the channel's own idle timeout.
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });
    }
}
