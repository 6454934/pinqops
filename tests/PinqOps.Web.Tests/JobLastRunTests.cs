using PinqOps.Scheduling;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// When a scheduled job last ran, and what "never" has to look like.
///
/// <para><see cref="CronSchedule.IsDue"/> reads a null last-run as "wait for the
/// next matching minute" and a timestamp as "catch up anything missed since
/// then". Those are opposite answers, so the difference between "no history" and
/// "a history that happens to be old" is the whole decision — and a lookup that
/// cannot express the first turns every job into the second.</para>
/// </summary>
public class JobLastRunTests
{
    private const string JobId = "nightly";

    private static readonly TimeSpan Utc = TimeSpan.Zero;

    private static CronExpression At3Am()
    {
        Assert.True(CronExpression.TryParse("0 3 * * *", out var cron, out _));
        return cron!;
    }

    [Fact]
    public void AJobThatHasNeverRunHasNoLastRun() =>
        Assert.Null(JobWorkSource.LastRunOf(new Dictionary<string, DateTimeOffset>(), JobId));

    [Fact]
    public void AJobThatHasRunReportsWhen()
    {
        var at = new DateTimeOffset(2026, 8, 2, 3, 0, 0, Utc);

        Assert.Equal(at, JobWorkSource.LastRunOf(new Dictionary<string, DateTimeOffset> { [JobId] = at }, JobId));
    }

    /// <summary>
    /// The consequence, stated end to end: a job written for three in the morning
    /// and saved at noon must not run at noon.
    /// </summary>
    [Fact]
    public void ANewJobDoesNotFireTheMomentItIsSaved()
    {
        var noon = new DateTimeOffset(2026, 8, 2, 12, 0, 0, Utc);
        var noHistory = new Dictionary<string, DateTimeOffset>();

        Assert.False(CronSchedule.IsDue(At3Am(), TimeZoneInfo.Utc, noon, JobWorkSource.LastRunOf(noHistory, JobId)));
    }

    /// <summary>
    /// What the bug actually was: the beginning of time is not "never", it is a
    /// last run that every expression is overdue against. Kept as a test because it
    /// is what makes the null above load-bearing rather than incidental.
    /// </summary>
    [Fact]
    public void TheBeginningOfTimeWouldMakeEveryJobDue()
    {
        var noon = new DateTimeOffset(2026, 8, 2, 12, 0, 0, Utc);

        Assert.True(CronSchedule.IsDue(At3Am(), TimeZoneInfo.Utc, noon, default(DateTimeOffset)));
    }

    /// <summary>A job that ran yesterday still catches up today's firing.</summary>
    [Fact]
    public void AMissedFiringIsStillCaughtUp()
    {
        var yesterday = new DateTimeOffset(2026, 8, 1, 3, 0, 0, Utc);
        var noon = new DateTimeOffset(2026, 8, 2, 12, 0, 0, Utc);
        var history = new Dictionary<string, DateTimeOffset> { [JobId] = yesterday };

        Assert.True(CronSchedule.IsDue(At3Am(), TimeZoneInfo.Utc, noon, JobWorkSource.LastRunOf(history, JobId)));
    }

    /// <summary>One job's history is not another's.</summary>
    [Fact]
    public void HistoryIsPerJob()
    {
        var history = new Dictionary<string, DateTimeOffset>
        {
            ["other"] = new DateTimeOffset(2026, 8, 1, 3, 0, 0, Utc),
        };

        Assert.Null(JobWorkSource.LastRunOf(history, JobId));
    }
}
