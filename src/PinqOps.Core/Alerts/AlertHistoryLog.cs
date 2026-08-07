using System.Text.Json;
using System.Text.Json.Serialization;

namespace PinqOps.Alerts;

/// <summary>One line of the alert trail: a rule changed state at a point in time.</summary>
public sealed record AlertHistoryEntry
{
    [JsonPropertyName("at")]
    public required DateTimeOffset At { get; init; }

    [JsonPropertyName("ruleId")]
    public required string RuleId { get; init; }

    [JsonPropertyName("rule")]
    public required string RuleName { get; init; }

    [JsonPropertyName("metric")]
    public required string Metric { get; init; }

    /// <summary>The container it fired for; empty for a host metric.</summary>
    [JsonPropertyName("series")]
    public required string Series { get; init; }

    /// <summary><c>firing</c>, <c>resolved</c>, <c>reminder</c> or <c>nodata</c>.</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    [JsonPropertyName("condition")]
    public required string Condition { get; init; }

    [JsonPropertyName("value")]
    public double? Value { get; init; }

    /// <summary>How long it had been firing, in seconds, on a resolve or a repeat.</summary>
    [JsonPropertyName("firingForSeconds")]
    public double? FiringForSeconds { get; init; }

    /// <summary>False when the rule was silenced, so the trail still shows what would have been sent.</summary>
    [JsonPropertyName("notified")]
    public bool Notified { get; init; } = true;

    public static AlertHistoryEntry From(AlertTransition transition, bool notified)
    {
        ArgumentNullException.ThrowIfNull(transition);

        return new AlertHistoryEntry
        {
            At = transition.At,
            RuleId = transition.Rule.Id,
            RuleName = transition.Rule.Name,
            Metric = transition.Rule.Metric,
            Series = transition.Series,
            Kind = transition.Kind.ToString().ToLowerInvariant(),
            Severity = transition.Rule.Severity,
            Condition = AlertRuleValidator.DescribeCondition(transition.Rule),
            Value = transition.Value,
            FiringForSeconds = transition.FiringFor?.TotalSeconds,
            Notified = notified,
        };
    }
}

/// <summary>
/// An append-only trail of every alert transition, so "did this fire overnight,
/// and when did it clear?" has an answer after the chat message has scrolled
/// away. Bounded at 8 MB — the live file plus <see cref="Generations"/> previous
/// ones, 2 MB each — which is tens of thousands of entries.
/// </summary>
public sealed class AlertHistoryLog
{
    private const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>Previous files kept alongside the live one.</summary>
    private const int Generations = 3;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly RotatingJsonLog _log;

    public AlertHistoryLog(string path) => _log = new RotatingJsonLog(path, Generations, maxBytes: MaxBytes);

    public string Path_ => _log.Path_;

    public void Append(AlertHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _log.Append(JsonSerializer.Serialize(entry, SerializerOptions));
    }

    public void Append(IEnumerable<AlertHistoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var entry in entries)
        {
            Append(entry);
        }
    }

    /// <summary>The most recent entries, newest first, optionally for one rule.</summary>
    public IReadOnlyList<AlertHistoryEntry> Read(int limit = 100, string? ruleId = null)
    {
        var entries = new List<AlertHistoryEntry>();
        foreach (var line in _log.ReadLines(oldestFirst: false))
        {
            try
            {
                if (JsonSerializer.Deserialize<AlertHistoryEntry>(line, SerializerOptions) is not { } entry)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(ruleId)
                    && !string.Equals(entry.RuleId, ruleId, StringComparison.Ordinal))
                {
                    continue;
                }

                entries.Add(entry);
                if (entries.Count >= Math.Clamp(limit, 1, 2000))
                {
                    break;
                }
            }
            catch (JsonException)
            {
                // Skip a torn line rather than losing the trail from there on.
            }
        }

        return entries;
    }
}
