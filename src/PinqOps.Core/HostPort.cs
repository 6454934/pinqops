using System.Net;
using System.Net.Sockets;

namespace PinqOps;

/// <summary>
/// Host-side port checks used when choosing what the compose project publishes.
/// Docker publishes on <c>0.0.0.0</c>, so a plain bind is an accurate test of
/// whether the port is already taken.
/// </summary>
/// <remarks>
/// Only meaningful BEFORE the app owns the port — at setup, or when moving to a
/// different port. Probing at deploy time would always report the app's own
/// running container as a conflict.
/// </remarks>
public static class HostPort
{
    /// <summary>How many consecutive ports <see cref="FindAvailable"/> will try.</summary>
    public const int ScanLimit = 50;

    /// <summary>True for a port number docker can publish.</summary>
    public static bool IsValid(int port) => port is >= 1 and <= 65535;

    /// <summary>
    /// Whether nothing is currently listening on <paramref name="port"/>. A port
    /// this process cannot bind (privileged, or in use) counts as unavailable.
    /// </summary>
    public static bool IsAvailable(int port)
    {
        if (!IsValid(port))
        {
            return false;
        }

        try
        {
            using var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// <paramref name="preferredPort"/> when it is free, otherwise the next free
    /// port above it, or null when <see cref="ScanLimit"/> consecutive ports are
    /// all taken. Callers decide whether that is fatal.
    /// </summary>
    public static int? FindAvailable(int preferredPort) => FindAvailable(preferredPort, IsAvailable);

    /// <summary>
    /// The same scan with the caller's idea of "free" — a bind probe only answers
    /// for this instant, so callers that also know about recorded-but-stopped
    /// owners fold that in here.
    /// </summary>
    public static int? FindAvailable(int preferredPort, Func<int, bool> isAvailable)
    {
        ArgumentNullException.ThrowIfNull(isAvailable);

        if (!IsValid(preferredPort))
        {
            throw new ArgumentOutOfRangeException(nameof(preferredPort), "Port must be between 1 and 65535.");
        }

        for (var port = preferredPort; port < preferredPort + ScanLimit && IsValid(port); port++)
        {
            if (isAvailable(port))
            {
                return port;
            }
        }

        return null;
    }
}
