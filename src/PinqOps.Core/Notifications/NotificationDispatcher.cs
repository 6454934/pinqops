namespace PinqOps.Notifications;

/// <summary>
/// Fans a deploy outcome out to every enabled channel, best effort: per-channel
/// timeout, failures logged and swallowed — notifications must never fail (or
/// slow down) a deploy. Plugs into <see cref="Deployer"/> as its
/// <see cref="IDeployObserver"/>.
/// </summary>
public sealed class NotificationDispatcher : IDeployObserver, IDisposable
{
    private static readonly TimeSpan ChannelTimeout = TimeSpan.FromSeconds(5);

    private readonly NotificationConfigStore _configStore;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly Action<string>? _log;

    public NotificationDispatcher(string composeFilePath, Action<string>? log = null, HttpClient? httpClient = null)
    {
        _configStore = new NotificationConfigStore(composeFilePath);
        _httpClient = httpClient ?? new HttpClient();
        _ownsClient = httpClient is null;
        _log = log;
    }

    public async Task OnDeployCompletedAsync(DeployOutcome outcome, CancellationToken cancellationToken)
    {
        var notification = DeployNotification.FromOutcome(outcome);
        await DispatchAsync(notification, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends to all channels enabled for the notification's event.</summary>
    public async Task DispatchAsync(DeployNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var config = _configStore.Load();
        if (!config.IsEventEnabled(notification.Event))
        {
            return;
        }

        var message = notification.ToMessage();
        foreach (var channel in BuildNotifiers(config))
        {
            await SendOneAsync(channel, message, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends a synthetic test notification to ONE channel (the dashboard's
    /// "Test" button) and reports success. Unlike deploy-time dispatch, an
    /// unconfigured channel throws so the user sees why nothing arrived.
    /// </summary>
    public async Task<bool> SendTestAsync(string channel, CancellationToken cancellationToken = default)
    {
        var config = _configStore.Load();
        var notifier = BuildNotifiers(config, includeDisabled: true)
            .FirstOrDefault(n => string.Equals(n.Channel, channel, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Channel '{channel}' is not configured.");

        var notification = new DeployNotification
        {
            Event = NotificationEvents.DeploySucceeded,
            Tag = "sha-test",
            Host = Environment.MachineName,
            Timestamp = DateTimeOffset.UtcNow,
        };
        return await SendOneAsync(notifier, notification.ToMessage(), cancellationToken).ConfigureAwait(false);
    }

    private Task<bool> SendOneAsync(
        INotificationChannel channel,
        NotificationMessage message,
        CancellationToken cancellationToken) =>
        ChannelFactory.SendAsync(channel, message, ChannelTimeout, _log, cancellationToken);

    private IReadOnlyList<INotificationChannel> BuildNotifiers(
        NotificationConfig config, bool includeDisabled = false) =>
        ChannelFactory.Build(config.Webhook, config.Slack, config.Telegram, _httpClient, includeDisabled, _log);

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
