using System.Text.Json;
using System.Text.Json.Serialization;

namespace PinqOps.Alerts;

/// <summary>One charted reading.</summary>
public sealed record MetricPoint(DateTimeOffset At, double Value);

/// <summary>
/// The on-disk shape of one sample. Short property names on purpose: this line is
/// written every minute forever, and "cpuPercent" instead of "c" would roughly
/// double the file for no reader's benefit.
/// </summary>
internal sealed record MetricLine
{
    [JsonPropertyName("t")]
    public long T { get; init; }

    [JsonPropertyName("c")]
    public double? Cpu { get; init; }

    [JsonPropertyName("m")]
    public double? Memory { get; init; }

    [JsonPropertyName("s")]
    public double? Swap { get; init; }

    [JsonPropertyName("d")]
    public double? Disk { get; init; }

    [JsonPropertyName("l")]
    public double[]? Load { get; init; }

    [JsonPropertyName("k")]
    public List<ContainerLine>? Containers { get; init; }
}

internal sealed record ContainerLine
{
    [JsonPropertyName("n")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("c")]
    public double? Cpu { get; init; }

    [JsonPropertyName("m")]
    public double? Memory { get; init; }

    [JsonPropertyName("s")]
    public int Down { get; init; }

    [JsonPropertyName("h")]
    public int Unhealthy { get; init; }

    [JsonPropertyName("r")]
    public int Restarting { get; init; }
}

/// <summary>
/// A rolling window of samples, so the dashboard can chart where a metric has
/// been and where the threshold sits — the thing that makes a threshold possible
/// to choose rather than guess.
///
/// Rotation is by line count rather than by size, which is what makes the window
/// a predictable length of <em>time</em>: 24 hours per file at one sample a
/// minute, kept as the live file plus <see cref="Generations"/> previous ones, so
/// between 48 and 72 hours are always on hand. The dashboard offers a 48-hour
/// chart, so the guarantee has to be the bottom of that range rather than an
/// average.
///
/// A measured host-only line is about 70 bytes and each recorded container adds
/// about 55, so <see cref="MaxContainersPerSample"/> holds a line under ~2.5 KB
/// and a full generation under ~3.5 MB. That keeps the byte cap below a genuine
/// backstop: if it were reachable in normal use it would shorten the window
/// silently, which is exactly what rotating by lines is meant to avoid.
/// </summary>
public sealed class MetricHistoryStore
{
    /// <summary>24 hours at one sample a minute.</summary>
    public const int MaxLinesPerGeneration = 1440;

    /// <summary>Previous files kept alongside the live one.</summary>
    public const int Generations = 2;

    /// <summary>
    /// How many container series one sample may record — the busiest first,
    /// because those are the ones worth charting. This bounds the chart history
    /// only: rules still evaluate every container, up to
    /// <see cref="AlertRuleValidator.MaxSeriesPerRule"/>.
    /// </summary>
    public const int MaxContainersPerSample = 40;

    /// <summary>Disk backstop, not the retention policy — see the class remarks.</summary>
    private const long MaxBytesPerGeneration = 8 * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly RotatingJsonLog _log;

    public MetricHistoryStore(string path) =>
        _log = new RotatingJsonLog(path, Generations, MaxBytesPerGeneration, MaxLinesPerGeneration);

    public string Path_ => _log.Path_;

    /// <summary>
    /// Records one sample. Only containers named in <paramref name="tracked"/> are
    /// written — without that filter the file would grow with the container count
    /// rather than with the number of things anyone is actually watching. Pass
    /// null to record every container.
    /// </summary>
    public void Append(MetricSample sample, IReadOnlyCollection<string>? tracked = null)
    {
        ArgumentNullException.ThrowIfNull(sample);

        var containers = sample.Containers.AsEnumerable();
        if (tracked is not null)
        {
            var wanted = new HashSet<string>(tracked, StringComparer.Ordinal);
            containers = containers.Where(c => wanted.Contains(c.Name));
        }

        var kept = containers
            .OrderByDescending(c => c.Cpu ?? 0)
            .Take(MaxContainersPerSample)
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => new ContainerLine
            {
                Name = c.Name,
                Cpu = Round(c.Cpu),
                Memory = Round(c.Memory),
                Down = c.Down ? 1 : 0,
                Unhealthy = c.Unhealthy ? 1 : 0,
                Restarting = c.Restarting ? 1 : 0,
            })
            .ToList();

        var line = new MetricLine
        {
            T = sample.At.ToUnixTimeSeconds(),
            Cpu = Round(sample.Cpu),
            Memory = Round(sample.Memory),
            Swap = Round(sample.Swap),
            Disk = Round(sample.Disk),
            Load = sample.Load1 is null && sample.Load5 is null && sample.Load15 is null
                ? null
                : [Round(sample.Load1) ?? 0, Round(sample.Load5) ?? 0, Round(sample.Load15) ?? 0],
            Containers = kept.Count > 0 ? kept : null,
        };

        _log.Append(JsonSerializer.Serialize(line, SerializerOptions));
    }

    /// <summary>
    /// One metric's series since <paramref name="since"/>, averaged down to at
    /// most <paramref name="maxPoints"/> so a hand-rolled SVG can draw it.
    /// </summary>
    public IReadOnlyList<MetricPoint> Read(
        string metric, string series, DateTimeOffset since, int maxPoints = 360)
    {
        var points = new List<MetricPoint>();
        foreach (var line in ReadLines(since))
        {
            if (ValueOf(line, metric, series) is { } value)
            {
                points.Add(new MetricPoint(DateTimeOffset.FromUnixTimeSeconds(line.T), value));
            }
        }

        return MetricDownsample.Bucket(points, maxPoints);
    }

    /// <summary>Every container name that appears in the window, for the chart's series picker.</summary>
    public IReadOnlyList<string> SeriesNames(DateTimeOffset since)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in ReadLines(since))
        {
            foreach (var container in line.Containers ?? [])
            {
                names.Add(container.Name);
            }
        }

        return [.. names];
    }

    private IEnumerable<MetricLine> ReadLines(DateTimeOffset since)
    {
        var cutoff = since.ToUnixTimeSeconds();
        foreach (var raw in _log.ReadLines(oldestFirst: true))
        {
            MetricLine? line;
            try
            {
                line = JsonSerializer.Deserialize<MetricLine>(raw, SerializerOptions);
            }
            catch (JsonException)
            {
                // A torn append costs one sample, not the window.
                continue;
            }

            if (line is not null && line.T >= cutoff)
            {
                yield return line;
            }
        }
    }

    private static double? ValueOf(MetricLine line, string metric, string series)
    {
        if (!AlertMetrics.IsContainerMetric(metric))
        {
            return metric switch
            {
                AlertMetrics.HostCpu => line.Cpu,
                AlertMetrics.HostMemory => line.Memory,
                AlertMetrics.HostSwap => line.Swap,
                AlertMetrics.HostDisk => line.Disk,
                AlertMetrics.HostLoad1 => line.Load is { Length: > 0 } ? line.Load[0] : null,
                AlertMetrics.HostLoad5 => line.Load is { Length: > 1 } ? line.Load[1] : null,
                AlertMetrics.HostLoad15 => line.Load is { Length: > 2 } ? line.Load[2] : null,
                _ => null,
            };
        }

        var container = line.Containers?.FirstOrDefault(c =>
            string.Equals(c.Name, series, StringComparison.Ordinal));
        if (container is null)
        {
            return null;
        }

        return metric switch
        {
            AlertMetrics.ContainerCpu => container.Cpu,
            AlertMetrics.ContainerMemory => container.Memory,
            AlertMetrics.ContainerDown => container.Down,
            AlertMetrics.ContainerUnhealthy => container.Unhealthy,
            AlertMetrics.ContainerRestarting => container.Restarting,
            _ => null,
        };
    }

    /// <summary>Two decimals is well past the precision of anything being sampled here.</summary>
    private static double? Round(double? value) =>
        value is { } number && double.IsFinite(number) ? Math.Round(number, 2) : null;
}

/// <summary>Averages a series into a drawable number of points. Pure.</summary>
public static class MetricDownsample
{
    /// <summary>
    /// Buckets <paramref name="points"/> (oldest first) into at most
    /// <paramref name="maxPoints"/> averages. A bucket with no samples in it
    /// produces no point at all: a gap in the chart is the truth when the
    /// dashboard was not running, and an interpolated zero would read as "the
    /// server was idle".
    /// </summary>
    public static IReadOnlyList<MetricPoint> Bucket(IReadOnlyList<MetricPoint> points, int maxPoints)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (maxPoints <= 0)
        {
            return [];
        }

        if (points.Count <= maxPoints)
        {
            return points;
        }

        var first = points[0].At;
        var span = points[^1].At - first;
        if (span <= TimeSpan.Zero)
        {
            return [points[^1]];
        }

        var width = span.TotalSeconds / maxPoints;
        var result = new List<MetricPoint>(maxPoints);

        var index = 0;
        for (var bucket = 0; bucket < maxPoints && index < points.Count; bucket++)
        {
            var end = first.AddSeconds(width * (bucket + 1));
            double sum = 0;
            var count = 0;
            DateTimeOffset last = default;

            while (index < points.Count && (points[index].At < end || bucket == maxPoints - 1))
            {
                sum += points[index].Value;
                last = points[index].At;
                count++;
                index++;
            }

            if (count > 0)
            {
                result.Add(new MetricPoint(last, sum / count));
            }
        }

        return result;
    }
}
