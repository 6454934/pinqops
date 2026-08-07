using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;

namespace PinqOps.Web;

/// <summary>
/// Turns Kestrel's failure to bind the listening socket into one line an
/// operator can act on.
///
/// This is the first thing that can go wrong on a brand-new server, and it went
/// wrong loudly: thirty lines of .NET stack trace ending in
/// <c>AddressInUseException</c>. The two causes an operator actually hits — the
/// port is taken, or the port is privileged and pinqops is not root — each have
/// a one-line answer, and neither of them is in that stack trace.
/// </summary>
public static class StartupBindError
{
    /// <summary>
    /// An operator-facing explanation of a failed bind, or <c>null</c> if the
    /// exception is not a bind failure and belongs to the caller.
    /// </summary>
    public static string? Describe(Exception exception, string host, string port)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // Kestrel wraps the socket error in an IOException; the cause is inside.
        var cause = exception is IOException ? exception.InnerException ?? exception : exception;

        if (cause is AddressInUseException || Socket(cause)?.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return $"error: port {port} is already in use on {host}. Another pinqops-ui may already be "
                + "running (check 'systemctl status pinqops-ui'), or something else holds the port. "
                + "Stop it, or start this one on a different port with --port <n>.";
        }

        var socketError = Socket(cause)?.SocketErrorCode;
        if (socketError == SocketError.AccessDenied)
        {
            return $"error: not allowed to bind port {port} on {host}. Ports below 1024 need root — "
                + "run it with sudo, or pick a port above 1024 with --port <n>.";
        }

        if (socketError == SocketError.AddressNotAvailable)
        {
            return $"error: no interface on this machine has the address {host}, so nothing can listen on "
                + "it. Use --host 0.0.0.0 to accept connections on every interface, or --host 127.0.0.1 "
                + "for this machine only.";
        }

        return null;
    }

    private static SocketException? Socket(Exception exception) =>
        exception as SocketException ?? exception.InnerException as SocketException;
}
