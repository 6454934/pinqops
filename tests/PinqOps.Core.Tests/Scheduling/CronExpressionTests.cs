using PinqOps.Scheduling;
using Xunit;

namespace PinqOps.Tests;

public class CronExpressionTests
{
    /// <summary>
    /// A time zone built here rather than looked up by name: the DST cases below
    /// have to behave identically on a developer's Windows box and on CI, and the
    /// set of zones a machine knows — and what they did in a given year — is not
    /// something a test should depend on.
    ///
    /// Standard time is UTC+1, daylight time UTC+2. The clocks go forward on
    /// 30 March at 02:00 (so 02:00–02:59 does not exist that day) and back on
    /// 26 October at 03:00 (so 02:00–02:59 happens twice).
    /// </summary>
    private static TimeZoneInfo DaylightSavingZone()
    {
        var forward = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 30);
        var back = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 26);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1), forward, back);

        return TimeZoneInfo.CreateCustomTimeZone(
            "pinqops-test-dst", TimeSpan.FromHours(1),
            "pinqops test", "pinqops test standard", "pinqops test daylight", [rule]);
    }

    private static DateTimeOffset Instant(TimeZoneInfo zone, int year, int month, int day, int hour, int minute) =>
        new(TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified), zone), TimeSpan.Zero);

    private static DateTime Local(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);

    // ---- field syntax -------------------------------------------------------

    [Fact]
    public void EveryMinuteMatchesEveryMinute()
    {
        var cron = CronExpression.Parse("* * * * *");

        Assert.True(cron.Matches(Local(2026, 8, 1, 0, 0)));
        Assert.True(cron.Matches(Local(2026, 8, 1, 13, 47)));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(5, true)]
    [InlineData(55, true)]
    [InlineData(1, false)]
    [InlineData(59, false)]
    public void AStepSelectsEveryNthValue(int minute, bool expected)
    {
        Assert.Equal(expected, CronExpression.Parse("*/5 * * * *").Matches(Local(2026, 8, 1, 10, minute)));
    }

    [Theory]
    [InlineData(8, false)]
    [InlineData(9, true)]
    [InlineData(17, true)]
    [InlineData(18, false)]
    public void ARangeIsInclusiveAtBothEnds(int hour, bool expected)
    {
        Assert.Equal(expected, CronExpression.Parse("0 9-17 * * *").Matches(Local(2026, 8, 1, hour, 0)));
    }

    [Fact]
    public void AListSelectsEachEntry()
    {
        var cron = CronExpression.Parse("0 0,12 * * *");

        Assert.True(cron.Matches(Local(2026, 8, 1, 0, 0)));
        Assert.True(cron.Matches(Local(2026, 8, 1, 12, 0)));
        Assert.False(cron.Matches(Local(2026, 8, 1, 6, 0)));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(6, true)]
    [InlineData(18, true)]
    [InlineData(7, false)]
    public void AStepAppliesWithinARange(int hour, bool expected)
    {
        Assert.Equal(expected, CronExpression.Parse("0 0-23/6 * * *").Matches(Local(2026, 8, 1, hour, 0)));
    }

    /// <summary>"5/10" is Vixie cron's "from 5 to the end of the field, every 10".</summary>
    [Theory]
    [InlineData(5, true)]
    [InlineData(15, true)]
    [InlineData(55, true)]
    [InlineData(0, false)]
    public void AValueWithAStepRunsToTheEndOfTheField(int minute, bool expected)
    {
        Assert.Equal(expected, CronExpression.Parse("5/10 * * * *").Matches(Local(2026, 8, 1, 10, minute)));
    }

    [Fact]
    public void MonthsMayBeNamed()
    {
        var cron = CronExpression.Parse("0 0 1 JAN *");

        Assert.True(cron.Matches(Local(2026, 1, 1, 0, 0)));
        Assert.False(cron.Matches(Local(2026, 2, 1, 0, 0)));
    }

    [Fact]
    public void WeekdaysMayBeNamed()
    {
        // 2026-08-03 is a Monday.
        var cron = CronExpression.Parse("0 0 * * MON");

        Assert.True(cron.Matches(Local(2026, 8, 3, 0, 0)));
        Assert.False(cron.Matches(Local(2026, 8, 4, 0, 0)));
    }

    /// <summary>Both 0 and 7 spell Sunday; 2026-08-02 is a Sunday.</summary>
    [Theory]
    [InlineData("0 0 * * 0")]
    [InlineData("0 0 * * 7")]
    [InlineData("0 0 * * SUN")]
    public void SundayHasThreeSpellings(string expression)
    {
        Assert.True(CronExpression.Parse(expression).Matches(Local(2026, 8, 2, 0, 0)));
    }

    // ---- the day-of-month / day-of-week rule --------------------------------

    /// <summary>
    /// crontab(5)'s rule: with both day fields narrowed, either one matching is
    /// enough. "0 0 13 * FRI" is every 13th <em>and</em> every Friday — not only
    /// Friday the 13th, which is what everyone expects it to mean and what would
    /// silently skip most of the runs.
    /// </summary>
    [Fact]
    public void NarrowedDayFieldsAreCombinedWithOr()
    {
        var cron = CronExpression.Parse("0 0 13 * FRI");

        Assert.True(cron.Matches(Local(2026, 8, 13, 0, 0)));  // a Thursday, but the 13th
        Assert.True(cron.Matches(Local(2026, 8, 7, 0, 0)));   // a Friday, but not the 13th
        Assert.False(cron.Matches(Local(2026, 8, 6, 0, 0)));  // neither
    }

    [Fact]
    public void OneNarrowedDayFieldStandsAlone()
    {
        var cron = CronExpression.Parse("0 0 13 * *");

        Assert.True(cron.Matches(Local(2026, 8, 13, 0, 0)));
        Assert.False(cron.Matches(Local(2026, 8, 7, 0, 0)));
    }

    // ---- macros -------------------------------------------------------------

    [Theory]
    [InlineData("@hourly", "0 * * * *")]
    [InlineData("@daily", "0 0 * * *")]
    [InlineData("@midnight", "0 0 * * *")]
    [InlineData("@weekly", "0 0 * * 0")]
    [InlineData("@monthly", "0 0 1 * *")]
    [InlineData("@yearly", "0 0 1 1 *")]
    [InlineData("@annually", "0 0 1 1 *")]
    public void MacrosExpandToTheirFields(string macro, string expanded)
    {
        Assert.Equal(expanded, CronExpression.Parse(macro).ToString());
    }

    // ---- what is refused ----------------------------------------------------

    /// <summary>
    /// Quartz constructs turn up in expressions copied from Spring documentation.
    /// Treating them as something else would run a job on a day nobody asked for,
    /// so they are refused by name.
    /// </summary>
    [Theory]
    [InlineData("0 0 L * *")]
    [InlineData("0 0 15W * *")]
    [InlineData("0 0 * * 6#3")]
    [InlineData("0 0 13 * ?")]
    public void QuartzOnlyConstructsAreRefused(string expression)
    {
        var exception = Assert.Throws<ArgumentException>(() => CronExpression.Parse(expression));
        Assert.Contains("does not support", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("* * * *")]
    [InlineData("* * * * * *")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("0 0 0 * *")]
    [InlineData("0 0 32 * *")]
    [InlineData("0 0 * 13 *")]
    [InlineData("0 0 * * 8")]
    [InlineData("17-5 * * * *")]
    [InlineData("*/0 * * * *")]
    [InlineData("*/-1 * * * *")]
    [InlineData("abc * * * *")]
    [InlineData("0,,5 * * * *")]
    [InlineData("@reboot")]
    [InlineData("@fortnightly")]
    public void MalformedExpressionsAreRefusedWithAReason(string expression)
    {
        Assert.False(CronExpression.TryParse(expression, out var parsed, out var error));
        Assert.Null(parsed);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    // ---- next occurrence ----------------------------------------------------

    [Fact]
    public void NextIsStrictlyAfterTheGivenInstant()
    {
        var cron = CronExpression.Parse("*/5 * * * *");
        var at = Instant(TimeZoneInfo.Utc, 2026, 8, 1, 10, 5);

        Assert.Equal(Instant(TimeZoneInfo.Utc, 2026, 8, 1, 10, 10), cron.Next(at, TimeZoneInfo.Utc));
    }

    [Fact]
    public void NextCrossesIntoTheFollowingDay()
    {
        var cron = CronExpression.Parse("0 3 * * *");
        var at = Instant(TimeZoneInfo.Utc, 2026, 8, 1, 4, 0);

        Assert.Equal(Instant(TimeZoneInfo.Utc, 2026, 8, 2, 3, 0), cron.Next(at, TimeZoneInfo.Utc));
    }

    /// <summary>The 29th of February only exists in a leap year, so the search has
    /// to skip whole months rather than walk minutes for two years.</summary>
    [Fact]
    public void NextFindsTheLeapDay()
    {
        var cron = CronExpression.Parse("0 0 29 2 *");
        var at = Instant(TimeZoneInfo.Utc, 2026, 3, 1, 0, 0);

        Assert.Equal(Instant(TimeZoneInfo.Utc, 2028, 2, 29, 0, 0), cron.Next(at, TimeZoneInfo.Utc));
    }

    /// <summary>An expression can name a date that never happens. Answering null
    /// is what stops the search from running forever.</summary>
    [Fact]
    public void ADateThatNeverHappensHasNoNext()
    {
        var cron = CronExpression.Parse("0 0 30 2 *");

        Assert.Null(cron.Next(Instant(TimeZoneInfo.Utc, 2026, 1, 1, 0, 0), TimeZoneInfo.Utc));
    }

    // ---- daylight saving ----------------------------------------------------

    /// <summary>
    /// On the day the clocks go forward there is no 02:30, so the job does not run
    /// that day. Pulling it to 03:30 or pushing it to 01:30 would run it at a time
    /// the expression does not name.
    /// </summary>
    [Fact]
    public void AFiringInsideTheSpringForwardGapIsSkipped()
    {
        var zone = DaylightSavingZone();
        Assert.True(zone.IsInvalidTime(Local(2026, 3, 30, 2, 30)), "the test zone should have a gap at 02:30 on 30 March.");

        var cron = CronExpression.Parse("30 2 * * *");
        var next = cron.Next(Instant(zone, 2026, 3, 29, 12, 0), zone);

        Assert.Equal(Instant(zone, 2026, 3, 31, 2, 30), next);
    }

    /// <summary>
    /// A */30 job loses only the minutes inside the gap, not the rest of the day —
    /// stepping a minute at a time through the gap rather than skipping the day is
    /// what makes that hold.
    /// </summary>
    [Fact]
    public void AFrequentJobResumesAfterTheGap()
    {
        var zone = DaylightSavingZone();
        var cron = CronExpression.Parse("*/30 * * * *");

        var next = cron.Next(Instant(zone, 2026, 3, 30, 1, 30), zone);

        Assert.Equal(Instant(zone, 2026, 3, 30, 3, 0), next);
    }

    /// <summary>
    /// On the day the clocks go back, 02:30 happens twice. The nightly job must
    /// produce one firing, not two — running a backup or a billing job twice a year
    /// is the classic version of this bug.
    /// </summary>
    [Fact]
    public void AFiringInsideTheFallBackOverlapHappensOnce()
    {
        var zone = DaylightSavingZone();
        Assert.True(zone.IsAmbiguousTime(Local(2026, 10, 26, 2, 30)), "the test zone should be ambiguous at 02:30 on 26 October.");

        var cron = CronExpression.Parse("30 2 * * *");
        var first = cron.Next(Instant(zone, 2026, 10, 25, 12, 0), zone);

        Assert.NotNull(first);
        // The standard-time reading — the second of the two 02:30s, at 01:30 UTC.
        Assert.Equal(new DateTime(2026, 10, 26, 1, 30, 0, DateTimeKind.Utc), first!.Value.UtcDateTime);

        // And the one after it is the next day, not the same day's other 02:30.
        Assert.Equal(new DateTime(2026, 10, 27, 1, 30, 0, DateTimeKind.Utc), cron.Next(first.Value, zone)!.Value.UtcDateTime);
    }

    /// <summary>
    /// The whole reason the search runs on wall-clock time: "03:00 every day" stays
    /// 03:00 for the operator across the transition, even though the UTC instant
    /// moves by an hour.
    /// </summary>
    [Fact]
    public void ADailyJobKeepsItsWallClockTimeAcrossTheTransition()
    {
        var zone = DaylightSavingZone();
        var cron = CronExpression.Parse("0 3 * * *");

        var beforeTransition = cron.Next(Instant(zone, 2026, 3, 28, 12, 0), zone);
        var afterTransition = cron.Next(Instant(zone, 2026, 3, 31, 12, 0), zone);

        Assert.Equal(new DateTime(2026, 3, 29, 2, 0, 0, DateTimeKind.Utc), beforeTransition!.Value.UtcDateTime);
        Assert.Equal(new DateTime(2026, 4, 1, 1, 0, 0, DateTimeKind.Utc), afterTransition!.Value.UtcDateTime);
    }
}
