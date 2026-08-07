using PinqOps.Alerts;
using PinqOps.Proxy;
using PinqOps.Scheduling;

namespace PinqOps.Web;

/// <summary>
/// Notices when the proxy is down while it holds an app's host port.
///
/// <para><b>Why this is worth its own watcher.</b> A stopped proxy normally costs
/// nothing urgent — certificate renewal pauses, domains stop resolving to anything,
/// and the dashboard's proxy card already says so. Once an app has handed over its
/// host port, a stopped proxy <em>is</em> that app being down, at an address the
/// operator never associated with the proxy. Nothing else on the host would say
/// why.</para>
///
/// <para><b>It only reports.</b> There is deliberately no automatic rescue:
/// rewriting compose files in the background to give apps their ports back — while
/// a deploy may hold a project's lock, or while the proxy was stopped on purpose —
/// is how a short outage becomes a set of corrupted projects. The dashboard shows a
/// red banner with a one-click "take the port back" instead, and that is the
/// operator's call.</para>
///
/// <para><b>The state is in memory.</b> A restart during an outage re-sends the
/// notice once, which is the right way round: the alternative is a persisted flag
/// that outlives the condition and swallows the notice for an outage still
/// happening.</para>
///
/// <para>The decision itself is <see cref="ProxyWatchdog"/> in Core; this is the
/// part that reads the config, asks docker and sends.</para>
/// </summary>
public sealed class ProxyWatchdogSource : ScheduledWorkSource
{
    private const string JobId = "proxy-watchdog";

    /// <summary>Distinct from an alert rule's events, so a receiver can route it.</summary>
    public const string DownEvent = "proxy_down";

    public const string RecoveredEvent = "proxy_recovered";

    private readonly ProxyService _proxy;
    private readonly AlertDispatcher _dispatcher;
    private readonly ILogger<ProxyWatchdogSource> _logger;

    private bool _reportedDown;
    private int _checkInProgress;

    public ProxyWatchdogSource(ProxyService proxy, AlertDispatcher dispatcher, ILogger<ProxyWatchdogSource> logger)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(logger);
        _proxy = proxy;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public string Name => "proxy-watchdog";

    public IReadOnlyList<ScheduledJob> Due(DateTimeOffset now)
    {
        // Reading the config is a file read; asking docker is a subprocess. Deciding
        // here that there is nothing to watch keeps the subprocess off every host
        // that never handed a port over — which is every host until someone does.
        var enrolled = ProxyWatchdog.EnrolledTargets(_proxy.Store.Load());
        if (enrolled.Count == 0)
        {
            // Cleared so that enrolling again after an outage starts from a clean
            // slate rather than suppressing the first notice.
            _reportedDown = false;
            return [];
        }

        return [new ScheduledJob(JobId, token => CheckAsync(enrolled, token))];
    }

    private async Task CheckAsync(IReadOnlyList<string> enrolled, CancellationToken cancellationToken)
    {
        // A wedged `docker inspect` can take as long as the tick interval, so two
        // checks really can overlap; the second would read the flag before the first
        // wrote it and send the notice twice.
        if (IsCheckAlreadyRunning())
        {
            return;
        }

        try
        {
            var decision = ProxyWatchdog.Observe(await _proxy.IsRunningAsync().ConfigureAwait(false), _reportedDown);
            _reportedDown = decision.ReportedDown;
            if (decision.Notice == ProxyWatchdogNotice.None)
            {
                return;
            }

            var down = decision.Notice == ProxyWatchdogNotice.Down;
            var apps = string.Join(", ", enrolled);
            var host = Environment.MachineName;

            if (down)
            {
                _logger.LogError("The proxy is not running and it publishes the host port for: {Apps}", apps);
            }
            else
            {
                _logger.LogWarning("The proxy is running again; it publishes the host port for: {Apps}", apps);
            }

            await _dispatcher.SendNoticeAsync(
                down ? DownEvent : RecoveredEvent,
                down
                    ? $"pinqops @ {host}: CRITICAL — the proxy is not running and it publishes the host port for: "
                        + $"{apps}. Those apps are unreachable until it starts."
                    : $"pinqops @ {host}: RESOLVED — the proxy is running again; {apps} can be reached again.",
                new
                {
                    @event = down ? DownEvent : RecoveredEvent,
                    host,
                    apps = enrolled,
                    timestamp = DateTimeOffset.UtcNow,
                },
                AlertSeverity.Critical,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            MarkCheckFinished();
        }
    }

    private bool IsCheckAlreadyRunning() => Interlocked.Exchange(ref _checkInProgress, 1) == 1;

    private void MarkCheckFinished() => Interlocked.Exchange(ref _checkInProgress, 0);
}
