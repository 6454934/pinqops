using PinqOps.Backups;
using PinqOps.Scheduling;

namespace PinqOps.Web;

/// <summary>
/// Reports which scheduled backups are due, for <see cref="ScheduledWorkHost"/> to
/// run. This was <c>BackupScheduler</c>, its own <c>BackgroundService</c>; only the
/// due-check below was ever specific to backups, so the tick and the
/// fire-and-forget moved to the host and this kept the decision.
///
/// <para>The config is re-read on every tick rather than cached, which is what
/// makes an edit on the Backups page take effect within the minute.</para>
/// </summary>
public sealed class BackupWorkSource : ScheduledWorkSource
{
    /// <summary>Prefixes the job id, so a failure line names the target.</summary>
    private const string JobPrefix = "backup:";

    private readonly BackupService _backups;
    private readonly BackupConfigStore _store;

    public BackupWorkSource(BackupService backups, BackupConfigStore store)
    {
        ArgumentNullException.ThrowIfNull(backups);
        ArgumentNullException.ThrowIfNull(store);
        _backups = backups;
        _store = store;
    }

    public string Name => "backups";

    public IReadOnlyList<ScheduledJob> Due(DateTimeOffset now) =>
    [
        .. _store.Load().Targets
            // IsRunning is checked here as well as inside RunGuardedAsync: the
            // guard there is what actually prevents an overlap, this only avoids
            // starting a task that would immediately give up.
            .Where(target => target.Enabled
                && !_backups.IsRunning(target.Id)
                && BackupSchedule.IsDue(target, now, _backups.LastRun(target.Id)))
            .Select(target => new ScheduledJob(JobPrefix + target.Id, _ => _backups.RunGuardedAsync(target))),
    ];
}
