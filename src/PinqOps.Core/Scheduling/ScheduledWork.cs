namespace PinqOps.Scheduling;

/// <summary>
/// One piece of work a scheduler tick decided to start.
///
/// <para><see cref="Id"/> is what a failure is reported against, so it has to name
/// the thing an operator would go and look at — <c>backup:db-postgres-main</c>,
/// not a counter.</para>
/// </summary>
public sealed record ScheduledJob(string Id, Func<CancellationToken, Task> Run);

/// <summary>
/// Something that owns a set of scheduled things and can say which of them are due.
///
/// <para>Deciding what is due stays with the feature: backups compare against a
/// last-run timestamp and their own hourly/daily/weekly windows, a cron job
/// compares against its expression. Only the tick, the fire-and-forget and the
/// error handling are shared, and those are exactly the parts that were worth
/// writing once.</para>
///
/// <para>Implementations must be cheap and must not throw for one bad entry — the
/// host calls this every minute for the life of the process, and a source that
/// throws stops its own jobs (never the other sources').</para>
/// </summary>
public interface ScheduledWorkSource
{
    /// <summary>Identifies the source in log lines. Not shown to users.</summary>
    string Name { get; }

    /// <summary>The jobs that should start now. An empty list is the normal answer.</summary>
    IReadOnlyList<ScheduledJob> Due(DateTimeOffset now);
}

/// <summary>
/// Whether a cron-driven job is due on this tick — the counterpart of
/// <c>BackupSchedule.IsDue</c>, and pure for the same reason: the decision is the
/// part worth testing, and it must not need a clock, a disk or a scheduler to
/// exercise.
/// </summary>
public static class CronSchedule
{
    /// <summary>
    /// True when the expression named a minute at or before <paramref name="now"/>
    /// that <paramref name="lastRun"/> has not already covered.
    ///
    /// <para><b>A job that has never run waits for its next matching minute</b>
    /// rather than firing immediately. Anchoring an unrun job at "the beginning of
    /// time" would make every job run the moment it is saved, which is not what
    /// writing <c>0 3 * * *</c> asks for — and it is the same stance
    /// <c>BackupSchedule</c> already takes for a target that has never run.</para>
    ///
    /// <para><b>A missed firing is caught up, once.</b> Asking for the first
    /// occurrence after the last run and comparing it to now means a host that was
    /// asleep at 03:00 runs the job when it wakes, instead of skipping the day —
    /// and because the comparison is against the <em>first</em> missed occurrence
    /// rather than each of them, a week of downtime produces one run, not seven.</para>
    ///
    /// <para><b>A clock stepped backwards cannot re-fire a job.</b> The next
    /// occurrence after the last run is later than the last run by construction, so
    /// a <paramref name="now"/> that has moved behind it satisfies nothing.</para>
    /// </summary>
    public static bool IsDue(
        CronExpression expression, TimeZoneInfo zone, DateTimeOffset now, DateTimeOffset? lastRun)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(zone);

        if (lastRun is not { } previous)
        {
            return expression.Matches(now, zone);
        }

        return expression.Next(previous, zone) is { } occurrence && occurrence <= now;
    }
}
