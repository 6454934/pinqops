using System.Text.Json;
using PinqOps.Backups;

namespace PinqOps.Web;

/// <summary>
/// Runs and restores backups of database containers and docker volumes.
/// Database dumps use the container's own tools and credentials (read from its
/// environment via <c>sh -c</c>, so no password is ever passed on the command
/// line); volumes are tarred through a throwaway alpine container. Snapshots
/// land under <c>/opt/pinqops/backups/&lt;target&gt;/&lt;timestamp&gt;.&lt;ext&gt;</c>.
/// </summary>
public sealed class BackupService
{
    public const string BackupRoot = "/opt/pinqops/backups";
    private const long MinFreeBytes = 500L * 1024 * 1024; // refuse to start a backup under 500 MB free

    private readonly DockerService _docker;
    private readonly SystemInfoService _system;
    private readonly ILogger<BackupService> _logger;
    private readonly string _lastRunPath = Path.Combine(BackupRoot, "lastRun.json");

    private readonly OffsiteBackupService? _offsite;

    /// <param name="offsite">
    /// Null in tests and wherever offsite copies are not wired up. Optional rather
    /// than required because a backup that lands on this disk is already a backup —
    /// the copy that survives the server is the part that can be missing.
    /// </param>
    public BackupService(
        DockerService docker,
        SystemInfoService system,
        ILogger<BackupService> logger,
        OffsiteBackupService? offsite = null)
    {
        _docker = docker;
        _system = system;
        _logger = logger;
        _offsite = offsite;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _running = new();

    /// <summary>
    /// Where a target's snapshots live. The id is validated here rather than at
    /// each call site: every backup path is built from this, and the result is
    /// bind-mounted into a container by a root daemon. backups.json is a file on
    /// disk, so a hand-edited or corrupt id must not be able to turn into an
    /// arbitrary host path.
    /// </summary>
    /// <summary>
    /// Where a fetched offsite copy is written, so restoring one is the restore that
    /// already exists rather than a second path with its own failure modes.
    /// </summary>
    public string LocalPathFor(string targetId, string snapshot)
    {
        if (!PinqOps.Backups.BackupNaming.IsValidSnapshot(snapshot))
        {
            throw new ArgumentException($"'{snapshot}' is not a snapshot name.");
        }

        var directory = TargetDirectory(targetId);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, snapshot);
    }

    public string TargetDirectory(string targetId)
    {
        if (!BackupNaming.IsValidId(targetId))
        {
            throw new ArgumentException($"'{targetId}' is not a valid backup target id.");
        }

        return Path.Combine(BackupRoot, targetId);
    }

    public bool IsRunning(string targetId) => _running.ContainsKey(targetId);

    /// <summary>Runs a backup, refusing to overlap another run of the same target.</summary>
    public async Task<object> RunGuardedAsync(BackupTarget target)
    {
        if (!_running.TryAdd(target.Id, 0))
        {
            throw new InvalidOperationException("A backup for this target is already running.");
        }

        try
        {
            return await BackupAsync(target);
        }
        finally
        {
            _running.TryRemove(target.Id, out _);
        }
    }

    /// <summary>Runs one backup, prunes old snapshots, and records the run time.</summary>
    public async Task<object> BackupAsync(BackupTarget target)
    {
        if (!BackupNaming.IsValidId(target.Id))
        {
            throw new ArgumentException($"'{target.Id}' is not a valid backup id.");
        }

        if (_system.RootFreeBytes() is { } free && free < MinFreeBytes)
        {
            throw new InvalidOperationException(
                $"Only {free / 1024 / 1024} MB free on disk — free some space before backing up.");
        }

        var directory = TargetDirectory(target.Id);
        Directory.CreateDirectory(directory);
        var timestamp = DateTimeOffset.UtcNow;
        var fileName = BackupNaming.FileName(target.Engine, timestamp);
        var hostPath = Path.Combine(directory, fileName);

        if (BackupNaming.IsVolume(target.Kind, target.Engine))
        {
            await _docker.BackupVolumeAsync(target.Name, directory, fileName);
        }
        else
        {
            await DumpDatabaseAsync(target, hostPath);
        }

        Prune(target);
        SetLastRun(target.Id, timestamp);
        var size = File.Exists(hostPath) ? new FileInfo(hostPath).Length : 0;
        _logger.LogWarning("Backup of {Target} → {File} ({Size} bytes)", target.Id, fileName, size);

        // The local snapshot is already written and is the copy that matters most,
        // so a bucket that cannot be reached is reported beside a successful backup
        // rather than turning one into a failure — which would hide a backup that
        // did work and raise an alert about the wrong thing.
        var offsite = _offsite is null ? null : await _offsite.UploadAsync(target.Id, hostPath);
        if (offsite is not null)
        {
            _logger.LogWarning("The offsite copy of {Target} failed: {Detail}", target.Id, offsite);
        }

        return new { ok = true, snapshot = fileName, sizeBytes = size, offsiteError = offsite };
    }

    private async Task DumpDatabaseAsync(BackupTarget target, string hostPath)
    {
        // Each dump writes to a temp file inside the container, then docker cp
        // brings it out — this never buffers a large dump in the dashboard.
        var (dump, containerFile) = DumpPlan(target.Engine);
        await _docker.ExecAsync(target.Name, dump);
        await _docker.CopyFromContainerAsync(target.Name, containerFile, hostPath);
        if (!containerFile.StartsWith("/data/", StringComparison.Ordinal)) // don't delete redis's live dump.rdb
        {
            try
            {
                await _docker.ExecAsync(target.Name, "rm", "-f", containerFile);
            }
            catch (InvalidOperationException)
            {
                // Best-effort cleanup of the in-container temp file.
            }
        }
    }

    /// <summary>The in-container dump command and the file it produces, per engine.</summary>
    public static (string[] Command, string ContainerFile) DumpPlan(string engine) => engine switch
    {
        "postgres" => (["pg_dumpall", "-U", "postgres", "-f", "/tmp/pinqops-backup.sql"], "/tmp/pinqops-backup.sql"),
        "mysql" => (["sh", "-c", "mysqldump -uroot -p\"$MYSQL_ROOT_PASSWORD\" --all-databases --result-file=/tmp/pinqops-backup.sql"], "/tmp/pinqops-backup.sql"),
        "mariadb" => (["sh", "-c", "mariadb-dump -uroot -p\"$MARIADB_ROOT_PASSWORD\" --all-databases --result-file=/tmp/pinqops-backup.sql"], "/tmp/pinqops-backup.sql"),
        "mongo" => (["mongodump", "--archive=/tmp/pinqops-backup.archive"], "/tmp/pinqops-backup.archive"),
        "redis" => (["redis-cli", "SAVE"], "/data/dump.rdb"),
        _ => throw new ArgumentException($"Backups are not supported for engine '{engine}'."),
    };

    /// <summary>The in-container restore command, per engine (reads /tmp/pinqops-restore.*).</summary>
    public static string[] RestorePlan(string engine) => engine switch
    {
        // ON_ERROR_STOP=1 because psql does not stop, and does not fail, on its own
        // errors: it prints every statement error and still exits 0. The exit code is
        // all the restore reads, so without this a dump that applied nothing — cut
        // short, half-copied, or conflicting with what the cluster already holds —
        // was reported as a completed restore. The other three engines exit non-zero
        // on the first error, which is why this is the only arm that needs it; the
        // database upgrade path carries the same flag for the same reason.
        "postgres" => ["sh", "-c", "psql -v ON_ERROR_STOP=1 -U postgres -f /tmp/pinqops-restore.sql postgres"],
        "mysql" => ["sh", "-c", "mysql -uroot -p\"$MYSQL_ROOT_PASSWORD\" < /tmp/pinqops-restore.sql"],
        "mariadb" => ["sh", "-c", "mariadb -uroot -p\"$MARIADB_ROOT_PASSWORD\" < /tmp/pinqops-restore.sql"],
        "mongo" => ["mongorestore", "--archive=/tmp/pinqops-restore.archive", "--drop"],
        _ => throw new ArgumentException($"Restore is not supported for engine '{engine}'."),
    };

    public async Task RestoreAsync(BackupTarget target, string snapshot)
    {
        if (!BackupNaming.IsValidSnapshot(snapshot))
        {
            throw new ArgumentException("Invalid snapshot name.");
        }

        var hostPath = Path.Combine(TargetDirectory(target.Id), snapshot);
        if (!File.Exists(hostPath))
        {
            throw new ArgumentException("That snapshot no longer exists.");
        }

        if (BackupNaming.IsVolume(target.Kind, target.Engine))
        {
            await _docker.RestoreVolumeAsync(target.Name, TargetDirectory(target.Id), snapshot);
        }
        else if (target.Engine == "redis")
        {
            // Redis loads its RDB at startup: stop, replace the file, start.
            //
            // The start is in a finally because it is the only thing that undoes
            // the stop. A copy that failed — a snapshot deleted underneath us, a
            // full disk, a daemon that went away — left the container stopped, so a
            // failed restore took the cache or session store down with it and the
            // error said nothing about that. The original failure still surfaces:
            // it propagates unless the restart fails too.
            await _docker.ContainerActionAsync(target.Name, "stop");
            try
            {
                await _docker.CopyToContainerAsync(hostPath, target.Name, "/data/dump.rdb");
            }
            finally
            {
                await _docker.ContainerActionAsync(target.Name, "start");
            }
        }
        else
        {
            var containerFile = $"/tmp/pinqops-restore.{BackupNaming.Extension(target.Engine)}";
            await _docker.CopyToContainerAsync(hostPath, target.Name, containerFile);
            await _docker.ExecAsync(target.Name, RestorePlan(target.Engine));
            try
            {
                await _docker.ExecAsync(target.Name, "rm", "-f", containerFile);
            }
            catch (InvalidOperationException)
            {
            }
        }

        _logger.LogWarning("Restored {Target} from {Snapshot}", target.Id, snapshot);
    }

    public IReadOnlyList<object> ListSnapshots(string targetId)
    {
        // Listing is best-effort: one corrupt id in backups.json should leave that
        // target empty rather than break the whole backups page.
        if (!BackupNaming.IsValidId(targetId))
        {
            return [];
        }

        var directory = TargetDirectory(targetId);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return new DirectoryInfo(directory).GetFiles()
            .Where(file => BackupNaming.IsValidSnapshot(file.Name))
            .OrderByDescending(file => file.Name, StringComparer.Ordinal)
            .Select(file => (object)new { name = file.Name, sizeBytes = file.Length, at = file.CreationTimeUtc })
            .ToList();
    }

    public void DeleteSnapshot(string targetId, string snapshot)
    {
        if (!BackupNaming.IsValidId(targetId) || !BackupNaming.IsValidSnapshot(snapshot))
        {
            throw new ArgumentException("Invalid snapshot.");
        }

        var path = Path.Combine(TargetDirectory(targetId), snapshot);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>A validated absolute path for downloading a snapshot, or null.</summary>
    public string? SnapshotPath(string targetId, string snapshot)
    {
        if (!BackupNaming.IsValidId(targetId) || !BackupNaming.IsValidSnapshot(snapshot))
        {
            return null;
        }

        var path = Path.Combine(TargetDirectory(targetId), snapshot);
        return File.Exists(path) ? path : null;
    }

    private void Prune(BackupTarget target)
    {
        if (target.RetentionCount <= 0 || !Directory.Exists(TargetDirectory(target.Id)))
        {
            return;
        }

        var stale = new DirectoryInfo(TargetDirectory(target.Id)).GetFiles()
            .Where(file => BackupNaming.IsValidSnapshot(file.Name))
            .OrderByDescending(file => file.Name, StringComparer.Ordinal)
            .Skip(target.RetentionCount);
        foreach (var file in stale)
        {
            file.Delete();
        }
    }

    // ---- last-run state -----------------------------------------------------------

    private readonly Lock _lastRunGate = new();

    /// <summary>
    /// When this target last ran, or null if it never has.
    ///
    /// TryGetValue rather than GetValueOrDefault: the dictionary's value type is
    /// non-nullable, so a missing key came back as
    /// <c>default(DateTimeOffset)</c> — a non-null 0001-01-01, which the dashboard
    /// rendered as "739983d ago" instead of the "never" its null check was written
    /// for.
    /// </summary>
    public DateTimeOffset? LastRun(string targetId)
    {
        lock (_lastRunGate)
        {
            return LoadLastRun().TryGetValue(targetId, out var at) ? at : null;
        }
    }

    private void SetLastRun(string targetId, DateTimeOffset at)
    {
        // The scheduler runs targets concurrently, so this read-modify-write of
        // the shared lastRun.json must be serialized — otherwise two finishing
        // backups each write only their own entry and the last writer wins,
        // dropping the other's timestamp and re-firing it every minute until the
        // hour rolls over. The write is atomic so a concurrent read can't see a
        // torn file either.
        lock (_lastRunGate)
        {
            var map = LoadLastRun();
            map[targetId] = at;
            SecureFile.WriteAllText(_lastRunPath, JsonSerializer.Serialize(map), ownerOnly: false);
        }
    }

    private Dictionary<string, DateTimeOffset> LoadLastRun()
    {
        try
        {
            if (File.Exists(_lastRunPath))
            {
                return JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(SecureFile.ReadAllText(_lastRunPath))
                    ?? [];
            }
        }
        catch (JsonException)
        {
        }

        return [];
    }
}
