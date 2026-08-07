using PinqOps;

namespace PinqOps.Web;

/// <summary>
/// Reads and sets the host's time zone through <c>timedatectl</c>.
///
/// Only the zone, deliberately. The clock itself is left to NTP: every
/// timestamp this dashboard shows — deploy history, the audit chain, alert
/// windows, TLS validity — is anchored to it, and a hand-set clock silently
/// invalidates the lot. The zone is the part an operator actually needs, because
/// it is what makes "17:04" mean the same thing in the UI as it does in
/// <c>docker logs</c> and <c>journalctl</c> on the same box.
/// </summary>
public sealed class HostTimeZoneService
{
    /// <summary>Where the system's zone database lives; used to list the choices.</summary>
    private const string ZoneInfoDirectory = "/usr/share/zoneinfo";

    private readonly IProcessRunner _processRunner;
    private readonly Action _forgetCachedZone;

    public HostTimeZoneService(IProcessRunner processRunner)
        : this(processRunner, TimeZoneInfo.ClearCachedData)
    {
    }

    /// <summary>
    /// Takes the "forget the zone this process resolved" step as a parameter so a
    /// test can watch for it: the effect itself is process-wide and invisible.
    /// </summary>
    internal HostTimeZoneService(IProcessRunner processRunner, Action forgetCachedZone)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(forgetCachedZone);
        _processRunner = processRunner;
        _forgetCachedZone = forgetCachedZone;
    }

    /// <summary>
    /// The current zone, whether the clock is NTP-synchronised, and every zone
    /// this host will accept. <c>supported</c> is false when timedatectl is not
    /// there at all — a container, or a non-systemd host — so the UI can say so
    /// instead of offering a control that cannot work.
    /// </summary>
    public async Task<object> GetAsync(CancellationToken cancellationToken = default)
    {
        var shown = await RunTimedatectlAsync(
            new[] { "show", "-p", "Timezone", "-p", "NTPSynchronized", "--value" }, cancellationToken)
            .ConfigureAwait(false);

        if (shown is null)
        {
            return new
            {
                supported = false,
                current = TimeZoneInfo.Local.Id,
                ntpSynchronized = (bool?)null,
                zones = Array.Empty<string>(),
                serverTimeUtc = DateTimeOffset.UtcNow,
            };
        }

        // --value prints one line per requested property, in the order asked.
        var lines = shown.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var current = lines.Length > 0 ? lines[0] : TimeZoneInfo.Local.Id;
        var ntpSynchronized = lines.Length > 1 ? string.Equals(lines[1], "yes", StringComparison.OrdinalIgnoreCase) : (bool?)null;

        return new
        {
            supported = true,
            current,
            ntpSynchronized,
            zones = await ListZonesAsync(cancellationToken).ConfigureAwait(false),
            serverTimeUtc = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Sets the zone. The value is checked against the host's own list first:
    /// arguments already go to the process verbatim rather than through a shell,
    /// so this is not about injection — it is so an unknown zone fails with a
    /// sentence the operator can act on instead of timedatectl's exit code.
    /// </summary>
    public async Task<object> SetAsync(string? zone, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(zone))
        {
            throw new ArgumentException("A time zone is required.");
        }

        zone = zone.Trim();
        var zones = await ListZonesAsync(cancellationToken).ConfigureAwait(false);
        if (zones.Count > 0 && !zones.Contains(zone, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"'{zone}' is not a time zone this server knows. Pick one from the list (e.g. Europe/Istanbul).");
        }

        var result = await _processRunner
            .RunAsync("timedatectl", new[] { "set-timezone", zone }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            var detail = result.StandardError.Trim();
            // The common one by far: pinqops is not root, so the call is refused.
            throw new InvalidOperationException(
                detail.Contains("permission", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("Interactive authentication required", StringComparison.OrdinalIgnoreCase)
                    ? "Changing the time zone needs root. Run pinqops-ui as a service (it runs as root), "
                        + "or set it on the host with 'sudo timedatectl set-timezone " + zone + "'."
                    : $"Could not set the time zone: {(detail.Length > 0 ? detail : "timedatectl failed.")}");
        }

        // .NET resolves the local zone once and caches it for the life of the
        // process. Without this the host moves, `docker logs` and `journalctl` move,
        // the reading this page shows back moves — and the scheduler goes on firing
        // in the old zone until something restarts it, at which point a nightly job
        // jumps by the offset with nobody having edited it.
        _forgetCachedZone();

        return new { current = zone, serverTimeUtc = DateTimeOffset.UtcNow };
    }

    /// <summary>
    /// Every zone the host accepts. Read from the zoneinfo directory rather than
    /// <c>timedatectl list-timezones</c>, which pages through a pager when it
    /// thinks it has a terminal and returns well over a thousand lines slowly.
    /// Falls back to timedatectl where the directory is absent.
    /// </summary>
    private async Task<IReadOnlyList<string>> ListZonesAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (Directory.Exists(ZoneInfoDirectory))
            {
                var zones = Directory
                    .EnumerateFiles(ZoneInfoDirectory, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(ZoneInfoDirectory, path).Replace('\\', '/'))
                    // Region/City only: the top level also holds the binary format
                    // files (zone.tab, leapseconds, posixrules) and the legacy
                    // single-word aliases, none of which belong in a picker.
                    .Where(name => name.Contains('/', StringComparison.Ordinal))
                    .Where(name => !name.StartsWith("right/", StringComparison.Ordinal)
                        && !name.StartsWith("posix/", StringComparison.Ordinal)
                        && !name.StartsWith("SystemV/", StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToList();

                if (zones.Count > 0)
                {
                    return zones;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Fall through to timedatectl below.
        }

        var listed = await RunTimedatectlAsync(new[] { "list-timezones", "--no-pager" }, cancellationToken)
            .ConfigureAwait(false);
        return listed is null
            ? Array.Empty<string>()
            : listed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Runs timedatectl, or null when it is not available on this host.</summary>
    private async Task<string?> RunTimedatectlAsync(string[] arguments, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processRunner
                .RunAsync("timedatectl", arguments, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return result.Succeeded ? result.StandardOutput : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // No timedatectl on this host (a container, or a non-systemd distro).
            return null;
        }
    }
}
