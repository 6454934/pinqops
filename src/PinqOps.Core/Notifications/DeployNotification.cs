namespace PinqOps.Notifications;

/// <summary>The payload every notification channel receives for a deploy event.</summary>
public sealed record DeployNotification
{
    /// <summary><c>deploy_succeeded</c>, <c>deploy_failed</c>, <c>health_check_failed</c> or <c>rolled_back</c>.</summary>
    public required string Event { get; init; }

    public string? Tag { get; init; }

    public string? PreviousTag { get; init; }

    /// <summary>The server the deploy ran on.</summary>
    public required string Host { get; init; }

    public string? Error { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Maps a <see cref="DeployOutcome"/> onto a notification event.</summary>
    public static DeployNotification FromOutcome(DeployOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var eventName = outcome.Result switch
        {
            DeployRecordValues.ResultRolledBack => NotificationEvents.RolledBack,
            DeployRecordValues.ResultSucceeded => NotificationEvents.DeploySucceeded,
            _ when outcome.HealthCheck == DeployRecordValues.HealthFailed => NotificationEvents.HealthCheckFailed,
            _ => NotificationEvents.DeployFailed,
        };

        return new DeployNotification
        {
            Event = eventName,
            Tag = outcome.Tag,
            PreviousTag = outcome.PreviousTag,
            Host = Environment.MachineName,
            Error = outcome.Error,
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// This notification as a channel-agnostic message. The payload is the
    /// notification itself, so the JSON a webhook receives is unchanged from
    /// before channels became generic.
    /// </summary>
    public NotificationMessage ToMessage() => new()
    {
        Event = Event,
        Text = Summary(),
        Timestamp = Timestamp,
        Payload = this,
        Severity = Event is NotificationEvents.DeployFailed or NotificationEvents.HealthCheckFailed
            ? "critical"
            : "info",
    };

    /// <summary>A short human-readable one-liner used by chat channels.</summary>
    public string Summary()
    {
        var what = Event switch
        {
            NotificationEvents.DeploySucceeded => "deploy succeeded",
            NotificationEvents.DeployFailed => "deploy FAILED",
            NotificationEvents.HealthCheckFailed => "deploy FAILED health check",
            NotificationEvents.RolledBack => "rolled back",
            _ => Event,
        };

        var text = $"pinqops @ {Host}: {what}" + (Tag is null ? string.Empty : $" — {Tag}");
        if (Event == NotificationEvents.RolledBack && PreviousTag is not null)
        {
            text += $" (was {PreviousTag})";
        }

        if (Error is not null)
        {
            text += $"\n{Error}";
        }

        return text;
    }
}

public static class NotificationEvents
{
    public const string DeploySucceeded = "deploy_succeeded";
    public const string DeployFailed = "deploy_failed";
    public const string HealthCheckFailed = "health_check_failed";
    public const string RolledBack = "rolled_back";
}
