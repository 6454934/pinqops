using PinqOps.Alerts;
using Xunit;

namespace PinqOps.Tests.Alerts;

public class MetricHistoryStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pinqops-metrics-").FullName;
    private static readonly DateTimeOffset T0 = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Path_ => Path.Combine(_dir, "metrics.jsonl");

    private static MetricSample Sample(
        DateTimeOffset at, double cpu = 10, params ContainerMetrics[] containers) => new()
        {
            At = at,
            Cpu = cpu,
            Memory = 55.5,
            Swap = 0,
            Disk = 41.25,
            Load1 = 0.31,
            Load5 = 0.28,
            Load15 = 0.3,
            Containers = containers,
        };

    [Fact]
    public void HostSeries_RoundTrips()
    {
        var store = new MetricHistoryStore(Path_);
        store.Append(Sample(T0, cpu: 12.4));

        var points = new MetricHistoryStore(Path_).Read(AlertMetrics.HostCpu, string.Empty, T0.AddHours(-1));

        var point = Assert.Single(points);
        Assert.Equal(12.4, point.Value);
        Assert.Equal(T0, point.At);
    }

    [Fact]
    public void LoadAverages_RoundTripInOrder()
    {
        var store = new MetricHistoryStore(Path_);
        store.Append(Sample(T0));

        var since = T0.AddHours(-1);
        Assert.Equal(0.31, Assert.Single(store.Read(AlertMetrics.HostLoad1, string.Empty, since)).Value);
        Assert.Equal(0.28, Assert.Single(store.Read(AlertMetrics.HostLoad5, string.Empty, since)).Value);
        Assert.Equal(0.3, Assert.Single(store.Read(AlertMetrics.HostLoad15, string.Empty, since)).Value);
    }

    [Fact]
    public void ContainerSeries_RoundTripIncludingStateFlags()
    {
        var store = new MetricHistoryStore(Path_);
        store.Append(Sample(
            T0,
            containers: new ContainerMetrics { Name = "app", Cpu = 3.5, Memory = 12, Down = true, Unhealthy = true }));

        var since = T0.AddHours(-1);
        Assert.Equal(3.5, Assert.Single(store.Read(AlertMetrics.ContainerCpu, "app", since)).Value);
        Assert.Equal(1, Assert.Single(store.Read(AlertMetrics.ContainerDown, "app", since)).Value);
        Assert.Equal(1, Assert.Single(store.Read(AlertMetrics.ContainerUnhealthy, "app", since)).Value);
        Assert.Equal(0, Assert.Single(store.Read(AlertMetrics.ContainerRestarting, "app", since)).Value);
    }

    [Fact]
    public void OnlyTrackedContainersAreWritten()
    {
        // Otherwise the file grows with the container count rather than with the
        // number of things anyone is watching.
        var store = new MetricHistoryStore(Path_);
        store.Append(
            Sample(
                T0,
                containers:
                [
                    new ContainerMetrics { Name = "app", Cpu = 3 },
                    new ContainerMetrics { Name = "noise", Cpu = 90 },
                ]),
            tracked: ["app"]);

        var since = T0.AddHours(-1);
        Assert.Single(store.Read(AlertMetrics.ContainerCpu, "app", since));
        Assert.Empty(store.Read(AlertMetrics.ContainerCpu, "noise", since));
    }

    [Fact]
    public void ContainerCount_IsCapped_KeepingTheBusiest()
    {
        const int extra = 10;
        var total = MetricHistoryStore.MaxContainersPerSample + extra;
        var store = new MetricHistoryStore(Path_);
        var containers = Enumerable.Range(0, total)
            .Select(i => new ContainerMetrics { Name = $"c{i:000}", Cpu = i })
            .ToArray();

        store.Append(Sample(T0, containers: containers));

        var names = store.SeriesNames(T0.AddHours(-1));
        Assert.Equal(MetricHistoryStore.MaxContainersPerSample, names.Count);
        // The busiest survive: the last one recorded, the quietest ones dropped.
        Assert.Contains($"c{total - 1:000}", names);
        Assert.DoesNotContain("c000", names);
        Assert.DoesNotContain($"c{extra - 1:000}", names);
    }

    [Fact]
    public void Read_IgnoresAnythingBeforeTheWindow()
    {
        var store = new MetricHistoryStore(Path_);
        store.Append(Sample(T0.AddHours(-5), cpu: 1));
        store.Append(Sample(T0, cpu: 2));

        var points = store.Read(AlertMetrics.HostCpu, string.Empty, T0.AddHours(-1));

        Assert.Equal(2, Assert.Single(points).Value);
    }

    [Fact]
    public void MissingReadings_ProduceNoPoint()
    {
        // A null CPU on the first tick after start must be a gap, not a zero: a
        // fabricated zero reads as "the machine was idle".
        var store = new MetricHistoryStore(Path_);
        store.Append(new MetricSample { At = T0, Cpu = null, Memory = 40 });

        Assert.Empty(store.Read(AlertMetrics.HostCpu, string.Empty, T0.AddHours(-1)));
        Assert.Single(store.Read(AlertMetrics.HostMemory, string.Empty, T0.AddHours(-1)));
    }

    [Fact]
    public void TheWindowIsAtLeastTheChartsLongestRange()
    {
        // The dashboard offers a 48-hour chart, so 48 hours has to be the floor of
        // what is retained, not an average. Retention is the live file plus
        // Generations previous ones — the "+1" is the part that is easy to lose.
        var minimumMinutes = MetricHistoryStore.MaxLinesPerGeneration * MetricHistoryStore.Generations;

        Assert.True(
            minimumMinutes >= 48 * 60,
            $"retention floor is {minimumMinutes / 60}h, below the 48h the chart offers");
    }

    [Fact]
    public void AFullGenerationStaysWellInsideTheByteBackstop()
    {
        // If the byte cap were reachable in normal use it would shorten the window
        // silently, which is the failure rotating by lines exists to prevent.
        var store = new MetricHistoryStore(Path_);
        var containers = Enumerable.Range(0, MetricHistoryStore.MaxContainersPerSample)
            .Select(i => new ContainerMetrics { Name = $"a-fairly-long-container-name-{i:000}", Cpu = i, Memory = i })
            .ToArray();

        for (var i = 0; i < 200; i++)
        {
            store.Append(Sample(T0.AddMinutes(i), containers: containers));
        }

        var bytesPerLine = (double)new FileInfo(Path_).Length / 200;
        var generationBytes = bytesPerLine * MetricHistoryStore.MaxLinesPerGeneration;

        Assert.True(
            generationBytes < 8 * 1024 * 1024,
            $"a full generation is {generationBytes / 1024 / 1024:F1} MB, at or over the byte backstop");
    }

    [Fact]
    public void RotatesAtTheLineBudget()
    {
        var store = new MetricHistoryStore(Path_);
        for (var i = 0; i <= MetricHistoryStore.MaxLinesPerGeneration; i++)
        {
            store.Append(Sample(T0.AddMinutes(i)));
        }

        Assert.True(File.Exists($"{Path_}.1"));
        Assert.False(File.Exists($"{Path_}.{MetricHistoryStore.Generations + 1}"));
    }

    [Fact]
    public void ReadsAcrossARotation()
    {
        var store = new MetricHistoryStore(Path_);
        var total = MetricHistoryStore.MaxLinesPerGeneration + 10;
        for (var i = 0; i < total; i++)
        {
            store.Append(Sample(T0.AddMinutes(i), cpu: 10));
        }

        var points = store.Read(AlertMetrics.HostCpu, string.Empty, T0.AddDays(-1), maxPoints: 10_000);

        Assert.Equal(total, points.Count);
    }

    [Fact]
    public void CorruptLine_IsSkipped()
    {
        var store = new MetricHistoryStore(Path_);
        store.Append(Sample(T0));
        File.AppendAllText(Path_, "{ torn\n");
        store.Append(Sample(T0.AddMinutes(1)));

        Assert.Equal(2, new MetricHistoryStore(Path_).Read(
            AlertMetrics.HostCpu, string.Empty, T0.AddHours(-1)).Count);
    }

    [Fact]
    public void File_IsOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        new MetricHistoryStore(Path_).Append(Sample(T0));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path_));
    }
}

public class MetricDownsampleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private static List<MetricPoint> Series(int count, Func<int, double>? value = null) =>
        [.. Enumerable.Range(0, count).Select(i => new MetricPoint(T0.AddMinutes(i), value?.Invoke(i) ?? i))];

    [Fact]
    public void ShortSeries_AreReturnedUntouched()
    {
        var points = Series(10);

        Assert.Same(points, MetricDownsample.Bucket(points, 360));
    }

    [Fact]
    public void ADaysWorthOfMinutes_FitsInTheBudget()
    {
        var bucketed = MetricDownsample.Bucket(Series(1440), 360);

        Assert.InRange(bucketed.Count, 1, 360);
    }

    [Fact]
    public void EveryPointIsAccountedFor()
    {
        // Averaging must not quietly drop the tail of the series.
        var bucketed = MetricDownsample.Bucket(Series(1000, _ => 5), 100);

        Assert.All(bucketed, point => Assert.Equal(5, point.Value));
        Assert.Equal(T0.AddMinutes(999), bucketed[^1].At);
    }

    [Fact]
    public void GapsProduceNoPoints()
    {
        // Two clusters an hour apart: the empty buckets between them must stay
        // empty rather than being filled with an interpolated value.
        var points = new List<MetricPoint>();
        for (var i = 0; i < 10; i++)
        {
            points.Add(new MetricPoint(T0.AddSeconds(i), 1));
        }

        for (var i = 0; i < 10; i++)
        {
            points.Add(new MetricPoint(T0.AddHours(1).AddSeconds(i), 1));
        }

        var bucketed = MetricDownsample.Bucket(points, 12);

        Assert.True(bucketed.Count < 12);
        Assert.All(bucketed, point => Assert.Equal(1, point.Value));
    }

    [Fact]
    public void IdenticalTimestamps_CollapseToOnePoint()
    {
        var points = Enumerable.Range(0, 20).Select(_ => new MetricPoint(T0, 3)).ToList();

        Assert.Single(MetricDownsample.Bucket(points, 5));
    }

    [Fact]
    public void ZeroBudget_ReturnsNothing()
    {
        Assert.Empty(MetricDownsample.Bucket(Series(10), 0));
    }
}
