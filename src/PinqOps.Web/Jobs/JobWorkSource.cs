using PinqOps.Scheduling;

namespace PinqOps.Web;

/// <summary>
/// Reports which scheduled jobs are due, for <see cref="ScheduledWorkHost"/> to run.
///
/// <para>The due-check is <see cref="CronSchedule.IsDue"/> — the same pure predicate
/// the backups use through their own windows. Everything about "did this minute
/// match, and has it already run" lives there, tested without a clock; this only
/// says which jobs to ask about and where the last run came from.</para>
///
/// <para>The jobs are re-read on every tick rather than cached, which is what makes
/// an edit on the Jobs page take effect within the minute.</para>
/// </summary>
public sealed class JobWorkSource : ScheduledWorkSource
{
    private const string JobPrefix = "job:";

    private readonly JobService _jobs;

    public JobWorkSource(JobService jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        _jobs = jobs;
    }

    public string Name => "jobs";

    public IReadOnlyList<ScheduledJob> Due(DateTimeOffset now)
    {
        var definitions = _jobs.Store.Load();
        if (definitions.Count == 0)
        {
            return [];
        }

        // "3 a.m." means three in the morning where the server is, not in UTC — and
        // the process runs on the server, so its own zone is that one. It is also
        // the zone the Settings page changes, so a job and a backup written for the
        // same hour run in the same hour.
        var zone = TimeZoneInfo.Local;
        var lastRuns = LastRuns();
        var due = new List<ScheduledJob>();

        foreach (var definition in definitions)
        {
            if (!definition.Enabled
                || _jobs.IsRunning(definition.Id)
                || !CronExpression.TryParse(definition.Cron, out var cron, out _))
            {
                // A job whose expression no longer parses is skipped rather than
                // throwing: one bad entry must not stop every other job on the host.
                continue;
            }

            if (CronSchedule.IsDue(cron!, zone, now, LastRunOf(lastRuns, definition.Id)))
            {
                due.Add(new ScheduledJob(JobPrefix + definition.Id, token => _jobs.RunGuardedAsync(definition, token)));
            }
        }

        return due;
    }

    /// <summary>
    /// When a job last started, or null when it never has.
    ///
    /// <para>Null and not <c>default</c>. <see cref="CronSchedule.IsDue"/> reads
    /// "no last run" as "wait for the next matching minute" and a timestamp as
    /// "catch up anything missed since then" — so handing it the default
    /// <see cref="DateTimeOffset"/> does not say "never ran", it says the job last
    /// ran in the year 1, which is overdue by two millennia. Every job would then
    /// fire on the tick after it was saved, whatever its expression said, which is
    /// the exact outcome <c>IsDue</c> documents itself as preventing.</para>
    /// </summary>
    internal static DateTimeOffset? LastRunOf(IReadOnlyDictionary<string, DateTimeOffset> lastRuns, string jobId)
    {
        ArgumentNullException.ThrowIfNull(lastRuns);

        return lastRuns.TryGetValue(jobId, out var at) ? at : null;
    }

    /// <summary>
    /// When each job last started. Read from the run history rather than held in
    /// memory, so a dashboard restart does not make every job think it has never
    /// run — which would fire each of them at its next matching minute regardless of
    /// whether it had just gone.
    /// </summary>
    private Dictionary<string, DateTimeOffset> LastRuns()
    {
        var latest = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var run in _jobs.History())
        {
            if (!latest.TryGetValue(run.JobId, out var seen) || run.StartedAt > seen)
            {
                latest[run.JobId] = run.StartedAt;
            }
        }

        return latest;
    }
}
