using PinqOps.Alerts;
using Xunit;

namespace PinqOps.Tests.Alerts;

public class AlertEvaluatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int minutes) => T0.AddMinutes(minutes);

    private static AlertRule Rule(
        int forSeconds = 300,
        string comparator = AlertComparators.GreaterThan,
        double threshold = 90,
        int reNotifySeconds = 0,
        bool notifyOnResolve = true,
        int noDataAfterSeconds = 300) => new()
        {
            Id = "r1",
            Name = "Memory high",
            Metric = AlertMetrics.HostMemory,
            Comparator = comparator,
            Threshold = threshold,
            ForSeconds = forSeconds,
            ReNotifySeconds = reNotifySeconds,
            NotifyOnResolve = notifyOnResolve,
            NoDataAfterSeconds = noDataAfterSeconds,
        };

    private static AlertEvaluation Step(AlertRule rule, AlertSeriesState? state, double? value, DateTimeOffset now) =>
        AlertEvaluator.Evaluate(rule, state, string.Empty, value, now);

    /// <summary>
    /// Drives a series to Alerting the way the worker does — one breaching sample a
    /// minute for <paramref name="minutes"/> minutes. A "for" window has to be
    /// observed continuously, so a test that needs a firing series cannot get one by
    /// jumping the clock.
    /// </summary>
    private static AlertSeriesState FireBySampling(AlertRule rule, double value, int minutes)
    {
        var state = Step(rule, null, value, T0).State;
        for (var minute = 1; minute <= minutes; minute++)
        {
            state = Step(rule, state, value, At(minute)).State;
        }

        return state;
    }

    [Fact]
    public void UnderThreshold_StaysNormal_AndSaysNothing()
    {
        var result = Step(Rule(), null, 40, T0);

        Assert.Equal(AlertHealth.Normal, result.State.Health);
        Assert.Null(result.Notify);
    }

    [Fact]
    public void Breach_EntersPending_WithoutNotifying()
    {
        var result = Step(Rule(), null, 95, T0);

        Assert.Equal(AlertHealth.Pending, result.State.Health);
        Assert.Null(result.Notify);
        Assert.Equal(T0, result.State.SinceUtc);
    }

    [Fact]
    public void Breach_WithForZero_FiresImmediately()
    {
        var result = Step(Rule(forSeconds: 0), null, 95, T0);

        Assert.Equal(AlertHealth.Alerting, result.State.Health);
        Assert.Equal(AlertTransitionKind.Firing, result.Notify?.Kind);
        Assert.Equal(95, result.Notify?.Value);
    }

    [Fact]
    public void Pending_BecomesAlerting_OnlyAfterForElapses()
    {
        // Sampled every tick, the way the worker does: the window has to be
        // observed throughout, not merely waited out.
        var rule = Rule(forSeconds: 300);
        var state = Step(rule, null, 95, T0).State;

        AlertEvaluation early = default!;
        for (var minute = 1; minute <= 4; minute++)
        {
            early = Step(rule, state, 96, At(minute));
            state = early.State;
        }

        Assert.Equal(AlertHealth.Pending, early.State.Health);
        Assert.Null(early.Notify);

        var due = Step(rule, state, 97, At(5));
        Assert.Equal(AlertHealth.Alerting, due.State.Health);
        Assert.Equal(AlertTransitionKind.Firing, due.Notify?.Kind);
        Assert.Equal(At(5), due.State.FiredAtUtc);
    }

    [Fact]
    public void AForWindowSpannedByMissingSamples_DoesNotFire()
    {
        // The defaults make the no-data grace exactly as wide as the "for" window,
        // so a breach seen once, five silent minutes, and a second breach used to
        // satisfy "breaching for 5 minutes" without anything being observed in
        // between. A window nothing was seen inside proves nothing.
        var rule = Rule(forSeconds: 300, noDataAfterSeconds: 300);
        var pending = Step(rule, null, 95, T0).State;
        Assert.Equal(AlertHealth.Pending, pending.Health);

        var afterGap = Step(rule, pending, 95, At(5));

        Assert.Equal(AlertHealth.Pending, afterGap.State.Health);
        Assert.Equal(At(5), afterGap.State.SinceUtc);
        Assert.Null(afterGap.Notify);
    }

    [Fact]
    public void OneMissedSample_DoesNotRestartTheForWindow()
    {
        // A single dropped tick is the hiccup the no-data grace exists for; it must
        // not cost the window its progress either.
        var rule = Rule(forSeconds: 120);
        var pending = Step(rule, null, 95, T0).State;

        var due = Step(rule, pending, 95, At(2));

        Assert.Equal(AlertHealth.Alerting, due.State.Health);
        Assert.Equal(AlertTransitionKind.Firing, due.Notify?.Kind);
    }

    [Fact]
    public void Pending_RecoversToNormal_Silently()
    {
        var rule = Rule();
        var pending = Step(rule, null, 95, T0).State;

        var recovered = Step(rule, pending, 40, At(2));

        Assert.Equal(AlertHealth.Normal, recovered.State.Health);
        Assert.Null(recovered.Notify);
    }

    [Fact]
    public void Alerting_Resolves_AndNotifies()
    {
        var rule = Rule(forSeconds: 0);
        var firing = Step(rule, null, 95, T0).State;

        var resolved = Step(rule, firing, 40, At(10));

        Assert.Equal(AlertHealth.Normal, resolved.State.Health);
        Assert.Equal(AlertTransitionKind.Resolved, resolved.Notify?.Kind);
        Assert.Equal(TimeSpan.FromMinutes(10), resolved.Notify?.FiringFor);
        Assert.Null(resolved.State.FiredAtUtc);
    }

    [Fact]
    public void Resolve_IsSilent_WhenNotifyOnResolveIsOff()
    {
        var rule = Rule(forSeconds: 0, notifyOnResolve: false);
        var firing = Step(rule, null, 95, T0).State;

        var resolved = Step(rule, firing, 40, At(10));

        Assert.Equal(AlertHealth.Normal, resolved.State.Health);
        Assert.Null(resolved.Notify);
    }

    [Fact]
    public void Alerting_ReNotifies_OnTheRepeatInterval()
    {
        var rule = Rule(forSeconds: 0, reNotifySeconds: 1800);
        var firing = Step(rule, null, 95, T0).State;

        var tooSoon = Step(rule, firing, 95, At(29));
        Assert.Null(tooSoon.Notify);

        var due = Step(rule, tooSoon.State, 96, At(30));
        Assert.Equal(AlertTransitionKind.Reminder, due.Notify?.Kind);
        Assert.Equal(TimeSpan.FromMinutes(30), due.Notify?.FiringFor);
        Assert.Equal(At(30), due.State.LastNotifiedUtc);
    }

    [Fact]
    public void Alerting_DoesNotReNotify_WhenTheIntervalIsZero()
    {
        var rule = Rule(forSeconds: 0, reNotifySeconds: 0);
        var firing = Step(rule, null, 95, T0).State;

        Assert.Null(Step(rule, firing, 95, At(120)).Notify);
    }

    [Fact]
    public void MissingValue_KeepsHealth_UntilTheNoDataWindowElapses()
    {
        var rule = Rule(noDataAfterSeconds: 300);
        var seen = Step(rule, null, 40, T0).State;

        var blip = Step(rule, seen, null, At(1));
        Assert.Equal(AlertHealth.Normal, blip.State.Health);
        Assert.Null(blip.Notify);

        var gone = Step(rule, blip.State, null, At(6));
        Assert.Equal(AlertHealth.NoData, gone.State.Health);
        Assert.Equal(AlertTransitionKind.NoData, gone.Notify?.Kind);
    }

    [Fact]
    public void NoData_IsAnnouncedOnce()
    {
        var rule = Rule();
        var seen = Step(rule, null, 40, T0).State;
        var gone = Step(rule, seen, null, At(6)).State;

        Assert.Null(Step(rule, gone, null, At(20)).Notify);
    }

    [Fact]
    public void FiringSeries_SurvivesAShortGap_WithoutFlapping()
    {
        var rule = Rule(forSeconds: 0, noDataAfterSeconds: 300);
        var firing = Step(rule, null, 95, T0).State;

        var blip = Step(rule, firing, null, At(1));

        Assert.Equal(AlertHealth.Alerting, blip.State.Health);
        Assert.Null(blip.Notify);
    }

    [Fact]
    public void FiringSeries_ThatVanishes_IsWrittenOff_RatherThanFiringForEver()
    {
        // The container was deleted. Left Alerting it would sit on the dashboard
        // for ever with nothing able to clear it, because only a reading can, and
        // no reading is ever coming.
        var rule = Rule(forSeconds: 0, noDataAfterSeconds: 300);
        var firing = Step(rule, null, 95, T0).State;

        var gone = Step(rule, firing, null, At(6));

        Assert.Equal(AlertHealth.NoData, gone.State.Health);
        Assert.Equal(AlertTransitionKind.NoData, gone.Notify?.Kind);
    }

    [Fact]
    public void BeingWrittenOff_IsNeverReportedAsResolved()
    {
        var rule = Rule(forSeconds: 0, noDataAfterSeconds: 300);
        var firing = Step(rule, null, 95, T0).State;

        var gone = Step(rule, firing, null, At(6));

        Assert.NotEqual(AlertTransitionKind.Resolved, gone.Notify?.Kind);
    }

    [Fact]
    public void AFiringEpisodeStaysOpenAcrossNoData_SoRecoveryStillResolves()
    {
        // Paged, then the metric stopped arriving, then it came back healthy. The
        // operator is still owed the all-clear for the page they got.
        var rule = Rule(forSeconds: 0, noDataAfterSeconds: 300);
        var firing = Step(rule, null, 95, T0).State;
        var gone = Step(rule, firing, null, At(6)).State;

        var recovered = Step(rule, gone, 40, At(20));

        Assert.Equal(AlertHealth.Normal, recovered.State.Health);
        Assert.Equal(AlertTransitionKind.Resolved, recovered.Notify?.Kind);
        Assert.Null(recovered.State.FiredAtUtc);
    }

    [Fact]
    public void AnEpisodeThatSurvivesNoData_IsNotAnnouncedTwice()
    {
        // Same open episode, but the readings come back STILL breaching. That used
        // to call Fire again: a second alert_firing with no alert_resolved between
        // them, and FiredAtUtc overwritten so the eventual resolve reported minutes
        // instead of the episode's real length.
        var rule = Rule(forSeconds: 0, noDataAfterSeconds: 300);
        var firing = Step(rule, null, 95, T0);
        Assert.Equal(AlertTransitionKind.Firing, firing.Notify?.Kind);

        var gone = Step(rule, firing.State, null, At(6)).State;
        Assert.Equal(AlertHealth.NoData, gone.Health);

        var back = Step(rule, gone, 95, At(7));

        Assert.Equal(AlertHealth.Alerting, back.State.Health);
        Assert.Null(back.Notify);
        Assert.Equal(T0, back.State.FiredAtUtc);

        // And the eventual resolve still measures from the original firing.
        var cleared = Step(rule, back.State, 10, At(8));
        Assert.Equal(AlertTransitionKind.Resolved, cleared.Notify?.Kind);
        Assert.Equal(TimeSpan.FromMinutes(8), cleared.Notify?.FiringFor);
    }

    [Fact]
    public void AnUnannouncedEpisodeThatSurvivesNoData_IsStillAnnouncedOnReturn()
    {
        // Fired while silenced, went quiet, came back breaching after the silence
        // lifted. The episode is the same one, but nobody has heard about it yet, so
        // this return is its announcement.
        var rule = Rule(forSeconds: 0, noDataAfterSeconds: 300);
        rule.SilencedUntil = At(3);

        var muted = Step(rule, null, 95, T0);
        Assert.True(muted.Suppressed);

        var gone = Step(rule, muted.State, null, At(6)).State;
        Assert.Equal(AlertHealth.NoData, gone.Health);

        var back = Step(rule, gone, 95, At(7));

        Assert.Equal(AlertTransitionKind.Firing, back.Notify?.Kind);
        Assert.False(back.Suppressed);
        Assert.Equal(T0, back.State.FiredAtUtc);
    }

    [Fact]
    public void AnUnannouncedFiring_ThatVanishesAndRecovers_StillSaysNothing()
    {
        var rule = Rule(forSeconds: 0, noDataAfterSeconds: 300);
        rule.SilencedUntil = At(600);
        var mutedFiring = Step(rule, null, 95, T0).State;
        var gone = Step(rule, mutedFiring, null, At(6)).State;

        rule.SilencedUntil = null;
        var recovered = Step(rule, gone, 40, At(20));

        Assert.Equal(AlertHealth.Normal, recovered.State.Health);
        Assert.Null(recovered.Notify);
    }

    [Fact]
    public void NoData_ToNormal_DoesNotClaimSomethingResolved()
    {
        var rule = Rule();
        var seen = Step(rule, null, 40, T0).State;
        var gone = Step(rule, seen, null, At(6)).State;

        var back = Step(rule, gone, 40, At(10));

        Assert.Equal(AlertHealth.Normal, back.State.Health);
        Assert.Null(back.Notify);
    }

    [Fact]
    public void ComingBackFromNoData_RestartsTheForWindow()
    {
        var rule = Rule(forSeconds: 300);
        var seen = Step(rule, null, 40, T0).State;
        var gone = Step(rule, seen, null, At(6)).State;

        var breaching = Step(rule, gone, 95, At(10));

        Assert.Equal(AlertHealth.Pending, breaching.State.Health);
        Assert.Equal(At(10), breaching.State.SinceUtc);
        Assert.Null(breaching.Notify);
    }

    [Fact]
    public void Silence_SuppressesDelivery_ButTracksHealth()
    {
        var rule = Rule(forSeconds: 0);
        rule.SilencedUntil = At(60);

        var result = Step(rule, null, 95, T0);

        Assert.Equal(AlertHealth.Alerting, result.State.Health);
        Assert.Null(result.Notify);
        Assert.Null(result.State.LastNotifiedUtc);
    }

    [Fact]
    public void Silence_StillReportsWhatHappened_ForTheTrail()
    {
        // Suppressing delivery must not erase the event: otherwise "why was I not
        // paged at three in the morning?" has no answer anywhere.
        var rule = Rule(forSeconds: 0);
        rule.SilencedUntil = At(60);

        var result = Step(rule, null, 95, T0);

        Assert.True(result.Suppressed);
        Assert.Equal(AlertTransitionKind.Firing, result.Transition?.Kind);
        Assert.Null(result.Notify);
    }

    [Fact]
    public void AnUneventfulTick_IsNotMarkedSuppressed()
    {
        var result = Step(Rule(), null, 40, T0);

        Assert.False(result.Suppressed);
        Assert.Null(result.Transition);
    }

    [Fact]
    public void SilenceDelaysAnAnnouncement_RatherThanSwallowingIt()
    {
        var rule = Rule(forSeconds: 0);
        rule.SilencedUntil = At(60);
        var mutedFiring = Step(rule, null, 95, T0).State;

        rule.SilencedUntil = null;
        var announced = Step(rule, mutedFiring, 95, At(61));

        Assert.Equal(AlertTransitionKind.Firing, announced.Notify?.Kind);
        Assert.Equal(At(61), announced.State.LastNotifiedUtc);
    }

    [Fact]
    public void AFiringNobodyHeardAbout_NeverSendsAResolve()
    {
        var rule = Rule(forSeconds: 0);
        rule.SilencedUntil = At(60);
        var mutedFiring = Step(rule, null, 95, T0).State;

        rule.SilencedUntil = null;
        var recovered = Step(rule, mutedFiring, 40, At(30));

        Assert.Equal(AlertHealth.Normal, recovered.State.Health);
        Assert.Null(recovered.Notify);
    }

    [Fact]
    public void AnOpenEpisode_ResolvesEvenIfTheSeriesIsBackInPending()
    {
        // Paged, the metric went quiet, came back still breaching (so the "for"
        // window restarted), then cleared before it could fire again. The original
        // page is still outstanding, so it still gets its all-clear.
        var rule = Rule(forSeconds: 300, noDataAfterSeconds: 300);
        var firing = FireBySampling(rule, 95, minutes: 5);
        Assert.Equal(AlertHealth.Alerting, firing.Health);

        var gone = Step(rule, firing, null, At(15)).State;
        Assert.Equal(AlertHealth.NoData, gone.Health);

        var breachingAgain = Step(rule, gone, 95, At(20)).State;
        Assert.Equal(AlertHealth.Pending, breachingAgain.Health);

        var cleared = Step(rule, breachingAgain, 10, At(22));

        Assert.Equal(AlertHealth.Normal, cleared.State.Health);
        Assert.Equal(AlertTransitionKind.Resolved, cleared.Notify?.Kind);
    }

    [Fact]
    public void BackwardsClockJump_DoesNotFireEarly()
    {
        var rule = Rule(forSeconds: 300);
        var pending = Step(rule, null, 95, At(10)).State;

        // The clock steps back behind SinceUtc: elapsed must clamp to zero, not
        // go negative and certainly not overshoot the window.
        var jumped = Step(rule, pending, 95, At(-30));

        Assert.Equal(AlertHealth.Pending, jumped.State.Health);
        Assert.Null(jumped.Notify);
    }

    [Theory]
    [InlineData(AlertComparators.GreaterThan, 90, 90, false)]
    [InlineData(AlertComparators.GreaterThan, 90, 91, true)]
    [InlineData(AlertComparators.GreaterOrEqual, 90, 90, true)]
    [InlineData(AlertComparators.GreaterOrEqual, 90, 89, false)]
    [InlineData(AlertComparators.LessThan, 10, 10, false)]
    [InlineData(AlertComparators.LessThan, 10, 9, true)]
    [InlineData(AlertComparators.LessOrEqual, 10, 10, true)]
    [InlineData(AlertComparators.LessOrEqual, 10, 11, false)]
    public void Comparators_HandleTheBoundaryExactly(
        string comparator, double threshold, double value, bool fires)
    {
        var rule = Rule(forSeconds: 0, comparator: comparator, threshold: threshold);

        var result = Step(rule, null, value, T0);

        Assert.Equal(fires, result.Notify is not null);
    }

    [Fact]
    public void SeriesAreIndependent()
    {
        var rule = new AlertRule
        {
            Id = "r2",
            Name = "Container CPU",
            Metric = AlertMetrics.ContainerCpu,
            Target = "*",
            Threshold = 90,
            ForSeconds = 0,
        };

        var busy = AlertEvaluator.Evaluate(rule, null, "app", 95, T0);
        var idle = AlertEvaluator.Evaluate(rule, null, "db", 5, T0);

        Assert.Equal(AlertHealth.Alerting, busy.State.Health);
        Assert.NotNull(busy.Notify);
        Assert.Equal(AlertHealth.Normal, idle.State.Health);
        Assert.Null(idle.Notify);
        Assert.NotEqual(busy.State.Key, idle.State.Key);
    }

    [Fact]
    public void EvaluationDoesNotMutateThePreviousState()
    {
        var rule = Rule(forSeconds: 0);
        var before = Step(rule, null, 40, T0).State;
        var snapshot = before.Health;

        Step(rule, before, 95, At(1));

        Assert.Equal(snapshot, before.Health);
    }
}
