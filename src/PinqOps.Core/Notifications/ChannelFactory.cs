namespace PinqOps.Notifications;

/// <summary>
/// Turns channel configuration into ready-to-use channels. Shared by the deploy
/// dispatcher and the alert dispatcher so both get the same behaviour for the
/// case that actually bites: one channel configured wrongly must skip that
/// channel, not silently drop every other, correctly configured one.
/// </summary>
public static class ChannelFactory
{
    /// <summary>
    /// Builds every channel that is enabled and has the settings it needs.
    /// <paramref name="includeDisabled"/> is for the dashboard's "Test" button,
    /// which must be able to prove a channel works before it is switched on.
    /// </summary>
    public static IReadOnlyList<INotificationChannel> Build(
        WebhookChannel webhook,
        SlackChannel slack,
        TelegramChannel telegram,
        HttpClient httpClient,
        bool includeDisabled = false,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(webhook);
        ArgumentNullException.ThrowIfNull(slack);
        ArgumentNullException.ThrowIfNull(telegram);

        var channels = new List<INotificationChannel>();

        // Each channel validates its own URL or token in its constructor and
        // throws on a bad one, so build them independently.
        void TryAdd(string name, Func<INotificationChannel> create)
        {
            try
            {
                channels.Add(create());
            }
            catch (ArgumentException exception)
            {
                log?.Invoke($"skipping the {name} notification channel: {exception.Message}");
            }
        }

        if ((webhook.Enabled || includeDisabled) && !string.IsNullOrWhiteSpace(webhook.Url))
        {
            TryAdd("webhook", () => new WebhookNotifier(webhook.Url, httpClient));
        }

        if ((slack.Enabled || includeDisabled) && !string.IsNullOrWhiteSpace(slack.WebhookUrl))
        {
            TryAdd("slack", () => new SlackNotifier(slack.WebhookUrl, httpClient));
        }

        if ((telegram.Enabled || includeDisabled)
            && !string.IsNullOrWhiteSpace(telegram.BotToken)
            && !string.IsNullOrWhiteSpace(telegram.ChatId))
        {
            TryAdd("telegram", () => new TelegramNotifier(telegram.BotToken, telegram.ChatId, httpClient));
        }

        return channels;
    }

    /// <summary>
    /// Sends one message to one channel, bounded by <paramref name="timeout"/>
    /// and never throwing for a delivery problem. Notifications must not be able
    /// to fail — or delay — whatever they are reporting on.
    /// </summary>
    public static async Task<bool> SendAsync(
        INotificationChannel channel,
        NotificationMessage message,
        TimeSpan timeout,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            var delivered = await channel.SendAsync(message, timeoutSource.Token).ConfigureAwait(false);
            log?.Invoke(delivered
                ? $"notification sent via {channel.Channel}"
                : $"notification via {channel.Channel} failed");
            return delivered;
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Never log the message body or the channel's URL: both routinely
            // carry a token.
            log?.Invoke($"notification via {channel.Channel} failed: {exception.Message}");
            return false;
        }
    }
}
