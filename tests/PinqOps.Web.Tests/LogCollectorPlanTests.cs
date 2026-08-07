using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// Which followers should be running.
///
/// <para>Starting one is the easy half. The half that matters is stopping one,
/// because a follower is a process writing to disk at a rate somebody else
/// controls — and the three ways it should stop are the three the feature's own
/// safety story is made of: the operator switches collection off, the operator
/// takes a container off the list, or the disk runs low. A collector that only
/// ever starts followers turns each of those into a control that appears to work
/// and does nothing.</para>
///
/// <para>The low-disk case is the sharpest: it is the one moment when continuing
/// to write is actively harmful, and it is meant to be the guard that prevents a
/// full disk taking the database, the proxy and the deploy down with it.</para>
/// </summary>
public class LogCollectorPlanTests
{
    private const long PlentyOfDisk = 500L * 1024 * 1024 * 1024;

    private const long AlmostNoDisk = LogCollector.MinimumFreeBytes - 1;

    private static LogCollectionConfig Watching(params string[] containers) =>
        new() { Enabled = true, Containers = [.. containers] };

    // ---- starting -----------------------------------------------------------

    [Fact]
    public void AConfiguredContainerWithNoFollowerIsStarted()
    {
        var (toStart, toStop) = LogCollector.Plan(Watching("web", "db"), PlentyOfDisk, []);

        Assert.Equal(["web", "db"], toStart);
        Assert.Empty(toStop);
    }

    [Fact]
    public void AContainerAlreadyFollowedIsNotStartedAgain()
    {
        var (toStart, _) = LogCollector.Plan(Watching("web", "db"), PlentyOfDisk, ["web"]);

        Assert.Equal(["db"], toStart);
    }

    [Fact]
    public void NoMoreThanTheCeilingAreEverFollowed()
    {
        var many = Enumerable.Range(0, LogCollectionConfig.MaximumContainers + 5)
            .Select(index => $"c{index:00}")
            .ToArray();

        var (toStart, _) = LogCollector.Plan(Watching(many), PlentyOfDisk, []);

        Assert.Equal(LogCollectionConfig.MaximumContainers, toStart.Count);
    }

    // ---- stopping -----------------------------------------------------------

    [Fact]
    public void TurningCollectionOffStopsEveryFollower()
    {
        var config = new LogCollectionConfig { Enabled = false, Containers = ["web", "db"] };

        var (toStart, toStop) = LogCollector.Plan(config, PlentyOfDisk, ["web", "db"]);

        Assert.Empty(toStart);
        Assert.Equal(["db", "web"], [.. toStop.Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public void AContainerTakenOffTheListStopsBeingFollowed()
    {
        var (toStart, toStop) = LogCollector.Plan(Watching("web"), PlentyOfDisk, ["web", "db"]);

        Assert.Empty(toStart);
        Assert.Equal(["db"], toStop);
    }

    /// <summary>
    /// The one case where continuing to write is the harm itself.
    /// </summary>
    [Fact]
    public void RunningLowOnDiskStopsEveryFollower()
    {
        var (toStart, toStop) = LogCollector.Plan(Watching("web", "db"), AlmostNoDisk, ["web", "db"]);

        Assert.Empty(toStart);
        Assert.Equal(["db", "web"], [.. toStop.Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public void RunningLowOnDiskStartsNothingEither()
    {
        var (toStart, _) = LogCollector.Plan(Watching("web"), AlmostNoDisk, []);

        Assert.Empty(toStart);
    }

    /// <summary>
    /// A container over the ceiling is not followed, so one already running past it
    /// has to be stopped rather than left as a way around the cap.
    /// </summary>
    [Fact]
    public void AFollowerPastTheCeilingIsStopped()
    {
        var many = Enumerable.Range(0, LogCollectionConfig.MaximumContainers + 1)
            .Select(index => $"c{index:00}")
            .ToArray();

        var (_, toStop) = LogCollector.Plan(Watching(many), PlentyOfDisk, [many[^1]]);

        Assert.Equal([many[^1]], toStop);
    }

    /// <summary>An unknown free-space reading is not a reason to stop collecting.</summary>
    [Fact]
    public void AnUnreadableDiskReadingChangesNothing()
    {
        var (toStart, toStop) = LogCollector.Plan(Watching("web"), freeBytes: null, ["web"]);

        Assert.Empty(toStart);
        Assert.Empty(toStop);
    }
}
