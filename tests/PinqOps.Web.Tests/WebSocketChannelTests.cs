using Microsoft.AspNetCore.Http;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// The subprotocol handshake, on its own. This is the only novel part of how a
/// socket authenticates — everything after it is the ordinary scope table — so it
/// is worth pinning without a server in the way.
/// </summary>
public class WebSocketChannelTests
{
    private static HttpRequest RequestWith(string? subprotocolHeader)
    {
        var context = new DefaultHttpContext();
        if (subprotocolHeader is not null)
        {
            context.Request.Headers.SecWebSocketProtocol = subprotocolHeader;
        }

        return context.Request;
    }

    [Fact]
    public void TheTokenIsReadFromTheSubprotocolList()
    {
        Assert.Equal("abc123", WebSocketChannel.TokenFrom(RequestWith("pinqops.bearer, abc123")));
    }

    [Fact]
    public void SpacingAroundTheEntriesDoesNotMatter()
    {
        Assert.Equal("abc123", WebSocketChannel.TokenFrom(RequestWith("pinqops.bearer,abc123")));
        Assert.Equal("abc123", WebSocketChannel.TokenFrom(RequestWith("  pinqops.bearer ,  abc123  ")));
    }

    /// <summary>
    /// An API token is base64url and a session token is hex; both survive being a
    /// subprotocol entry, which is why this scheme works at all.
    /// </summary>
    [Theory]
    [InlineData("pot_ab-cd_ef123")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void RealTokenShapesSurvive(string token)
    {
        Assert.Equal(token, WebSocketChannel.TokenFrom(RequestWith($"pinqops.bearer, {token}")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pinqops.bearer")]
    [InlineData("chat, abc123")]
    [InlineData("abc123, pinqops.bearer")]
    public void AnythingElseCarriesNoToken(string? header)
    {
        Assert.Null(WebSocketChannel.TokenFrom(RequestWith(header)));
    }

    /// <summary>The marker is matched exactly — a near miss must not be read as a
    /// token, or a typo would turn into a confusing 401 instead of a clear one.</summary>
    [Fact]
    public void TheMarkerIsCaseSensitive()
    {
        Assert.Null(WebSocketChannel.TokenFrom(RequestWith("PINQOPS.BEARER, abc123")));
    }

    [Fact]
    public void TheLimitsAreTheOnesDocumented()
    {
        Assert.Equal(64 * 1024, WebSocketChannel.MaximumMessageBytes);
        Assert.Equal(TimeSpan.FromMinutes(5), WebSocketChannel.IdleTimeout);
        Assert.Equal(TimeSpan.FromHours(2), WebSocketChannel.MaximumDuration);
        Assert.Equal(
            new WebSocketLimits(
                WebSocketChannel.MaximumMessageBytes, WebSocketChannel.IdleTimeout, WebSocketChannel.MaximumDuration),
            WebSocketLimits.Default);
    }

    // ---- the timeouts -------------------------------------------------------

    /// <summary>
    /// A socket that never says anything and never closes — what a forgotten tab
    /// behind a proxy that swallowed the FIN looks like from the server.
    /// </summary>
    private sealed class SilentSocket : System.Net.WebSockets.WebSocket
    {
        public System.Net.WebSockets.WebSocketCloseStatus? ClosedWith { get; private set; }

        public string? CloseReason { get; private set; }

        public override System.Net.WebSockets.WebSocketCloseStatus? CloseStatus => ClosedWith;

        public override string? CloseStatusDescription => CloseReason;

        public override System.Net.WebSockets.WebSocketState State =>
            ClosedWith is null
                ? System.Net.WebSockets.WebSocketState.Open
                : System.Net.WebSockets.WebSocketState.Closed;

        public override string? SubProtocol => WebSocketChannel.BearerSubprotocol;

        public override void Abort()
        {
        }

        public override Task CloseAsync(
            System.Net.WebSockets.WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            ClosedWith = closeStatus;
            CloseReason = statusDescription;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            System.Net.WebSockets.WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose()
        {
        }

        /// <summary>Never completes on its own — only the caller's token ends the wait.</summary>
        public override async Task<System.Net.WebSockets.WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            System.Net.WebSockets.WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static WebSocketSession Session(SilentSocket socket, WebSocketLimits limits, CancellationToken cancelled) =>
        new(socket, "boss", "admin", cancelled, limits);

    /// <summary>
    /// The idle timeout fires, and the client is told that is what happened — an
    /// unexplained disconnect sends people looking at their proxy.
    /// </summary>
    [Fact]
    public async Task ASilentSocketIsClosedWhenItGoesIdle()
    {
        using var socket = new SilentSocket();
        var limits = new WebSocketLimits(1024, TimeSpan.FromMilliseconds(150), TimeSpan.FromMinutes(5));
        var session = Session(socket, limits, CancellationToken.None);

        Assert.Null(await session.Receive());

        await session.Close();
        Assert.Equal(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, socket.ClosedWith);
        Assert.Contains("Idle", socket.CloseReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The overall lifetime cap — what <c>MaximumDuration</c> arms in the channel —
    /// ends the session too, and is reported as an ordinary close rather than as an
    /// idle one, because the socket may have been perfectly busy.
    /// </summary>
    [Fact]
    public async Task ASocketPastItsMaximumDurationIsClosed()
    {
        using var socket = new SilentSocket();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var limits = new WebSocketLimits(1024, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        var session = Session(socket, limits, lifetime.Token);

        Assert.Null(await session.Receive());

        await session.Close();
        Assert.Equal(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, socket.ClosedWith);
        Assert.DoesNotContain("Idle", socket.CloseReason, StringComparison.Ordinal);
    }
}
