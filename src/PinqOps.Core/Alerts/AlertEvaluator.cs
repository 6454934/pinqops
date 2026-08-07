namespace PinqOps.Alerts;

/// <summary>What just happened to a series, and therefore what to send.</summary>
public enum AlertTransitionKind
{
    /// <summary>The rule started firing (or is being announced after a silence lifted).</summary>
    Firing,

    /// <summary>The condition cleared after a firing that was announced.</summary>
    Resolved,

    /// <summary>Still firing; the rule's repeat interval came round again.</summary>
    Reminder,

    /// <summary>The series stopped producing values for longer than the rule allows.</summary>
    NoData,
}

/// <summary>One thing worth telling somebody about.</summary>
public sealed record AlertTransition
{
    public required AlertRule Rule { get; init; }

    /// <summary>Container name, or empty for a host metric.</summary>
    public required string Series { get; init; }

    public required AlertTransitionKind Kind { get; init; }

    public required DateTimeOffset At { get; init; }

    /// <summary>The sample that caused it; null for <see cref="AlertTransitionKind.NoData"/>.</summary>
    public double? Value { get; init; }

    /// <summary>How long the rule had been firing, on a resolve or a reminder.</summary>
    public TimeSpan? FiringFor { get; init; }
}

/// <summary>
/// The evaluator's answer for one series: the state to persist, what happened,
/// and whether it may be delivered.
///
/// <see cref="Transition"/> and <see cref="Notify"/> are separate so that a
/// silence can suppress delivery without erasing the event — otherwise "why was
/// I not paged at three in the morning?" would have no answer anywhere.
/// </summary>
public sealed record AlertEvaluation(AlertSeriesState State, AlertTransition? Transition, bool Suppressed = false)
{
    /// <summary>What to send: the transition, or null when the rule is silenced.</summary>
    public AlertTransition? Notify => Suppressed ? null : Transition;
}

/// <summary>
/// The alert state machine: Normal → Pending → Alerting → Normal, with NoData off
/// to one side. Deliberately pure — no clock, no files, no HTTP — because every
/// interesting case here is a timing case, and timing cases are only testable
/// when the caller supplies the time.
///
/// Four behaviours are load-bearing and easy to get wrong:
///
/// <list type="bullet">
/// <item>A series that goes quiet keeps its health for
/// <see cref="AlertRule.NoDataAfterSeconds"/> before reporting no data, so one
/// missed sample does not flap the state. That grace applies to a firing series
/// too, and past it a firing series is written off like any other — otherwise a
/// container deleted while its alert was up leaves that alert on screen for
/// ever, with nothing left that could ever clear it.</item>
/// <item>Being written off is <em>not</em> a resolve. A firing episode stays open
/// across NoData (<see cref="AlertSeriesState.FiredAtUtc"/> is what remembers
/// it), so when readings come back healthy the operator still gets the
/// "resolved" they are owed — and never gets an all-clear that only means the
/// metric stopped arriving.</item>
/// <item>A silence delays an announcement rather than swallowing it: a rule that
/// fires while muted is announced when the silence lifts, if it is still firing.
/// Correspondingly, a firing that was never announced never sends a
/// "resolved".</item>
/// <item>Durations never go negative, so a clock stepped backwards can delay a
/// transition but never trigger one early.</item>
/// </list>
///
/// The invariant that makes the silence handling work:
/// <see cref="AlertSeriesState.LastNotifiedUtc"/> is non-null exactly when the
/// current firing episode has been announced. It is cleared when an episode
/// starts and when one ends.
/// </summary>
public static class AlertEvaluator
{
    /// <summary>
    /// The longest gap between samples that still counts as continuous
    /// observation — one missed tick. Past it a "for" window restarts, because a
    /// window nothing was observed inside proves nothing: the two defaults make
    /// the no-data grace exactly as wide as the "for" window, so without this the
    /// entire window could be spanned by missing samples and the rule would fire on
    /// a breach that was never seen to be sustained.
    ///
    /// This is the same tolerance <see cref="AlertStateHygiene.ResetAfterDowntime"/>
    /// is called with, and for the same reason.
    /// </summary>
    public static readonly TimeSpan DefaultMaximumSampleGap = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Folds one sampled value into one series' state.
    /// <paramref name="value"/> is null when the series produced no reading.
    /// </summary>
    /// <param name="maximumSampleGap">
    /// How long a gap between readings still counts as continuous observation;
    /// see <see cref="DefaultMaximumSampleGap"/>.
    /// </param>
    public static AlertEvaluation Evaluate(
        AlertRule rule,
        AlertSeriesState? previous,
        string series,
        double? value,
        DateTimeOffset now,
        TimeSpan? maximumSampleGap = null)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var state = previous?.Clone() ?? new AlertSeriesState { RuleId = rule.Id, Series = series };
        state.RuleId = rule.Id;
        state.Series = series;

        var transition = value is null
            ? StepWithoutValue(rule, state, series, now)
            : StepWithValue(rule, state, series, value.Value, now, maximumSampleGap ?? DefaultMaximumSampleGap);

        // A silence suppresses delivery, not evaluation: the dashboard still shows
        // the real health and the trail still records what happened.
        // LastNotifiedUtc is deliberately left alone, so an unannounced firing is
        // announced once the silence lifts.
        if (transition is null || rule.IsSilenced(now))
        {
            if (transition is not null)
            {
                // Marked so the trail can tell "this is new" from "this is the same
                // suppressed condition, recomputed a minute later". Advanced only
                // when the entry is actually trailed — stamping every tick would
                // reset the repeat clock each minute and the interval in
                // IsWorthTrailing could never elapse.
                if (previous is null || IsWorthTrailing(rule, previous, transition))
                {
                    state.LastSuppressedUtc = now;
                    state.LastSuppressedKind = transition.Kind;
                }
            }

            return new AlertEvaluation(state, transition, Suppressed: transition is not null);
        }

        state.LastSuppressedUtc = null;
        state.LastSuppressedKind = null;

        if (transition.Kind is AlertTransitionKind.Firing or AlertTransitionKind.Reminder)
        {
            state.LastNotifiedUtc = now;
        }

        return new AlertEvaluation(state, transition);
    }

    /// <summary>
    /// Whether a suppressed transition is worth another trail entry, given what was
    /// last written for this series. A changed kind always is; the same kind again
    /// only once the rule's repeat interval has passed (or, for a rule that never
    /// repeats, not at all).
    /// </summary>
    public static bool IsWorthTrailing(AlertRule rule, AlertSeriesState previous, AlertTransition transition)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(transition);

        if (previous.LastSuppressedKind is not { } lastKind || previous.LastSuppressedUtc is not { } lastAt)
        {
            return true;
        }

        if (lastKind != transition.Kind)
        {
            return true;
        }

        return rule.ReNotifySeconds > 0
            && Elapsed(lastAt, transition.At) >= TimeSpan.FromSeconds(rule.ReNotifySeconds);
    }

    private static AlertTransition? StepWithoutValue(
        AlertRule rule, AlertSeriesState state, string series, DateTimeOffset now)
    {
        // Start the no-data clock at the first evaluation, so a series that never
        // produces a value still reports no data instead of sitting on Normal.
        state.LastSeenUtc ??= now;

        // Already written off; saying so again every minute would be noise.
        if (state.Health == AlertHealth.NoData)
        {
            return null;
        }

        // The grace period is what protects a firing alert from a one-tick docker
        // hiccup. Past it, every series is treated the same — including a firing
        // one, which otherwise stays on screen for ever once its container is
        // deleted, with nothing left that could ever clear it.
        if (Elapsed(state.LastSeenUtc, now) < TimeSpan.FromSeconds(rule.NoDataAfterSeconds))
        {
            return null;
        }

        state.Health = AlertHealth.NoData;
        state.SinceUtc = now;

        // FiredAtUtc and LastNotifiedUtc are deliberately kept: they are what
        // remembers that an announced firing is still open, so that readings
        // coming back healthy can send the "resolved" the operator is owed. This
        // is not a false all-clear — no-data is reported as no-data.
        return new AlertTransition { Rule = rule, Series = series, Kind = AlertTransitionKind.NoData, At = now };
    }

    private static AlertTransition? StepWithValue(
        AlertRule rule, AlertSeriesState state, string series, double value, DateTimeOffset now,
        TimeSpan maximumSampleGap)
    {
        // Captured before it is overwritten: whether this reading follows on from the
        // last one is what decides if a "for" window in progress is still valid.
        var observedContinuously = state.LastSeenUtc is not { } lastSeen
            || Elapsed(lastSeen, now) <= maximumSampleGap;

        state.LastSeenUtc = now;
        state.LastValue = value;

        var breaching = AlertComparators.Breaches(value, rule.Comparator, rule.Threshold);

        if (!breaching)
        {
            // FiredAtUtc is non-null exactly while a firing episode is open —
            // whether the series is still Alerting or drifted through NoData on
            // the way here. Either way this reading is the one that clears it.
            if (state.FiredAtUtc is not null)
            {
                return Resolve(rule, state, series, value, now);
            }

            if (state.Health != AlertHealth.Normal)
            {
                state.Health = AlertHealth.Normal;
                state.SinceUtc = now;
            }

            state.SinceUtc ??= now;
            state.LastNotifiedUtc = null;
            return null;
        }

        if (state.Health == AlertHealth.Alerting)
        {
            return StillFiring(rule, state, series, value, now);
        }

        if (state.Health == AlertHealth.Pending)
        {
            // A gap in the readings restarts the window rather than counting toward
            // it. "For 5 minutes" has to mean the breach was observed throughout,
            // not that a breach was seen once, the samples stopped, and a second
            // breach arrived five minutes later.
            if (!observedContinuously)
            {
                state.SinceUtc = now;
                return null;
            }

            return Elapsed(state.SinceUtc, now) >= TimeSpan.FromSeconds(rule.ForSeconds)
                ? Fire(rule, state, series, value, now)
                : null;
        }

        // Normal or NoData. Coming back from NoData restarts the "for" window:
        // nothing was observed during the gap, so nothing was proven by it.
        if (rule.ForSeconds <= 0)
        {
            return Fire(rule, state, series, value, now);
        }

        state.Health = AlertHealth.Pending;
        state.SinceUtc = now;
        return null;
    }

    private static AlertTransition? Fire(
        AlertRule rule, AlertSeriesState state, string series, double value, DateTimeOffset now)
    {
        // FiredAtUtc is non-null exactly while an episode is open, and an episode
        // survives NoData by design. So arriving here with one already open means
        // the series went quiet and came back STILL breaching — the same episode,
        // not a new one. Overwriting FiredAtUtc here closed an announced episode
        // with no "resolved" at all, announced a duplicate "firing", and threw away
        // the episode's real start time.
        var reopening = state.FiredAtUtc is not null;

        state.Health = AlertHealth.Alerting;
        state.SinceUtc = now;

        if (reopening)
        {
            return StillFiring(rule, state, series, value, now);
        }

        state.FiredAtUtc = now;

        // A new episode starts unannounced; Evaluate marks it announced unless the
        // rule is silenced.
        state.LastNotifiedUtc = null;

        return new AlertTransition
        {
            Rule = rule,
            Series = series,
            Kind = AlertTransitionKind.Firing,
            At = now,
            Value = value,
        };
    }

    private static AlertTransition? StillFiring(
        AlertRule rule, AlertSeriesState state, string series, double value, DateTimeOffset now)
    {
        // Never announced — it fired while the rule was silenced. Say so now.
        if (state.LastNotifiedUtc is null)
        {
            return new AlertTransition
            {
                Rule = rule,
                Series = series,
                Kind = AlertTransitionKind.Firing,
                At = now,
                Value = value,
            };
        }

        if (rule.ReNotifySeconds <= 0
            || Elapsed(state.LastNotifiedUtc, now) < TimeSpan.FromSeconds(rule.ReNotifySeconds))
        {
            return null;
        }

        return new AlertTransition
        {
            Rule = rule,
            Series = series,
            Kind = AlertTransitionKind.Reminder,
            At = now,
            Value = value,
            FiringFor = Elapsed(state.FiredAtUtc, now),
        };
    }

    private static AlertTransition? Resolve(
        AlertRule rule, AlertSeriesState state, string series, double value, DateTimeOffset now)
    {
        var announced = state.LastNotifiedUtc is not null;
        var firingFor = Elapsed(state.FiredAtUtc, now);

        state.Health = AlertHealth.Normal;
        state.SinceUtc = now;
        state.FiredAtUtc = null;
        state.LastNotifiedUtc = null;

        // Nobody was told it started, so nobody needs telling it stopped.
        if (!announced || !rule.NotifyOnResolve)
        {
            return null;
        }

        return new AlertTransition
        {
            Rule = rule,
            Series = series,
            Kind = AlertTransitionKind.Resolved,
            At = now,
            Value = value,
            FiringFor = firingFor,
        };
    }

    /// <summary>
    /// Time since <paramref name="from"/>, never negative. A clock stepped
    /// backwards (an NTP correction, a VM resumed from a snapshot) must not be
    /// able to fire a rule early or resolve one early; it can only delay them.
    /// </summary>
    private static TimeSpan Elapsed(DateTimeOffset? from, DateTimeOffset now)
    {
        if (from is not { } start)
        {
            return TimeSpan.Zero;
        }

        var elapsed = now - start;
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }
}
