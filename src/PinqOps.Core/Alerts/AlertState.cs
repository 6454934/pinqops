namespace PinqOps.Alerts;

/// <summary>Where one series of one rule currently stands.</summary>
public enum AlertHealth
{
    /// <summary>The condition does not hold.</summary>
    Normal,

    /// <summary>Breached, but not yet for the rule's "for" duration.</summary>
    Pending,

    /// <summary>Breached for long enough — the rule has fired.</summary>
    Alerting,

    /// <summary>The series stopped producing values (docker down, container gone).</summary>
    NoData,
}

/// <summary>
/// The tracked state of one rule against one series. Container rules with a
/// <c>*</c> target produce one of these per container, so a single noisy
/// container neither fires nor silences the rest.
/// </summary>
public sealed class AlertSeriesState
{
    // Normalised in the setters for the same reason AlertRule's are: an explicit
    // null in alert-state.json would otherwise reach the evaluator and throw.
    private string _ruleId = string.Empty;
    private string _series = string.Empty;

    /// <summary>The rule this belongs to.</summary>
    public string RuleId { get => _ruleId; set => _ruleId = value ?? string.Empty; }

    /// <summary>Container name, or empty for a host metric.</summary>
    public string Series { get => _series; set => _series = value ?? string.Empty; }

    public AlertHealth Health { get; set; } = AlertHealth.Normal;

    /// <summary>When the current health began — this is what "for" is measured against.</summary>
    public DateTimeOffset? SinceUtc { get; set; }

    /// <summary>When this series last entered <see cref="AlertHealth.Alerting"/>.</summary>
    public DateTimeOffset? FiredAtUtc { get; set; }

    /// <summary>When a notification for this series last went out.</summary>
    public DateTimeOffset? LastNotifiedUtc { get; set; }

    /// <summary>The last tick at which the series produced a value at all.</summary>
    public DateTimeOffset? LastSeenUtc { get; set; }

    public double? LastValue { get; set; }

    /// <summary>
    /// When a suppressed transition for this series was last written to the trail,
    /// and what kind it was.
    ///
    /// A silence suppresses delivery, so <see cref="LastNotifiedUtc"/> is
    /// deliberately not advanced — that is what makes a muted firing announced when
    /// the silence lifts. But the transition itself is still recomputed every tick,
    /// so without a separate mark the trail took a fresh entry every minute for as
    /// long as the silence lasted, rotating the real history out of the log.
    /// </summary>
    public DateTimeOffset? LastSuppressedUtc { get; set; }

    /// <inheritdoc cref="LastSuppressedUtc"/>
    public AlertTransitionKind? LastSuppressedKind { get; set; }

    /// <summary>The dictionary key a series is stored under.</summary>
    public static string KeyFor(string ruleId, string series) => $"{ruleId}|{series}";

    public string Key => KeyFor(RuleId, Series);

    public AlertSeriesState Clone() => new()
    {
        RuleId = RuleId,
        Series = Series,
        Health = Health,
        SinceUtc = SinceUtc,
        FiredAtUtc = FiredAtUtc,
        LastNotifiedUtc = LastNotifiedUtc,
        LastSeenUtc = LastSeenUtc,
        LastValue = LastValue,
        LastSuppressedUtc = LastSuppressedUtc,
        LastSuppressedKind = LastSuppressedKind,
    };
}

/// <summary>
/// Repairs the persisted state map after the dashboard has been away. Pure, so
/// the awkward cases are testable without a clock or a disk.
///
/// Retiring state for deleted rules and vanished containers is <em>not</em> done
/// here — <see cref="AlertTick"/> rebuilds the map from the series it actually
/// evaluated, so anything stale falls out on its own.
/// </summary>
public static class AlertStateHygiene
{
    /// <summary>
    /// Resets any series that has not been evaluated for longer than
    /// <paramref name="staleAfter"/>. Called once, when the worker starts.
    ///
    /// Without it, a rule that was Pending when the dashboard was stopped three
    /// days ago would measure its old <c>SinceUtc</c> against today's clock and
    /// fire on the very first tick after a restart. A "for" window is meant to
    /// prove that a condition held continuously, and downtime proves nothing.
    /// Alerting series are reset too: re-detecting a real problem one window
    /// later is better than announcing a days-old one as if it were news.
    /// </summary>
    public static Dictionary<string, AlertSeriesState> ResetAfterDowntime(
        IReadOnlyDictionary<string, AlertSeriesState> states,
        DateTimeOffset now,
        TimeSpan staleAfter)
    {
        ArgumentNullException.ThrowIfNull(states);

        var repaired = new Dictionary<string, AlertSeriesState>(StringComparer.Ordinal);
        foreach (var (key, state) in states)
        {
            var stale = state.LastSeenUtc is not { } lastSeen || now - lastSeen > staleAfter;
            repaired[key] = stale
                ? new AlertSeriesState { RuleId = state.RuleId, Series = state.Series }
                : state;
        }

        return repaired;
    }
}
