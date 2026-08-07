namespace PinqOps;

/// <summary>
/// Reads what pinqops needs to know from an application's Dockerfile. Only the
/// port the image listens on today — enough to publish it without asking the
/// user to hand-edit the compose file.
/// </summary>
public static class DockerfileInspector
{
    /// <summary>Published when a Dockerfile declares no usable port.</summary>
    public const int DefaultPort = 80;

    /// <summary>
    /// The port an <c>EXPOSE</c> instruction declares, or null when there is
    /// none pinqops can use. Handles <c>EXPOSE 80</c>, <c>EXPOSE 80/tcp</c> and
    /// <c>EXPOSE 80 443</c>, and ignores comments and values that are build-time
    /// variables (<c>EXPOSE ${PORT}</c>).
    /// </summary>
    /// <remarks>
    /// Resolution is per build stage: within a stage the FIRST usable port wins,
    /// and the answer is the last stage that declared one. Both halves matter.
    /// Taking the last stage follows a multi-stage build to its runtime image
    /// (a <c>node</c> build stage's port is not what the container serves).
    /// Taking the first port within a stage picks the plain-HTTP port of the
    /// common "HTTP then HTTPS" pair — the stock ASP.NET Core Dockerfile emits
    /// <c>EXPOSE 8080</c> followed by <c>EXPOSE 8081</c>, and only 8080 is ever
    /// bound unless a certificate is configured.
    /// </remarks>
    public static int? FindExposedPort(string dockerfileContent)
    {
        if (string.IsNullOrWhiteSpace(dockerfileContent))
        {
            return null;
        }

        int? lastStagePort = null;
        int? currentStagePort = null;

        foreach (var rawLine in dockerfileContent.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (IsInstruction(line, "FROM"))
            {
                // A new stage begins; whatever the previous one declared is the
                // best answer so far.
                lastStagePort = currentStagePort ?? lastStagePort;
                currentStagePort = null;
                continue;
            }

            // Only the first usable port in a stage counts.
            if (currentStagePort is not null || !IsInstruction(line, "EXPOSE"))
            {
                continue;
            }

            var firstArgument = line[("EXPOSE".Length)..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (firstArgument is null)
            {
                continue;
            }

            // Strip the optional /tcp or /udp protocol suffix.
            var slash = firstArgument.IndexOf('/');
            var portText = slash >= 0 ? firstArgument[..slash] : firstArgument;

            if (int.TryParse(portText, out var port) && IsValidPort(port))
            {
                currentStagePort = port;
            }
        }

        return currentStagePort ?? lastStagePort;
    }

    /// <summary>
    /// Whether <paramref name="line"/> is the given Dockerfile instruction —
    /// the keyword must be followed by whitespace, so <c>EXPOSED</c> is not an
    /// <c>EXPOSE</c>. Instruction keywords are case-insensitive.
    /// </summary>
    private static bool IsInstruction(string line, string keyword) =>
        line.Length > keyword.Length
        && line.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)
        && char.IsWhiteSpace(line[keyword.Length]);

    /// <summary>True for a port number docker can actually publish.</summary>
    public static bool IsValidPort(int port) => HostPort.IsValid(port);
}
