namespace PinqOps.Notifications;

/// <summary>A single notification channel (webhook, Slack, Telegram, …).</summary>
public interface INotifier
{
    /// <summary>Channel id as used in the config file, e.g. <c>slack</c>.</summary>
    string Channel { get; }

    /// <summary>
    /// Sends the notification. Returns false on failure — channels never throw
    /// for delivery problems, because a notification must not fail a deploy.
    /// </summary>
    Task<bool> SendAsync(DeployNotification notification, CancellationToken cancellationToken = default);
}
