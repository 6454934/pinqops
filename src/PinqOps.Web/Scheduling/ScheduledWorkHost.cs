using PinqOps.Scheduling;

namespace PinqOps.Web;

/// <summary>
/// The dashboard's timed worker: once a minute it asks every
/// <see cref="ScheduledWorkSource"/> what is due and starts it.
///
/// <para>This is the generalised form of what used to be <c>BackupScheduler</c>.
/// The tick, the fire-and-forget and the "one bad entry must not stop the rest"
/// handling were never specific to backups; deciding what is due is, and that
/// stays with each source.</para>
///
/// <para>A minute is the resolution the whole scheduling model is built on — cron
/// has no finer field, and the backup windows are hourly at their tightest — so
/// this interval is not a tuning knob, it is the unit the sources' due-checks
/// assume.</para>
///
/// <para><see cref="AlertScheduler"/> deliberately stays a separate worker: it
/// samples metrics, persists evaluator state and dispatches notifications in one
/// ordered sequence per tick, and folding that into a fan-out of independent jobs
/// would lose the ordering it depends on.</para>
/// </summary>
public sealed class ScheduledWorkHost : BackgroundService
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    private readonly IEnumerable<ScheduledWorkSource> _sources;
    private readonly ILogger<ScheduledWorkHost> _logger;

    public ScheduledWorkHost(IEnumerable<ScheduledWorkSource> sources, ILogger<ScheduledWorkHost> logger)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(logger);
        _sources = sources;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Tick(stoppingToken);

            try
            {
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// One pass over every source. Internal rather than private so a test can take
    /// exactly one tick: driving this through <c>StartAsync</c>/<c>StopAsync</c>
    /// would make a test of scheduling depend on when <c>BackgroundService</c>
    /// happens to reach its first await, which is not a contract worth testing
    /// against.
    /// </summary>
    internal void Tick(CancellationToken stoppingToken)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var source in _sources)
        {
            IReadOnlyList<ScheduledJob> due;
            try
            {
                due = source.Due(now);
            }
            catch (Exception exception)
            {
                // Per source, so a source whose config file is unreadable this
                // minute does not stop every other source's jobs from running.
                _logger.LogWarning(exception, "Scheduled work source {Source} could not report what is due", source.Name);
                continue;
            }

            foreach (var job in due)
            {
                Start(job, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Runs a job detached from the tick. A dump that takes ten minutes must not
    /// hold up the next tick or the other jobs, and a source that refuses to
    /// overlap two runs of the same thing is what keeps that safe — the host
    /// deliberately does not track what it started.
    /// </summary>
    private void Start(ScheduledJob job, CancellationToken stoppingToken)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await job.Run(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Scheduled job {Job} failed", job.Id);
                }
            },
            CancellationToken.None);
    }
}
