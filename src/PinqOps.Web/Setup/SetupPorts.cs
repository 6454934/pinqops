using PinqOps.Proxy;

namespace PinqOps.Web;

/// <summary>
/// Resolves the port pair the compose project publishes, honoring an explicit
/// choice from the publish wizard over the automatic defaults.
/// </summary>
public static class SetupPorts
{
    /// <summary>
    /// Host ports already spoken for even when nothing is bound to them right
    /// now: every other app's recorded <c>PINQOPS_HOST_PORT</c>, and everything
    /// the proxy publishes (its own 80/443 and each enrolled port). A bind probe
    /// alone misses all of these — an app that is stopped, not yet deployed, or
    /// served by a proxy that is momentarily down still owns its port, and
    /// handing it out again works until both try to run.
    /// </summary>
    public static HashSet<int> ReservedHostPorts(UiConfig config, DomainConfig domains, string? excludeAppId)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(domains);

        var reserved = new HashSet<int>(ProxyPortSet.HostPorts(domains));
        foreach (var app in config.Apps)
        {
            if (string.Equals(app.Id, excludeAppId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (EndpointHelpers.TryParsePort(
                    EnvFileStore.GetValue(
                        PinqOpsStatePaths.EnvFile(app.ComposeFile), SetupTemplates.HostPortVariable)) is { } port)
            {
                reserved.Add(port);
            }
        }

        return reserved;
    }

    /// <summary>
    /// The container-side port. When the Dockerfile's EXPOSE is known it wins —
    /// a stale form/.env value that mirrored the host port (e.g. both 8085)
    /// used to publish <c>host→host</c> while the app listened on EXPOSE.
    /// Without EXPOSE, the wizard's explicit value wins, else
    /// <see cref="DockerfileInspector.DefaultPort"/>.
    /// </summary>
    public static int ResolveContainer(int? requested, int? detected)
    {
        if (detected is { } exposed)
        {
            if (!HostPort.IsValid(exposed))
            {
                throw new ArgumentException($"'{exposed}' is not a valid container port (1-65535).");
            }

            return exposed;
        }

        if (requested is { } port)
        {
            if (!HostPort.IsValid(port))
            {
                throw new ArgumentException($"'{port}' is not a valid container port (1-65535).");
            }

            return port;
        }

        return DockerfileInspector.DefaultPort;
    }

    /// <summary>
    /// The host-side port. An explicit value must be free right now — a taken
    /// port would only surface later as a failed `up -d` that leaves the app
    /// stopped. Without one, the first free port from <paramref name="defaultPort"/>.
    /// </summary>
    /// <remarks>Probing is injected so the choice logic is testable without sockets.</remarks>
    public static int ResolveHost(
        int? requested, int defaultPort, Func<int, bool> isAvailable, Func<int, int?> findAvailable)
    {
        if (requested is { } port)
        {
            if (!HostPort.IsValid(port))
            {
                throw new ArgumentException($"'{port}' is not a valid host port (1-65535).");
            }

            if (!isAvailable(port))
            {
                throw new ArgumentException(
                    $"Port {port} is already in use on this server. Pick a free one — "
                    + "the deploy would fail on 'port is already allocated' and leave the app stopped.");
            }

            return port;
        }

        // Exhaustion is surfaced, not papered over: the fallback used to hand back
        // the known-busy default, and the failure this method exists to prevent —
        // a first deploy dying on "port is already allocated" — happened anyway,
        // just later and with less context.
        return findAvailable(defaultPort)
            ?? throw new InvalidOperationException(
                $"No free host port found scanning {HostPort.ScanLimit} ports from {defaultPort}. "
                + "Free one up, or pick a port explicitly.");
    }
}
