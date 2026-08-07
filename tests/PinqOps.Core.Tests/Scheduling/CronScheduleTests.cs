using PinqOps.Scheduling;
using Xunit;

namespace PinqOps.Tests;

public class CronScheduleTests
{
    private static readonly CronExpression DailyAtThree = CronExpression.Parse("0 3 * * *");

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
        new(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc));

    private static bool IsDue(DateTimeOffset now, DateTimeOffset? lastRun) =>
        CronSchedule.IsDue(DailyAtThree, TimeZoneInfo.Utc, now, lastRun);

    /// <summary>
    /// A job that has never run waits for its next matching minute. Anchoring it at
    /// the beginning of time would fire it the moment it is saved, which is not
    /// what "03:00 every day" asks for.
    /// </summary>
    [Fact]
    public void AJobThatHasNeverRunWaitsForItsMinute()
    {
        Assert.False(IsDue(Utc(2026, 8, 1, 14, 0), lastRun: null));
        Assert.True(IsDue(Utc(2026, 8, 2, 3, 0), lastRun: null));
    }

    [Fact]
    public void AJobIsNotDueAgainWithinItsPeriod()
    {
        Assert.False(IsDue(Utc(2026, 8, 1, 14, 0), Utc(2026, 8, 1, 3, 0)));
    }

    [Fact]
    public void AJobIsDueOnceItsNextOccurrenceHasPassed()
    {
        Assert.True(IsDue(Utc(2026, 8, 2, 3, 0), Utc(2026, 8, 1, 3, 0)));
    }

    /// <summary>
    /// A host that was asleep at 03:00 runs the job when it wakes rather than
    /// skipping the day — the same catch-up BackupSchedule already gives an
    /// overdue target.
    /// </summary>
    [Fact]
    public void AMissedFiringIsCaughtUp()
    {
        Assert.True(IsDue(Utc(2026, 8, 2, 9, 15), Utc(2026, 8, 1, 3, 0)));
    }

    /// <summary>
    /// A week of downtime owes one run, not seven. The check compares against the
    /// first occurrence after the last run, and the run that follows records a new
    /// last-run — so the backlog collapses instead of replaying.
    /// </summary>
    [Fact]
    public void AWeekOfDowntimeOwesOneRun()
    {
        var wokeUp = Utc(2026, 8, 8, 9, 0);
        Assert.True(IsDue(wokeUp, Utc(2026, 8, 1, 3, 0)));

        // Having run on waking, it is not due again until tomorrow's 03:00.
        Assert.False(IsDue(wokeUp.AddMinutes(1), wokeUp));
        Assert.True(IsDue(Utc(2026, 8, 9, 3, 0), wokeUp));
    }

    /// <summary>
    /// A clock stepped backwards must not make an already-run job look due again.
    /// The next occurrence is later than the last run by construction, so a "now"
    /// behind it satisfies nothing.
    /// </summary>
    [Fact]
    public void AClockSteppedBackwardsCannotRefireAJob()
    {
        Assert.False(IsDue(Utc(2026, 8, 1, 2, 0), Utc(2026, 8, 1, 3, 0)));
    }

    /// <summary>An expression that can never fire is never due, rather than throwing
    /// on every tick for the life of the process.</summary>
    [Fact]
    public void AnExpressionThatNeverFiresIsNeverDue()
    {
        var impossible = CronExpression.Parse("0 0 30 2 *");

        Assert.False(CronSchedule.IsDue(impossible, TimeZoneInfo.Utc, Utc(2026, 8, 1, 0, 0), Utc(2026, 1, 1, 0, 0)));
    }
}
