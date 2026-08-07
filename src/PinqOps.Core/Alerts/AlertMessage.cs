using System.Globalization;

namespace PinqOps.Alerts;

/// <summary>
/// What a webhook receives for an alert. A flat, self-describing shape: the
/// receiver should not have to hold pinqops' rule model to make sense of it.
/// </summary>
public sealed record AlertPayload
{
    /// <summary><c>alert_firing</c>, <c>alert_resolved</c> or <c>alert_nodata</c>.</summary>
    public required string Event { get; init; }

    public required string RuleId { get; init; }

    public required string Rule { get; init; }

    public required string Metric { get; init; }

    /// <summary>The container this fired for; empty for a host metric.</summary>
    public required string Series { get; init; }

    public required string Severity { get; init; }

    /// <summary>The rule's threshold, rendered — e.g. <c>&gt; 90%</c>.</summary>
    public required string Condition { get; init; }

    public double? Value { get; init; }

    /// <summary>True when this repeats an alert that is still firing.</summary>
    public bool Repeat { get; init; }

    /// <summary>How long it had been firing, in seconds, on a repeat or a resolve.</summary>
    public double? FiringForSeconds { get; init; }

    public required string Host { get; init; }

    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Turns a state transition into the two things a channel needs: a one-line
/// summary for chat, and a structured payload for webhooks. English only, like
/// the rest of the server — the dashboard has its own translations.
/// </summary>
public static class AlertMessage
{
    public const string FiringEvent = "alert_firing";
    public const string ResolvedEvent = "alert_resolved";
    public const string NoDataEvent = "alert_nodata";

    public static string EventName(AlertTransitionKind kind) => kind switch
    {
        AlertTransitionKind.Resolved => ResolvedEvent,
        AlertTransitionKind.NoData => NoDataEvent,
        _ => FiringEvent,
    };

    /// <summary>The chat one-liner, in the same voice as a deploy notification.</summary>
    public static string Text(AlertTransition transition, string host)
    {
        ArgumentNullException.ThrowIfNull(transition);

        var rule = transition.Rule;
        var lead = transition.Kind switch
        {
            AlertTransitionKind.Resolved => "RESOLVED",
            AlertTransitionKind.Reminder => "STILL FIRING",
            AlertTransitionKind.NoData => "NO DATA",
            _ => rule.Severity == AlertSeverity.Critical ? "CRITICAL ALERT" : "ALERT",
        };

        var text = $"pinqops @ {host}: {lead} — {rule.Name}: ";

        if (transition.Kind == AlertTransitionKind.NoData)
        {
            return text + $"{Subject(rule, transition.Series)} has stopped reporting.";
        }

        text += Reading(rule, transition.Series, transition.Value);

        if (transition.Kind is AlertTransitionKind.Firing or AlertTransitionKind.Reminder
            && AlertMetrics.Unit(rule.Metric) != "bool")
        {
            text += $" (threshold {AlertRuleValidator.DescribeCondition(rule)})";
        }

        if (transition.FiringFor is { } firingFor && firingFor > TimeSpan.Zero)
        {
            text += transition.Kind == AlertTransitionKind.Resolved
                ? $", after {Duration(firingFor)}"
                : $", for {Duration(firingFor)}";
        }

        return text + ".";
    }

    /// <summary>The structured body a webhook receives.</summary>
    public static AlertPayload Payload(AlertTransition transition, string host)
    {
        ArgumentNullException.ThrowIfNull(transition);

        return new AlertPayload
        {
            Event = EventName(transition.Kind),
            RuleId = transition.Rule.Id,
            Rule = transition.Rule.Name,
            Metric = transition.Rule.Metric,
            Series = transition.Series,
            Severity = transition.Rule.Severity,
            Condition = AlertRuleValidator.DescribeCondition(transition.Rule),
            Value = transition.Value,
            Repeat = transition.Kind == AlertTransitionKind.Reminder,
            FiringForSeconds = transition.FiringFor?.TotalSeconds,
            Host = host,
            Timestamp = transition.At,
        };
    }

    /// <summary>What the rule is about: the container's name, or the host metric's name.</summary>
    public static string Subject(AlertRule rule, string series)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.IsContainerRule && series.Length > 0 ? series : MetricLabel(rule.Metric);
    }

    /// <summary>A plain-English metric name, for people who never saw the rule form.</summary>
    public static string MetricLabel(string metric) => metric switch
    {
        AlertMetrics.HostCpu => "CPU usage",
        AlertMetrics.HostMemory => "memory usage",
        AlertMetrics.HostSwap => "swap usage",
        AlertMetrics.HostDisk => "disk usage",
        AlertMetrics.HostLoad1 => "load average (1m, per core)",
        AlertMetrics.HostLoad5 => "load average (5m, per core)",
        AlertMetrics.HostLoad15 => "load average (15m, per core)",
        AlertMetrics.ContainerCpu => "container CPU",
        AlertMetrics.ContainerMemory => "container memory",
        AlertMetrics.ContainerDown => "container state",
        AlertMetrics.ContainerUnhealthy => "container health",
        AlertMetrics.ContainerRestarting => "container restarts",
        _ => metric,
    };

    /// <summary>
    /// "disk usage is 93.4%" or "acme-app-1 is not running" — the 0/1 state
    /// metrics read as nonsense otherwise, and those are exactly the rules people
    /// set up first.
    /// </summary>
    private static string Reading(AlertRule rule, string series, double? value)
    {
        var subject = Subject(rule, series);
        var on = value is { } sample && sample >= 0.5;

        return rule.Metric switch
        {
            AlertMetrics.ContainerDown => $"{subject} is {(on ? "not running" : "running")}",
            AlertMetrics.ContainerUnhealthy => $"{subject} is {(on ? "unhealthy" : "healthy")}",
            AlertMetrics.ContainerRestarting => $"{subject} is {(on ? "restarting" : "no longer restarting")}",
            AlertMetrics.ContainerCpu => $"{subject} CPU is {Number(value)}%",
            AlertMetrics.ContainerMemory => $"{subject} memory is {Number(value)}%",
            _ => AlertMetrics.Unit(rule.Metric) == "percent"
                ? $"{subject} is {Number(value)}%"
                : $"{subject} is {Number(value)}",
        };
    }

    private static string Number(double? value) =>
        value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "unknown";

    /// <summary>A compact duration: <c>45s</c>, <c>12m</c>, <c>2h 5m</c>, <c>3d 4h</c>.</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        if (span.TotalMinutes < 1)
        {
            return $"{(int)span.TotalSeconds}s";
        }

        if (span.TotalHours < 1)
        {
            return $"{(int)span.TotalMinutes}m";
        }

        if (span.TotalDays < 1)
        {
            var minutes = span.Minutes;
            return minutes > 0 ? $"{(int)span.TotalHours}h {minutes}m" : $"{(int)span.TotalHours}h";
        }

        var hours = span.Hours;
        return hours > 0 ? $"{(int)span.TotalDays}d {hours}h" : $"{(int)span.TotalDays}d";
    }
}
