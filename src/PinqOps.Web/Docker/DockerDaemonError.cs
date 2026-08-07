namespace PinqOps.Web;

/// <summary>
/// Turns the Docker CLI's "I cannot reach the daemon" stderr into something an
/// operator can act on.
///
/// Every docker call in the dashboard used to surface <c>docker</c>'s raw stderr,
/// so a server with the daemon stopped filled five pages with
/// <c>Cannot connect to the Docker daemon at unix:///var/run/docker.sock…</c> —
/// text that names a socket path and asks a question, but never says what the
/// reader should do. The three causes need three different answers (start it,
/// join the group, install it), and only the daemon knows which one applies, so
/// the classification happens here, once, where the stderr arrives.
///
/// The wording is part of the contract: the dashboard matches these messages to
/// swap a table full of red text for a single "Docker is not reachable" card
/// with a retry button, so changing them means changing that matcher too.
/// </summary>
public static class DockerDaemonError
{
    /// <summary>Why the daemon could not be reached, or <see cref="None"/>.</summary>
    public enum Cause
    {
        /// <summary>Not a connectivity failure — some other docker error.</summary>
        None,

        /// <summary>The daemon is not listening (stopped, or never started).</summary>
        NotRunning,

        /// <summary>The socket is there but this user may not open it.</summary>
        PermissionDenied,

        /// <summary>There is no docker binary to run.</summary>
        NotInstalled,
    }

    /// <summary>
    /// The message shown when the daemon is unreachable. The leading sentence is
    /// what the dashboard matches on, so it stays stable across all three causes.
    /// </summary>
    public const string Unreachable = "Docker is not reachable.";

    /// <summary>
    /// Classifies docker's stderr. Matching is on the daemon's own phrasing rather
    /// than an exit code, because the CLI exits 1 for everything.
    /// </summary>
    public static Cause Classify(string? standardError)
    {
        if (string.IsNullOrWhiteSpace(standardError))
        {
            return Cause.None;
        }

        // A missing binary is reported by the process launcher, not by docker.
        if (Contains(standardError, "command not found")
            || Contains(standardError, "is not recognized as an internal or external command")
            || Contains(standardError, "No such file or directory: 'docker'")
            || Contains(standardError, "executable file not found"))
        {
            return Cause.NotInstalled;
        }

        // Permission has to be tested before the general connectivity check: the
        // permission failure also carries "connect to the Docker daemon socket".
        var isConnectionFailure =
            Contains(standardError, "Cannot connect to the Docker daemon")
            || Contains(standardError, "failed to connect to the docker API")
            || Contains(standardError, "docker daemon is not running")
            || Contains(standardError, "error during connect")
            || Contains(standardError, "Is the docker daemon running");

        if (Contains(standardError, "permission denied")
            && (isConnectionFailure || Contains(standardError, "docker.sock")))
        {
            return Cause.PermissionDenied;
        }

        return isConnectionFailure ? Cause.NotRunning : Cause.None;
    }

    /// <summary>
    /// The operator-facing message for a classified failure, or <c>null</c> when
    /// the stderr was some other docker error and belongs to the caller.
    /// </summary>
    public static string? Describe(string? standardError) => Classify(standardError) switch
    {
        Cause.NotRunning =>
            $"{Unreachable} The Docker daemon is not running on this server — start it with "
            + "'sudo systemctl start docker' and try again.",
        Cause.PermissionDenied =>
            $"{Unreachable} This user may not open the Docker socket — add it to the 'docker' group "
            + "('sudo usermod -aG docker <user>') and restart pinqops.",
        Cause.NotInstalled =>
            $"{Unreachable} Docker does not appear to be installed on this server — install it, "
            + "then reload this page.",
        _ => null,
    };

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
