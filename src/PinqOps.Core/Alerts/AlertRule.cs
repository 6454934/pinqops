using System.Globalization;

namespace PinqOps.Alerts;

/// <summary>
/// One threshold rule over a server or container metric. The dashboard's
/// background worker samples every metric once a minute and hands each rule to
/// <see cref="AlertEvaluator"/>; a rule that stays breached for
/// <see cref="ForSeconds"/> fires to the configured channels.
/// </summary>
public sealed class AlertRule
{
    // A property initializer only runs when the JSON omits the member. An
    // explicit `"metric": null` — which a hand-edited alerts.json can easily
    // carry — assigns null straight over it, and a null metric then throws out
    // of IsContainerRule on every evaluation, taking the whole worker's tick
    // down once a minute. Normalising in the setters keeps "a corrupt config
    // means no alerts, never a crash" true for every field, not just for JSON
    // that fails to parse at all.
    private string _id = string.Empty;
    private string _name = string.Empty;
    private string _metric = string.Empty;
    private string _target = string.Empty;
    private string _comparator = AlertComparators.GreaterThan;
    private string _severity = AlertSeverity.Warning;
    private List<string> _channels = [];

    /// <summary>Server-generated, 8 hex characters. Stable across edits.</summary>
    public string Id { get => _id; set => _id = value ?? string.Empty; }

    public string Name { get => _name; set => _name = value ?? string.Empty; }

    public bool Enabled { get; set; } = true;

    /// <summary>One of <see cref="AlertMetrics"/>.</summary>
    public string Metric { get => _metric; set => _metric = value ?? string.Empty; }

    /// <summary>
    /// Empty for host metrics. For container metrics: a container name, or
    /// <c>*</c> to watch every container as its own independent series.
    /// </summary>
    public string Target { get => _target; set => _target = value ?? string.Empty; }

    /// <summary>gt | gte | lt | lte.</summary>
    public string Comparator { get => _comparator; set => _comparator = value ?? string.Empty; }

    public double Threshold { get; set; }

    /// <summary>
    /// How long the condition must hold before the rule fires — Grafana's "for".
    /// Zero fires on the first breaching sample. Named <c>ForSeconds</c> because
    /// <c>for</c> is a C# keyword.
    /// </summary>
    public int ForSeconds { get; set; } = 300;

    /// <summary>critical | warning | info.</summary>
    public string Severity { get => _severity; set => _severity = value ?? string.Empty; }

    /// <summary>
    /// Channels to notify, from <see cref="AlertChannelNames"/>. Empty means
    /// every channel that is enabled — which is what most people want, and what
    /// keeps a rule working after a channel is added later.
    /// </summary>
    public List<string> Channels { get => _channels; set => _channels = value ?? []; }

    /// <summary>How often to repeat while still firing. Zero notifies once.</summary>
    public int ReNotifySeconds { get; set; }

    public bool NotifyOnResolve { get; set; } = true;

    /// <summary>
    /// How long a series may produce no value before the rule reports no data.
    /// A single missed sample must not flap the state, so this is deliberately
    /// several ticks wide.
    /// </summary>
    public int NoDataAfterSeconds { get; set; } = 300;

    /// <summary>Delivery is suppressed until this instant; null when not silenced.</summary>
    public DateTimeOffset? SilencedUntil { get; set; }

    /// <summary>True while <see cref="SilencedUntil"/> is still in the future.</summary>
    public bool IsSilenced(DateTimeOffset now) => SilencedUntil is { } until && until > now;

    /// <summary>True when this rule's metric is sampled per container.</summary>
    public bool IsContainerRule => AlertMetrics.IsContainerMetric(Metric);
}

/// <summary>The metric ids a rule can watch. Shared by the API, the evaluator and the dashboard's dropdown.</summary>
public static class AlertMetrics
{
    public const string HostCpu = "host.cpu";
    public const string HostMemory = "host.mem";
    public const string HostSwap = "host.swap";
    public const string HostDisk = "host.disk";
    public const string HostLoad1 = "host.load1";
    public const string HostLoad5 = "host.load5";
    public const string HostLoad15 = "host.load15";

    public const string ContainerCpu = "container.cpu";
    public const string ContainerMemory = "container.mem";
    public const string ContainerDown = "container.down";
    public const string ContainerUnhealthy = "container.unhealthy";
    public const string ContainerRestarting = "container.restarting";

    /// <summary>Every metric id, in the order the dashboard lists them.</summary>
    public static readonly string[] All =
    [
        HostCpu, HostMemory, HostSwap, HostDisk, HostLoad1, HostLoad5, HostLoad15,
        ContainerCpu, ContainerMemory, ContainerDown, ContainerUnhealthy, ContainerRestarting,
    ];

    public static bool IsKnown(string metric) => Array.IndexOf(All, metric) >= 0;

    public static bool IsContainerMetric(string metric) =>
        metric.StartsWith("container.", StringComparison.Ordinal);

    /// <summary>
    /// The unit a threshold is expressed in: <c>percent</c>, <c>ratio</c> (load
    /// per core) or <c>bool</c> (0/1 state flags). The dashboard uses it to label
    /// the threshold field and to offer a sensible default.
    /// </summary>
    public static string Unit(string metric) => metric switch
    {
        HostLoad1 or HostLoad5 or HostLoad15 => "ratio",
        ContainerDown or ContainerUnhealthy or ContainerRestarting => "bool",
        _ => "percent",
    };
}

public static class AlertComparators
{
    public const string GreaterThan = "gt";
    public const string GreaterOrEqual = "gte";
    public const string LessThan = "lt";
    public const string LessOrEqual = "lte";

    public static readonly string[] All = [GreaterThan, GreaterOrEqual, LessThan, LessOrEqual];

    public static bool IsKnown(string comparator) => Array.IndexOf(All, comparator) >= 0;

    /// <summary>Whether a sampled value violates the rule's threshold.</summary>
    public static bool Breaches(double value, string comparator, double threshold) => comparator switch
    {
        GreaterThan => value > threshold,
        GreaterOrEqual => value >= threshold,
        LessThan => value < threshold,
        LessOrEqual => value <= threshold,
        _ => false,
    };

    /// <summary>The comparator rendered for a human, e.g. <c>&gt;=</c>.</summary>
    public static string Symbol(string comparator) => comparator switch
    {
        GreaterOrEqual => ">=",
        LessThan => "<",
        LessOrEqual => "<=",
        _ => ">",
    };
}

public static class AlertSeverity
{
    public const string Critical = "critical";
    public const string Warning = "warning";
    public const string Info = "info";

    public static readonly string[] All = [Critical, Warning, Info];

    public static bool IsKnown(string severity) => Array.IndexOf(All, severity) >= 0;

    /// <summary>Higher is worse — used to pick the worst severity currently firing.</summary>
    public static int Rank(string severity) => severity switch
    {
        Critical => 3,
        Warning => 2,
        Info => 1,
        _ => 0,
    };
}

/// <summary>Channel ids, matching the keys used in the notification config.</summary>
public static class AlertChannelNames
{
    public const string Webhook = "webhook";
    public const string Slack = "slack";
    public const string Telegram = "telegram";

    /// <summary>
    /// Mail, through the server's relay. The only channel whose destination is not
    /// stored with the channel itself — where it sends is the relay settings, and
    /// who it sends to is the recipient list.
    /// </summary>
    public const string Email = "email";

    public static readonly string[] All = [Webhook, Slack, Telegram, Email];

    public static bool IsKnown(string channel) => Array.IndexOf(All, channel) >= 0;
}

/// <summary>
/// Validates a rule before it is stored. Pure and exception-based so the HTTP
/// handler can call it directly — <c>Safe()</c> turns an
/// <see cref="ArgumentException"/> into a 400 with this message.
/// </summary>
public static class AlertRuleValidator
{
    public const int MaxNameLength = 80;

    /// <summary>
    /// A <c>*</c> rule on a host with thousands of containers would fan out to a
    /// series per container, each with its own state and its own notification.
    /// Refuse well before that becomes a problem.
    /// </summary>
    public const int MaxSeriesPerRule = 200;

    /// <summary>One day. Beyond that a "for" window is a mistake, not a choice.</summary>
    public const int MaxForSeconds = 86400;

    /// <summary>Throws <see cref="ArgumentException"/> when the rule cannot be stored as-is.</summary>
    public static void Validate(AlertRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (string.IsNullOrWhiteSpace(rule.Name))
        {
            throw new ArgumentException("A rule name is required.");
        }

        if (rule.Name.Length > MaxNameLength)
        {
            throw new ArgumentException($"A rule name may be at most {MaxNameLength} characters.");
        }

        if (!AlertMetrics.IsKnown(rule.Metric))
        {
            throw new ArgumentException($"Unknown metric '{rule.Metric}'.");
        }

        if (!AlertComparators.IsKnown(rule.Comparator))
        {
            throw new ArgumentException($"Unknown comparator '{rule.Comparator}'.");
        }

        if (!AlertSeverity.IsKnown(rule.Severity))
        {
            throw new ArgumentException($"Unknown severity '{rule.Severity}'.");
        }

        if (!double.IsFinite(rule.Threshold))
        {
            throw new ArgumentException("The threshold must be a finite number.");
        }

        if (rule.ForSeconds < 0 || rule.ForSeconds > MaxForSeconds)
        {
            throw new ArgumentException(
                $"The 'for' duration must be between 0 and {MaxForSeconds} seconds.");
        }

        if (rule.ReNotifySeconds < 0 || rule.ReNotifySeconds > MaxForSeconds)
        {
            throw new ArgumentException(
                $"The repeat interval must be between 0 and {MaxForSeconds} seconds.");
        }

        if (rule.NoDataAfterSeconds < 0 || rule.NoDataAfterSeconds > MaxForSeconds)
        {
            throw new ArgumentException(
                $"The no-data delay must be between 0 and {MaxForSeconds} seconds.");
        }

        foreach (var channel in rule.Channels)
        {
            if (!AlertChannelNames.IsKnown(channel))
            {
                throw new ArgumentException($"Unknown notification channel '{channel}'.");
            }
        }

        if (rule.IsContainerRule && string.IsNullOrWhiteSpace(rule.Target))
        {
            throw new ArgumentException("A container metric needs a container name, or '*' for all of them.");
        }

        if (!rule.IsContainerRule && !string.IsNullOrEmpty(rule.Target))
        {
            throw new ArgumentException($"The metric '{rule.Metric}' watches the host and takes no target.");
        }
    }

    /// <summary>
    /// A human-readable condition, e.g. <c>&gt; 90 %</c>. Used in notification
    /// text, where the reader has no access to the rule's fields.
    /// </summary>
    public static string DescribeCondition(AlertRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var unit = AlertMetrics.Unit(rule.Metric) == "percent" ? "%" : string.Empty;
        return $"{AlertComparators.Symbol(rule.Comparator)} "
            + rule.Threshold.ToString("0.##", CultureInfo.InvariantCulture) + unit;
    }
}
