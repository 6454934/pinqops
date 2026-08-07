namespace PinqOps.Alerts;

/// <summary>The whole outcome of one evaluation pass.</summary>
/// <param name="States">The complete new state map — anything absent has been retired.</param>
/// <param name="Transitions">Everything that happened, in rule order — the alert trail.</param>
/// <param name="Notifications">
/// The subset of <paramref name="Transitions"/> that may be delivered; a silenced
/// rule's transitions are recorded but not sent.
/// </param>
public sealed record AlertTickResult(
    Dictionary<string, AlertSeriesState> States,
    IReadOnlyList<AlertTransition> Transitions,
    IReadOnlyList<AlertTransition> Notifications);

/// <summary>
/// One evaluation pass over every enabled rule, as a pure fold. The background
/// worker is then a thin shell — sample, call this, save, send — which is what
/// keeps the interesting behaviour testable without a clock, a disk or docker.
///
/// The returned map is authoritative: a series that was not evaluated is simply
/// not in it. That is how state for deleted rules, disabled rules and containers
/// that have gone away for good stops accumulating, without a separate sweep.
/// </summary>
public static class AlertTick
{
    /// <param name="maximumSampleGap">
    /// How long a gap between readings still counts as continuous observation. Pass
    /// the worker's tick tolerance; null uses
    /// <see cref="AlertEvaluator.DefaultMaximumSampleGap"/>.
    /// </param>
    public static AlertTickResult Run(
        IEnumerable<AlertRule> rules,
        MetricSample sample,
        IReadOnlyDictionary<string, AlertSeriesState> previous,
        DateTimeOffset now,
        TimeSpan? maximumSampleGap = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(previous);

        var states = new Dictionary<string, AlertSeriesState>(StringComparer.Ordinal);
        var transitions = new List<AlertTransition>();
        var notifications = new List<AlertTransition>();

        foreach (var rule in rules)
        {
            if (!rule.Enabled || string.IsNullOrEmpty(rule.Id))
            {
                continue;
            }

            foreach (var series in SeriesFor(rule, sample, previous))
            {
                var key = AlertSeriesState.KeyFor(rule.Id, series);
                previous.TryGetValue(key, out var before);

                var evaluation = AlertEvaluator.Evaluate(
                    rule, before, series, sample.Value(rule.Metric, series), now, maximumSampleGap);

                states[key] = evaluation.State;
                if (evaluation.Transition is not { } transition)
                {
                    continue;
                }

                if (!evaluation.Suppressed)
                {
                    transitions.Add(transition);
                    notifications.Add(transition);
                    continue;
                }

                // A suppressed transition is recomputed on every tick for as long as
                // the silence lasts, because LastNotifiedUtc is deliberately not
                // advanced while muted. Recording each one would fill the trail with
                // one identical entry a minute and rotate the real history out, so
                // only a change — or the rule's own repeat interval — earns another.
                if (before is null || AlertEvaluator.IsWorthTrailing(rule, before, transition))
                {
                    transitions.Add(transition);
                }
            }
        }

        return new AlertTickResult(states, transitions, notifications);
    }

    /// <summary>
    /// Which series a rule covers this tick. Host rules have exactly one (the
    /// empty series); a container rule with an explicit target always evaluates
    /// it, even when the container is missing, so that "my database is gone" is
    /// reported rather than ignored.
    /// </summary>
    private static IEnumerable<string> SeriesFor(
        AlertRule rule, MetricSample sample, IReadOnlyDictionary<string, AlertSeriesState> previous)
    {
        if (!rule.IsContainerRule)
        {
            return [string.Empty];
        }

        if (rule.Target != "*")
        {
            return [rule.Target];
        }

        // A wildcard rule covers what docker reports now, plus anything it was
        // already tracking that has not yet been written off as NoData. That
        // second half is what lets a container which disappears reach NoData once
        // — and then drop out of the map for good, instead of being rediscovered
        // and re-announced on every tick.
        //
        // A written-off series with an episode still open is the exception: its
        // FiredAtUtc is the only record that somebody was paged, and this map is
        // authoritative, so dropping it here is what loses the "resolved". It
        // costs nothing to keep — StepWithoutValue says nothing once the health is
        // NoData — and the moment the episode closes the series falls out on the
        // next tick like any other.
        var names = new List<string>(sample.ContainerNames);
        var seen = new HashSet<string>(names, StringComparer.Ordinal);

        foreach (var state in previous.Values)
        {
            if (state.RuleId == rule.Id
                && (state.Health != AlertHealth.NoData || state.FiredAtUtc is not null)
                && state.Series.Length > 0
                && seen.Add(state.Series))
            {
                names.Add(state.Series);
            }
        }

        names.Sort(StringComparer.Ordinal);
        return names.Count > AlertRuleValidator.MaxSeriesPerRule
            ? names.Take(AlertRuleValidator.MaxSeriesPerRule)
            : names;
    }
}
