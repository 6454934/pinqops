using PinqOps.Proxy;
using PinqOps.Traffic;

namespace PinqOps.Web;

/// <summary>
/// Summarises what the proxy served, by reading its access log.
///
/// <para><b>Read on demand, not collected.</b> Caddy already writes and rolls the
/// file; a background service that copied it into a second store would double the
/// disk cost to answer a question nobody asks between page loads. The window is
/// bounded instead, so the read is bounded.</para>
///
/// <para><b>This is not tracking.</b> There are no cookies, no identifiers and no
/// per-visitor anything, and nothing leaves the machine. What it can say about
/// geography is exactly what a CDN in front of it already put in a header — pinqops
/// embeds no GeoIP database and makes no lookup per request.</para>
/// </summary>
public sealed class TrafficService
{
    /// <summary>
    /// How many of the most recent lines are read. A busy proxy writes faster than
    /// anyone reads, and the alternative to a cap is a page that gets slower every
    /// day until it times out.
    /// </summary>
    public const int MaximumLines = 200_000;

    private readonly ProxyService _proxy;
    private readonly ILogger<TrafficService> _logger;

    public TrafficService(ProxyService proxy, ILogger<TrafficService> logger)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(logger);
        _proxy = proxy;
        _logger = logger;
    }

    /// <summary>Whether the proxy is writing the log this reads.</summary>
    public bool Enabled => _proxy.Store.Load().AccessLog;

    /// <summary>Turns the access log on or off, and reloads the proxy.</summary>
    public async Task<string?> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var applied = await _proxy.Gateway
            .Update(config => config.AccessLog = enabled, cancellationToken)
            .ConfigureAwait(false);

        if (applied.Failed)
        {
            return applied.Error;
        }

        _logger.LogWarning("The proxy access log is now {State}", enabled ? "on" : "off");
        return null;
    }

    /// <summary>
    /// What the proxy served since <paramref name="since"/>, per domain.
    /// </summary>
    public IReadOnlyList<DomainTraffic> Summarise(DateTimeOffset since)
    {
        var path = ProxyPaths.AccessLogFile(ProxyService.Directory);
        if (!File.Exists(path))
        {
            return [];
        }

        IReadOnlyList<AccessEntry> entries = [];
        try
        {
            // Read forward and narrowed to a bounded window rather than reversed: the
            // file is rolled by Caddy, so the whole of it is already an interval
            // rather than all of history. ReadLines streams, so the file is never
            // held in memory whatever its size.
            entries = TrafficWindow.MostRecent(
                File.ReadLines(path).Select(AccessLog.Parse).OfType<AccessEntry>(),
                since,
                MaximumLines);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Being written to while it is read is the normal case, and a summary
            // that threw would be one nobody could load during traffic.
            _logger.LogInformation("The access log could not be read fully: {Detail}", exception.Message);
        }

        return TrafficRollup.Summarise(entries);
    }
}
