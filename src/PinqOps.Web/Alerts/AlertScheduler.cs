using PinqOps.Alerts;

namespace PinqOps.Web;

/// <summary>
/// The dashboard's second background worker: once a minute it samples the host
/// and its containers, records the sample, evaluates every enabled rule and sends
/// whatever changed.
///
/// It is deliberately a thin shell around <see cref="AlertTick"/>, which is pure
/// — every timing rule that matters (the "for" window, repeats, no-data, silences)
/// lives there and is tested without a clock or a disk.
/// </summary>
public sealed class AlertScheduler : BackgroundService
{
    /// <summary>
    /// Matches the metric history's resolution and the backup scheduler's tick.
    /// It also low-pass filters the metrics for free: a one-second spike between
    /// two samples was never going to page anyone.
    /// </summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Tighter than DockerService's own 60s command timeout, so a wedged
    /// `docker stats` delays a tick rather than eating the whole interval.
    /// </summary>
    private static readonly TimeSpan SampleTimeout = TimeSpan.FromSeconds(30);

    private readonly MetricSampler _sampler;
    private readonly MetricHistoryStore _metrics;
    private readonly AlertRuleStore _rules;
    private readonly AlertStateStore _state;
    private readonly AlertHistoryLog _history;
    private readonly AlertDispatcher _dispatcher;
    private readonly ILogger<AlertScheduler> _logger;

    public AlertScheduler(
        MetricSampler sampler,
        MetricHistoryStore metrics,
        AlertRuleStore rules,
        AlertStateStore state,
        AlertHistoryLog history,
        AlertDispatcher dispatcher,
        ILogger<AlertScheduler> logger)
    {
        _sampler = sampler;
        _metrics = metrics;
        _rules = rules;
        _state = state;
        _history = history;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    // Written by the worker, read by request threads. Volatile because a
    // reference assignment being atomic only guarantees readers never see a torn
    // object — not that they ever see the new one.
    private volatile MetricSample? _latest;

    /// <summary>
    /// The most recent sample, for the endpoints that show live values next to the
    /// threshold field. Held in memory rather than re-read from the history file,
    /// and never sampled on demand: `docker stats` takes about a second, and the
    /// rule form asks for a value on every metric change.
    /// </summary>
    public MetricSample? Latest => _latest;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Any state older than a couple of ticks was written before a restart. Its
        // timestamps cannot be measured against today's clock: a rule left Pending
        // three days ago would otherwise fire on the very first tick.
        try
        {
            _state.Save(AlertStateHygiene.ResetAfterDowntime(
                _state.Load(), DateTimeOffset.UtcNow, TickInterval * 2));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not reconcile alert state after start");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Alert evaluation tick failed");
            }

            try
            {
                await Task.Delay(TickInterval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One whole pass: sample, record, evaluate, trail, send.</summary>
    internal async Task TickAsync(CancellationToken stoppingToken)
    {
        var now = DateTimeOffset.UtcNow;

        using var sampleTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        sampleTimeout.CancelAfter(SampleTimeout);
        var sample = await _sampler.SampleAsync(now, sampleTimeout.Token);

        // The sampler treats a cancelled docker read as "unreachable" rather than
        // failing the tick, so a shutdown has to be recognised here instead: there
        // is no point evaluating rules against a sample we abandoned collecting.
        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        _latest = sample;

        var rules = _rules.Load().Rules;
        WarnIfFanOutIsCapped(rules, sample);

        // Record before evaluating, so a fault in rule handling never costs chart
        // data — and so the charts keep working even with no rules configured.
        _metrics.Append(sample, TrackedContainers(rules));

        // The gap tolerance is the same TickInterval * 2 the startup reconciliation
        // uses: past one missed sample, a "for" window in progress has not been
        // observed continuously and restarts.
        AlertTickResult result = default!;
        _state.Update(states =>
        {
            result = AlertTick.Run(rules, sample, states, now, TickInterval * 2);

            // Persist before sending. A crash between the two costs one message; the
            // other order would re-fire every alert on every restart. The load,
            // evaluate and save happen under the store's lock so a concurrent rule
            // deletion cannot write this tick's transitions away.
            return result.States;
        });

        if (result.Transitions.Count == 0)
        {
            return;
        }

        if (result.Notifications.Count == 0)
        {
            // A silence held the whole tick back, so there is no delivery to wait
            // for and nothing that could have been sent.
            AppendTrail(result.Transitions, []);
            return;
        }

        // Detached: a channel that takes its full five-second timeout must not
        // delay the next sample, and twenty of them would overrun the tick.
        _ = Task.Run(
            async () =>
            {
                IReadOnlyList<AlertTransition> delivered = [];
                try
                {
                    delivered = await _dispatcher.DispatchAsync(result.Notifications, stoppingToken);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Sending alert notifications failed");
                }

                try
                {
                    AppendTrail(result.Transitions, delivered);
                }
                catch (Exception exception)
                {
                    // Detached, so nothing else would ever observe this.
                    _logger.LogWarning(exception, "Recording the alert trail failed");
                }
            },
            CancellationToken.None);
    }

    /// <summary>
    /// Writes the trail for one tick. It records everything that happened,
    /// including what a silence held back — otherwise "why was I not paged?" has
    /// no answer anywhere — so <paramref name="delivered"/> is what separates the
    /// two: only a transition a channel actually accepted is marked as notified.
    ///
    /// <para>This is why the trail is written after the send rather than before
    /// it. The column is headed "Sent", and it was filled in from what the
    /// evaluator handed to the dispatcher — which says a silence did not stop it,
    /// and nothing at all about whether it arrived.</para>
    /// </summary>
    private void AppendTrail(
        IReadOnlyList<AlertTransition> transitions, IReadOnlyCollection<AlertTransition> delivered)
    {
        var sent = delivered.ToHashSet();
        _history.Append(transitions.Select(transition =>
            AlertHistoryEntry.From(transition, notified: sent.Contains(transition))));
    }

    /// <summary>
    /// A wildcard rule stops at <see cref="AlertRuleValidator.MaxSeriesPerRule"/>
    /// containers. Say so: a cap nobody is told about reads as "everything is
    /// covered" when it is not.
    /// </summary>
    private void WarnIfFanOutIsCapped(IReadOnlyList<AlertRule> rules, MetricSample sample)
    {
        if (sample.Containers.Count <= AlertRuleValidator.MaxSeriesPerRule
            || !rules.Any(rule => rule.Enabled && rule.IsContainerRule && rule.Target == "*"))
        {
            return;
        }

        _logger.LogWarning(
            "A rule watching all containers covers the first {Cap} of {Count}; target containers by name to watch the rest",
            AlertRuleValidator.MaxSeriesPerRule,
            sample.Containers.Count);
    }

    /// <summary>
    /// Which container series are worth recording. Writing every container would
    /// make the history grow with the size of the host rather than with the number
    /// of things anyone is watching; a wildcard rule opts all of them back in,
    /// which is the point of a wildcard rule.
    /// </summary>
    private static IReadOnlyCollection<string>? TrackedContainers(IReadOnlyList<AlertRule> rules)
    {
        var tracked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (!rule.Enabled || !rule.IsContainerRule)
            {
                continue;
            }

            if (rule.Target == "*")
            {
                return null;
            }

            tracked.Add(rule.Target);
        }

        return tracked;
    }
}
