using PinqOps.Alerts;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// The disk figure the Logs page shows before collection is switched on.
///
/// <para>It is the whole promise the feature makes: a collector writes to disk at
/// a rate somebody else controls, so the operator is told the ceiling up front.
/// A ceiling that is lower than what the same response reports as already used is
/// worse than no ceiling — it is a number that says the guard is holding while
/// the evidence beside it says otherwise.</para>
///
/// <para>Checked against the rotator rather than against a copy of the sum:
/// <c>generations</c> counts the files kept <em>beside</em> the live one, and the
/// rotator's own summary warns that reading it as a total makes every retention
/// sum come out one file short. That is exactly what had happened, so the test
/// that matters is the one that asks the rotator how many files it actually
/// leaves behind.</para>
/// </summary>
public class LogDiskBudgetTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("pinqops-log-budget-").FullName;

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Rotates a log until it has all the generations it will ever keep, and
    /// answers how many files that left on disk.
    /// </summary>
    private int FilesKeptAfterRotating()
    {
        const int TinyCeiling = 64;
        var path = Path.Combine(_directory, "web.jsonl");
        var log = new RotatingJsonLog(path, LogCollectionConfig.Generations, maxBytes: TinyCeiling);

        // Comfortably more rotations than there are generations, so the count has
        // settled at whatever the rotator keeps rather than at how much was written.
        for (var line = 0; line < (LogCollectionConfig.Generations + 5) * 4; line++)
        {
            log.Append(new string('x', TinyCeiling));
        }

        return Directory.GetFiles(_directory, "web.jsonl*").Length;
    }

    [Fact]
    public void TheCeilingCountsEveryFileTheRotatorKeeps()
    {
        var files = FilesKeptAfterRotating();

        Assert.Equal(
            files * LogCollectionConfig.MaximumBytesPerContainer,
            LogCollectionConfig.WorstCaseBytes(containers: 1));
    }

    /// <summary>
    /// Stated separately because it is the sentence the rotator's summary warns
    /// about: the live file counts too.
    /// </summary>
    [Fact]
    public void TheLiveFileIsOneOfThem() =>
        Assert.Equal(LogCollectionConfig.Generations + 1, FilesKeptAfterRotating());

    [Fact]
    public void TheCeilingScalesWithTheNumberOfContainers() =>
        Assert.Equal(
            3 * LogCollectionConfig.WorstCaseBytes(containers: 1),
            LogCollectionConfig.WorstCaseBytes(containers: 3));

    /// <summary>
    /// More containers than may ever be followed cost no more than the cap, which
    /// is what makes the cap a budget rather than a suggestion.
    /// </summary>
    [Fact]
    public void PastTheContainerCapTheCeilingStops() =>
        Assert.Equal(
            LogCollectionConfig.WorstCaseBytes(LogCollectionConfig.MaximumContainers),
            LogCollectionConfig.WorstCaseBytes(LogCollectionConfig.MaximumContainers + 50));

    [Fact]
    public void NoContainersCostNothing() =>
        Assert.Equal(0, LogCollectionConfig.WorstCaseBytes(containers: 0));
}
