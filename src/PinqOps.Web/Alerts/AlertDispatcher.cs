using PinqOps.Alerts;
using PinqOps.Notifications;

namespace PinqOps.Web;

/// <summary>
/// Sends alert transitions to the configured channels. Best effort by design:
/// per-channel timeout, failures logged and swallowed. An alert that cannot be
/// delivered must not take the evaluator down with it — the dashboard would then
/// stop noticing everything else too.
/// </summary>
public sealed class AlertDispatcher : IDisposable
{
    private static readonly TimeSpan ChannelTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A cap on how much one tick may send. A rule watching every container on a
    /// host that just lost its disk can produce a transition per container at
    /// once; nobody is helped by two hundred messages, and the trail keeps the
    /// full picture either way.
    /// </summary>
    public const int MaxPerBatch = 20;

    /// <summary>
    /// How much of one batch a single rule is served before any rule is served
    /// twice — a share, not a ceiling. Without it, one wildcard rule with a
    /// transition per container fills the whole budget and every other rule on the
    /// host is dropped — including a critical one that fired in the same tick.
    /// Whatever is left of <see cref="MaxPerBatch"/> afterwards still goes to
    /// whoever has transitions waiting.
    /// </summary>
    public const int MaxPerRule = 5;

    private readonly AlertChannelStore _channels;
    private readonly MailService _mail;
    private readonly ILogger<AlertDispatcher> _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    // Delivery runs detached from the evaluator's tick, so two batches could
    // otherwise overlap and arrive out of order.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AlertDispatcher(
        AlertChannelStore channels,
        MailService mail,
        ILogger<AlertDispatcher> logger,
        HttpClient? httpClient = null)
    {
        _channels = channels;
        _mail = mail;
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient();
        _ownsClient = httpClient is null;
    }

    /// <summary>
    /// Every channel a message could go to.
    ///
    /// <para>Mail is appended rather than built by <see cref="ChannelFactory"/>:
    /// the other three are an HTTP call to a URL held in this config, and mail is a
    /// relay configured elsewhere, a password in the vault and a recipient list. It
    /// keeps the same contract, which is the part that mattered — one channel
    /// configured wrongly skips that channel and leaves the rest working.</para>
    /// </summary>
    private IReadOnlyList<INotificationChannel> Channels(AlertChannelConfig config, bool includeDisabled = false)
    {
        var channels = ChannelFactory
            .Build(config.Webhook, config.Slack, config.Telegram, _httpClient, includeDisabled, Log)
            .ToList();

        if (_mail.BuildChannel(config.Email, includeDisabled, Log) is { } email)
        {
            channels.Add(email);
        }

        return channels;
    }

    /// <summary>
    /// Sends a batch, in order, without ever throwing, and returns the transitions
    /// at least one channel accepted.
    ///
    /// <para>That return value is not the batch it was handed. A transition the
    /// per-rule ordering dropped, one whose rule names a channel that is switched
    /// off, one whose every POST failed, and every transition at all on a server
    /// with nothing configured under "where alerts are sent" — none of them
    /// reached anyone, and the alert trail's "Sent" column is where the operator
    /// goes to find that out.</para>
    /// </summary>
    public async Task<IReadOnlyList<AlertTransition>> DispatchAsync(
        IReadOnlyList<AlertTransition> transitions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transitions);

        var delivered = new List<AlertTransition>(transitions.Count);
        if (transitions.Count == 0)
        {
            return delivered;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = _channels.Load();
            var host = Environment.MachineName;
            var batch = Prioritize(transitions);
            var dropped = transitions.Count - batch.Count;

            foreach (var transition in batch)
            {
                if (await SendAsync(config, transition, host, cancellationToken).ConfigureAwait(false))
                {
                    delivered.Add(transition);
                }
            }

            if (dropped > 0)
            {
                // Say so rather than truncating silently: "we sent 20 of 214" is
                // information, a quiet cut-off is a bug report waiting to happen.
                _logger.LogWarning(
                    "Alert batch capped at {Sent} notifications; {Dropped} more are in the alert history",
                    batch.Count,
                    dropped);
            }

            if (delivered.Count < batch.Count)
            {
                // The fresh-install case reaches zero channels and produced no log
                // line at all, so the only sign anything was wrong was an alert
                // that never arrived — which reads exactly like an alert that never
                // fired.
                _logger.LogWarning(
                    "{Undelivered} of {Sent} alert notifications reached no channel; check where alerts are sent",
                    batch.Count - delivered.Count,
                    batch.Count);
            }

            return delivered;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Alert dispatch failed");
            return delivered;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The transitions this batch will actually send, worst first.
    ///
    /// The evaluator hands them over in the order the rules appear in alerts.json —
    /// i.e. the order they were created. Capping that at
    /// <see cref="MaxPerBatch"/> meant which alerts got delivered was decided by
    /// where their rule happened to sit in the file, so one noisy rule created early
    /// could starve a critical one created later of its only notification: the
    /// evaluator has already stamped LastNotifiedUtc for every transition it handed
    /// over, so there is no retry.
    ///
    /// Ordering is severity first, then kind (a firing matters more than a reminder
    /// or a no-data), and the per-rule cap keeps one fan-out rule from taking the
    /// whole budget. The sort is stable, so within one rule and severity the
    /// evaluator's container ordering is preserved.
    ///
    /// <para>Two passes, because <see cref="MaxPerRule"/> is a share and not a
    /// ceiling. The first hands every rule its share; the second spends whatever is
    /// left of <see cref="MaxPerBatch"/> on the rest, in the same order. Dropping
    /// those while most of the batch was empty meant an eight-service stack going
    /// down paged five containers and silently abandoned three — permanently, since
    /// the evaluator has already stamped LastNotifiedUtc for all eight.</para>
    /// </summary>
    internal static List<AlertTransition> Prioritize(IReadOnlyList<AlertTransition> transitions)
    {
        var perRule = new Dictionary<string, int>(StringComparer.Ordinal);
        var batch = new List<AlertTransition>(Math.Min(transitions.Count, MaxPerBatch));
        var leftovers = new List<AlertTransition>();

        var ordered = transitions
            .OrderByDescending(transition => AlertSeverity.Rank(transition.Rule.Severity))
            .ThenBy(transition => KindPriority(transition.Kind));

        foreach (var transition in ordered)
        {
            perRule.TryGetValue(transition.Rule.Id, out var taken);
            if (taken >= MaxPerRule)
            {
                leftovers.Add(transition);
                continue;
            }

            perRule[transition.Rule.Id] = taken + 1;
            if (batch.Count < MaxPerBatch)
            {
                batch.Add(transition);
            }
        }

        foreach (var transition in leftovers)
        {
            if (batch.Count >= MaxPerBatch)
            {
                break;
            }

            batch.Add(transition);
        }

        return batch;
    }

    /// <summary>Lower is delivered first.</summary>
    private static int KindPriority(AlertTransitionKind kind) => kind switch
    {
        AlertTransitionKind.Firing => 0,
        AlertTransitionKind.Resolved => 1,
        AlertTransitionKind.Reminder => 2,
        _ => 3,
    };

    /// <summary>
    /// Sends a made-up firing message for one rule, so the dashboard's "Test"
    /// button proves the whole path rather than only the channel settings.
    /// </summary>
    public async Task<bool> SendTestAsync(AlertRule rule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var transition = new AlertTransition
        {
            Rule = rule,
            Series = rule.IsContainerRule && rule.Target != "*" ? rule.Target : string.Empty,
            Kind = AlertTransitionKind.Firing,
            At = DateTimeOffset.UtcNow,
            // A 0/1 state metric reads as its "everything is fine" wording at the
            // threshold, which makes for a confusing test message; send the value
            // that would actually have fired it.
            Value = AlertMetrics.Unit(rule.Metric) == "bool" ? 1 : rule.Threshold,
        };

        return await SendAsync(_channels.Load(), transition, Environment.MachineName, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one operational notice from pinqops itself to every enabled channel.
    ///
    /// <para>Deliberately outside the rule model. A notice has no threshold, no
    /// series and no "for" window — forcing one into an <see cref="AlertRule"/>
    /// would put a rule nobody wrote on the Alerts page, a row nobody can silence
    /// in the alert history, and a <c>condition</c> field in the webhook payload
    /// describing a comparison that was never made. This shares the channels and
    /// the timeout, which is the part that was worth sharing.</para>
    ///
    /// <para>Best effort like every other send: returns whether any channel took
    /// it, and never throws.</para>
    /// </summary>
    public async Task<bool> SendNoticeAsync(
        string eventName,
        string text,
        object payload,
        string severity,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentNullException.ThrowIfNull(payload);

        var message = new NotificationMessage
        {
            Event = eventName,
            Text = text,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = payload,
            Severity = severity,
        };

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = _channels.Load();
            var delivered = false;
            foreach (var channel in Channels(config))
            {
                delivered |= await ChannelFactory
                    .SendAsync(channel, message, ChannelTimeout, Log, cancellationToken)
                    .ConfigureAwait(false);
            }

            return delivered;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Sending the {Event} notice failed", eventName);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Sends a synthetic message to one channel, to prove it is configured
    /// correctly. Unlike a real dispatch, an unconfigured channel throws so the
    /// operator is told why nothing arrived.
    /// </summary>
    public async Task<bool> SendChannelTestAsync(string channel, CancellationToken cancellationToken = default)
    {
        var config = _channels.Load();
        var target = Channels(config, includeDisabled: true)
            .FirstOrDefault(c => string.Equals(c.Channel, channel, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Channel '{channel}' is not configured.");

        var message = new NotificationMessage
        {
            Event = AlertMessage.FiringEvent,
            Text = $"pinqops @ {Environment.MachineName}: alert channel test — this is what an alert looks like.",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = new
            {
                @event = AlertMessage.FiringEvent,
                rule = "Channel test",
                host = Environment.MachineName,
                timestamp = DateTimeOffset.UtcNow,
            },
            Severity = AlertSeverity.Info,
        };

        return await ChannelFactory
            .SendAsync(target, message, ChannelTimeout, Log, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> SendAsync(
        AlertChannelConfig config, AlertTransition transition, string host, CancellationToken cancellationToken)
    {
        var message = new NotificationMessage
        {
            Event = AlertMessage.EventName(transition.Kind),
            Text = AlertMessage.Text(transition, host),
            Timestamp = transition.At,
            Payload = AlertMessage.Payload(transition, host),
            Severity = transition.Rule.Severity,
        };

        var delivered = false;
        foreach (var channel in Channels(config))
        {
            // An empty channel list on a rule means "wherever alerts go", which is
            // what keeps a rule working after a channel is added later.
            if (transition.Rule.Channels.Count > 0
                && !transition.Rule.Channels.Contains(channel.Channel, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            delivered |= await ChannelFactory
                .SendAsync(channel, message, ChannelTimeout, Log, cancellationToken)
                .ConfigureAwait(false);
        }

        return delivered;
    }

    // Channel URLs are credentials; ChannelFactory logs the channel name only.
    private void Log(string line) => _logger.LogInformation("{Detail}", line);

    public void Dispose()
    {
        _gate.Dispose();
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
