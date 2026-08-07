using Microsoft.Extensions.Logging.Abstractions;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// What the collector leaves on disk.
///
/// <para>Nothing ever deleted a collected file. Rename a watched container — a
/// redeploy under a new name, or an edit on the page — and its four files, up to a
/// quarter of a gigabyte, stayed in the directory for the life of the server. Worse
/// than the space: the usage figure walked the <em>configured</em> list, so those
/// bytes stopped being counted the moment they stopped being wanted. Both numbers the
/// page shows — what is used now, and the worst case — understated the real
/// consumption by an unbounded amount as names churned, which is the wrong direction
/// for a feature whose entire safety story is a disk ceiling.</para>
/// </summary>
public class LogRetentionTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("pinqops-log-retention-").FullName;

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private LogCollector Collector(params string[] containers)
    {
        var store = new LogConfigStore(Path.Combine(_directory, "logs.json"));
        store.Save(new LogCollectionConfig { Enabled = true, Containers = [.. containers] });

        return new LogCollector(store, new SystemInfoService(), NullLogger<LogCollector>.Instance, _directory);
    }

    /// <summary>Writes a container's live file and all three generations.</summary>
    private void Collected(LogCollector collector, string container, int bytesEach)
    {
        var contents = new string('x', bytesEach);
        File.WriteAllText(collector.FileFor(container), contents);
        for (var generation = 1; generation <= LogCollectionConfig.Generations; generation++)
        {
            File.WriteAllText($"{collector.FileFor(container)}.{generation}", contents);
        }
    }

    [Fact]
    public void UsageCountsEveryGenerationOfAWatchedContainer()
    {
        var collector = Collector("acme-app-1");
        Collected(collector, "acme-app-1", bytesEach: 100);

        var row = Assert.Single(collector.DiskUsage());

        Assert.Equal("acme-app-1", row.Container);
        Assert.Equal(100 * LogCollectionConfig.FilesPerContainer, row.Bytes);
    }

    /// <summary>
    /// The bytes left behind by a container that is no longer watched are still
    /// bytes. Counting only what is configured is what let the page understate the
    /// disk by an unbounded amount.
    /// </summary>
    [Fact]
    public void UsageCountsFilesLeftBehindByAContainerNoLongerWatched()
    {
        var collector = Collector("acme-app-2");
        Collected(collector, "acme-app-1", bytesEach: 100);
        Collected(collector, "acme-app-2", bytesEach: 10);

        var usage = collector.DiskUsage();

        Assert.Equal(
            100 * LogCollectionConfig.FilesPerContainer + (10 * LogCollectionConfig.FilesPerContainer),
            usage.Sum(row => row.Bytes));
        Assert.Contains(usage, row => row.Container.Contains("acme-app-1", StringComparison.Ordinal));
    }

    [Fact]
    public void DiscardingAContainerDeletesEveryGenerationOfIt()
    {
        var collector = Collector("acme-app-1");
        Collected(collector, "acme-app-1", bytesEach: 10);

        collector.DiscardCollected("acme-app-1");

        Assert.Equal(0, collector.DiskUsage().Sum(row => row.Bytes));
        Assert.Empty(Directory.GetFiles(_directory, "acme-app-1.jsonl*"));
    }

    // ---- which containers' files should go ------------------------------------

    private static LogCollectionConfig Watching(bool enabled, params string[] containers) =>
        new() { Enabled = enabled, Containers = [.. containers] };

    [Fact]
    public void AContainerTakenOffTheListHasItsFilesDiscarded() =>
        Assert.Equal(
            ["acme-app-1"],
            LogCollector.ToDiscard(Watching(true, "acme-app-2"), ["acme-app-1", "acme-app-2"]));

    /// <summary>
    /// Switching collection off keeps what has been collected. It is a pause, and an
    /// operator who turns it back on expects to find their history where they left
    /// it — deleting on the way out would make the switch destructive.
    /// </summary>
    [Fact]
    public void SwitchingCollectionOffDiscardsNothing() =>
        Assert.Empty(LogCollector.ToDiscard(Watching(false, "acme-app-1"), ["acme-app-1"]));

    /// <summary>
    /// And the low-disk pause above all. That is the one moment the history is most
    /// likely to be wanted, and it stops followers for a reason that has nothing to
    /// do with the operator changing their mind about which containers to keep.
    /// </summary>
    [Fact]
    public void APauseForLowDiskDiscardsNothing() =>
        Assert.Empty(LogCollector.ToDiscard(Watching(true, "acme-app-1"), ["acme-app-1"]));

    // ---- where a restarted follower picks up ----------------------------------

    private static readonly DateTimeOffset Now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AFirstAttachReadsTheDefaultWindow() =>
        Assert.Equal(LogCollector.DefaultSince, LogCollector.SinceFor(lastCollected: null, Now));

    /// <summary>
    /// The bug. <c>docker logs --follow</c> exits when its container stops and the
    /// reconcile tick starts it again ten seconds later, so a fixed window re-read
    /// lines that were already written — about six times over for a container that
    /// had just stopped, and continuously for one in a restart loop.
    /// </summary>
    [Fact]
    public void AFollowerThatIsStartedAgainResumesWhereItStopped()
    {
        var since = LogCollector.SinceFor(Now.AddSeconds(-10), Now);

        Assert.NotEqual(LogCollector.DefaultSince, since);
        Assert.Equal("2026-08-03T11:59:50.0000000Z", since);
    }

    /// <summary>
    /// And never further back than the default. A follower stopped for an hour still
    /// pulls in a minute, exactly as before — this can narrow the window, never
    /// widen it.
    /// </summary>
    [Fact]
    public void AFollowerStoppedForALongTimeStillReadsOnlyTheDefaultWindow() =>
        Assert.Equal(LogCollector.DefaultSince, LogCollector.SinceFor(Now.AddHours(-1), Now));

    // ---- what a search costs ---------------------------------------------------

    /// <summary>
    /// Writes <paramref name="lines"/> collected lines for a container, newest last.
    /// </summary>
    private void Collect(LogCollector collector, string container, int lines)
    {
        var at = new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
        using var writer = new StreamWriter(collector.FileFor(container));
        for (var index = 0; index < lines; index++)
        {
            writer.WriteLine(
                $$"""{"at":"{{at.AddSeconds(index):O}}","text":"line {{index}} {{new string('x', 100)}}"}""");
        }
    }

    /// <summary>
    /// A search reads as far as its limit and no further.
    ///
    /// <para>Every container's whole archive used to be parsed into memory before the
    /// first line was examined, so the limit bounded the answer and not the work. At
    /// the ceiling this feature advertises to the operator — twenty containers, four
    /// files each at 64 MB — one request tried to allocate on the order of twenty
    /// gigabytes, on the dashboard's own request thread.</para>
    ///
    /// <para>Allocation rather than elapsed time, so a loaded machine cannot change
    /// the answer. Reading everything allocates tens of megabytes here; reading five
    /// lines allocates kilobytes. There is nothing in between.</para>
    /// </summary>
    [Fact]
    public void ASearchDoesNotReadPastItsLimit()
    {
        var collector = Collector("acme-app-1");
        Collect(collector, "acme-app-1", lines: 100_000);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var taken = collector.Read("acme-app-1").Take(5).ToList();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(5, taken.Count);
        Assert.True(
            allocated < 4L * 1024 * 1024,
            $"reading five lines allocated {allocated / 1024 / 1024} MB, which means the whole archive was read");
    }

    [Fact]
    public void ASearchStillReadsEveryContainerItWasGiven()
    {
        var collector = Collector("acme-app-1", "acme-app-2");
        Collect(collector, "acme-app-1", lines: 3);
        Collect(collector, "acme-app-2", lines: 3);

        var all = collector.Read(null).ToList();

        Assert.Equal(6, all.Count);
        // Newest first, across both containers.
        Assert.Equal(all.OrderByDescending(line => line.At).Select(line => line.At), all.Select(line => line.At));
    }

    [Fact]
    public void TheResumePointIsExpressedInUtc() =>
        Assert.EndsWith(
            "Z",
            LogCollector.SinceFor(Now.AddSeconds(-5).ToOffset(TimeSpan.FromHours(3)), Now),
            StringComparison.Ordinal);
}
