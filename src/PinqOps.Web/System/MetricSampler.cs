using System.Text.Json;
using PinqOps.Alerts;

namespace PinqOps.Web;

/// <summary>
/// Produces one <see cref="MetricSample"/> per tick from <c>/proc</c> and two
/// docker commands — <c>docker stats</c> for CPU and memory, <c>docker ps -a</c>
/// for state and health. Two invocations regardless of how many containers the
/// host runs; nothing here calls <c>docker inspect</c> per container.
///
/// Only the local daemon is sampled. Remote hosts registered under
/// Settings → Servers are reached over SSH, which would mean a round trip per
/// host per minute, and <c>/proc</c> would still describe <em>this</em> machine —
/// a half-sample nobody could read correctly. The rule model leaves room to add
/// it later.
/// </summary>
public sealed class MetricSampler
{
    /// <summary>
    /// Docker being unreachable is usually a sustained condition. Logging it every
    /// minute would put 1440 identical warnings a day in the journal and bury
    /// everything else, so only every thirtieth failure is reported.
    /// </summary>
    private const int LogEveryNthFailure = 30;

    private readonly DockerService _docker;
    private readonly SystemInfoService _system;
    private readonly ILogger<MetricSampler> _logger;

    private CpuTimes? _previousCpu;
    private int _dockerFailures;

    public MetricSampler(DockerService docker, SystemInfoService system, ILogger<MetricSampler> logger)
    {
        _docker = docker;
        _system = system;
        _logger = logger;
    }

    public async Task<MetricSample> SampleAsync(DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        var (host, cpu) = _system.Snapshot(at, _previousCpu);
        _previousCpu = cpu;

        var (containers, reachable) = await ReadContainersAsync(cancellationToken).ConfigureAwait(false);

        return host with { Containers = containers, DockerReachable = reachable };
    }

    private async Task<(IReadOnlyList<ContainerMetrics> Containers, bool Reachable)> ReadContainersAsync(
        CancellationToken cancellationToken)
    {
        List<JsonElement> listed;
        List<JsonElement> stats;
        try
        {
            // docker ps first: it is the cheaper of the two and the one that
            // decides which containers exist at all.
            //
            // The token goes into docker rather than only being checked between the
            // two calls: without it the caller's 30s budget could not interrupt a
            // call already in flight, so a wedged daemon cost 60s + 60s — twice the
            // tick interval the whole design is built around.
            listed = await _docker.ListContainersAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            stats = await _docker.StatsAsync(cancellationToken).ConfigureAwait(false);
            _dockerFailures = 0;
        }
        // A cancelled read is one more way docker did not answer: reporting the
        // series as no-data costs one tick's container metrics, whereas letting it
        // propagate cost the whole tick — including the host metrics that were
        // already collected and the chart history they feed.
        catch (Exception exception) when (exception is InvalidOperationException or IOException
                                             or OperationCanceledException)
        {
            if (_dockerFailures++ % LogEveryNthFailure == 0)
            {
                _logger.LogWarning(
                    exception, "Could not read container metrics; container alert rules will report no data");
            }

            return ([], false);
        }

        var usage = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var entry in stats)
        {
            var name = MetricParsing.FirstName(Text(entry, "Name"));
            if (name.Length > 0)
            {
                usage[name] = entry;
            }
        }

        var containers = new List<ContainerMetrics>(listed.Count);
        foreach (var entry in listed)
        {
            var name = MetricParsing.FirstName(Text(entry, "Names"));
            if (name.Length == 0)
            {
                continue;
            }

            var state = Text(entry, "State");
            var status = Text(entry, "Status");
            usage.TryGetValue(name, out var stat);

            containers.Add(new ContainerMetrics
            {
                Name = name,
                Cpu = MetricParsing.Percent(Text(stat, "CPUPerc")),
                Memory = MetricParsing.MemoryPercent(Text(stat, "MemPerc"), Text(stat, "MemUsage")),
                Down = !MetricParsing.IsRunning(state, status),
                Unhealthy = MetricParsing.IsUnhealthy(status),
                Restarting = MetricParsing.IsRestarting(state, status),
            });
        }

        return (containers, true);
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
}
