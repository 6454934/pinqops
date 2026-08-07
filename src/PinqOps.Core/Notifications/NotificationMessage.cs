namespace PinqOps.Notifications;

/// <summary>
/// One thing to deliver, in the two forms channels actually need: a line of text
/// for the chat channels, and an object for the webhook to serialize.
///
/// This exists so alerts can reuse the deploy notifiers without either pretending
/// to be a deploy or growing a second copy of the URL validation, the timeout
/// policy and the Slack/Telegram body shapes.
/// </summary>
public sealed record NotificationMessage
{
    /// <summary>
    /// The event id, e.g. <c>deploy_succeeded</c> or <c>alert_firing</c>. Carried
    /// separately from the payload so a channel can act on it without knowing
    /// which payload type it is looking at.
    /// </summary>
    public required string Event { get; init; }

    /// <summary>The one-line summary chat channels post.</summary>
    public required string Text { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// What the webhook receives as its JSON body, serialized by its runtime
    /// type. For a deploy this is the <see cref="DeployNotification"/> itself, so
    /// the body existing consumers already parse does not change.
    /// </summary>
    public required object Payload { get; init; }

    /// <summary><c>info</c>, <c>warning</c> or <c>critical</c>; informational.</summary>
    public string Severity { get; init; } = "info";
}

/// <summary>
/// A delivery channel for any kind of notification. Implemented by the same
/// classes as <see cref="INotifier"/>, which stays as the deploy-shaped entry
/// point the CLI uses.
/// </summary>
public interface INotificationChannel
{
    /// <summary>Channel id as used in the config file, e.g. <c>slack</c>.</summary>
    string Channel { get; }

    /// <summary>
    /// Sends the message. Returns false on failure — channels never throw for
    /// delivery problems, because a notification must not fail the thing it is
    /// reporting on.
    /// </summary>
    Task<bool> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default);
}
