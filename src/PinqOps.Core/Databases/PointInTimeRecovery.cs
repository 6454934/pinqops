using System.Globalization;

namespace PinqOps.Databases;

/// <summary>One base backup, and the moment it was taken.</summary>
public sealed record BaseBackup(string Name, DateTimeOffset TakenAt, long SizeBytes);

/// <summary>Why a recovery cannot be attempted, if it cannot.</summary>
public sealed record RecoveryVerdict(IReadOnlyList<string> Blockers)
{
    public bool Possible => Blockers.Count == 0;
}

/// <summary>
/// Point-in-time recovery, for PostgreSQL and nothing else.
///
/// <para><b>Why only PostgreSQL.</b> PITR needs a write-ahead log that can be
/// archived continuously and replayed against a base backup to an arbitrary instant.
/// Postgres has exactly that, and it is configuration rather than tooling. MySQL's
/// binlog can approximate it with different commands and different failure modes,
/// MongoDB's oplog is a replica-set feature, and Redis has nothing of the kind.
/// Offering "PITR" that means something different for each engine would be a
/// promise pinqops could not keep, so the page says PostgreSQL and means it.</para>
///
/// <para><b>What it does not do.</b> This is a scheduled base backup plus archived
/// WAL on one machine. It is not replication, it does not survive the disk, and it
/// does not make the database highly available. Copying the archive offsite is the
/// part that makes it a disaster plan, and that is the offsite backup feature.</para>
/// </summary>
public static class PointInTimeRecovery
{
    /// <summary>Where the archived segments and base backups live inside the container.</summary>
    public const string ArchiveMount = "/pinqops-wal";

    public const string BaseBackupMount = "/pinqops-base";

    /// <summary>
    /// The <c>postgresql.conf</c> lines that turn continuous archiving on.
    ///
    /// <para><b>The archive command refuses to overwrite.</b> <c>test ! -f</c> before
    /// the copy is not belt and braces — postgres treats a successful archive command
    /// as "this segment is safely stored" and is then free to recycle it. A command
    /// that silently overwrote an existing file would report success for a segment it
    /// had just destroyed, and the gap only appears during a recovery.</para>
    ///
    /// <para><b>It is appended, not written.</b> The file may hold tuning somebody
    /// put there; postgres takes the last setting of a name, so appending is both
    /// safe and the documented way to override.</para>
    /// </summary>
    public static IReadOnlyList<string> ArchiveSettings() =>
    [
        "# pinqops: continuous archiving for point-in-time recovery.",
        "wal_level = replica",
        "archive_mode = on",
        $"archive_command = 'test ! -f {ArchiveMount}/%f && cp %p {ArchiveMount}/%f'",
        // A segment is archived when it fills or when this elapses, so the window
        // that can be lost is bounded by time rather than by write volume.
        "archive_timeout = 300",
    ];

    /// <summary>
    /// The recovery settings for restoring to <paramref name="target"/>.
    ///
    /// <para><c>recovery_target_action = 'promote'</c> so the server comes up
    /// writable once it has replayed to the target. The alternative leaves it paused,
    /// which looks like a hung restore to anyone who did not choose it.</para>
    /// </summary>
    public static IReadOnlyList<string> RecoverySettings(DateTimeOffset target) =>
    [
        "# pinqops: recovering to a point in time.",
        $"restore_command = 'cp {ArchiveMount}/%f %p'",
        // ISO 8601 with an explicit offset: postgres reads a bare timestamp in the
        // server's timezone, and "which timezone" is exactly the question nobody
        // wants to be answering during a recovery.
        $"recovery_target_time = '{target.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ssK", CultureInfo.InvariantCulture)}'",
        "recovery_target_action = 'promote'",
    ];

    /// <summary>
    /// Whether a recovery to <paramref name="target"/> is possible with the base
    /// backups on hand.
    /// </summary>
    public static RecoveryVerdict Check(
        IReadOnlyList<BaseBackup> baseBackups, DateTimeOffset target, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(baseBackups);

        var blockers = new List<string>();

        if (baseBackups.Count == 0)
        {
            blockers.Add("There is no base backup to recover from. Take one first — WAL alone is not a backup.");
            return new RecoveryVerdict(blockers);
        }

        if (target > now)
        {
            blockers.Add("That moment has not happened yet.");
        }

        // Recovery replays forward from a base backup, so the target has to be after
        // one of them. A target before every base backup is not a recovery this
        // archive can perform, however much WAL is kept.
        var oldest = baseBackups.Min(backup => backup.TakenAt);
        if (target < oldest)
        {
            blockers.Add(
                $"The oldest base backup is from {oldest.ToUniversalTime():yyyy-MM-dd HH:mm} UTC, and recovery "
                + "replays forward from one — there is nothing to replay from for a moment before that.");
        }

        return new RecoveryVerdict(blockers);
    }

    /// <summary>
    /// The base backup a recovery to <paramref name="target"/> should start from:
    /// the most recent one taken at or before it.
    ///
    /// <para>The most recent rather than the oldest, because replay time is the cost
    /// — starting from a month-old base and replaying a month of WAL takes about as
    /// long as the month did.</para>
    /// </summary>
    public static BaseBackup? StartingPointFor(IReadOnlyList<BaseBackup> baseBackups, DateTimeOffset target)
    {
        ArgumentNullException.ThrowIfNull(baseBackups);

        return baseBackups
            .Where(backup => backup.TakenAt <= target)
            .OrderByDescending(backup => backup.TakenAt)
            .FirstOrDefault();
    }

    /// <summary>
    /// The base backups to delete, keeping <paramref name="keep"/> of them.
    ///
    /// <para>Oldest first, and never the newest: a retention sweep that could empty
    /// the set would turn "keep 3 backups" into "keep no way to recover".</para>
    /// </summary>
    public static IReadOnlyList<BaseBackup> BaseBackupsToDelete(IReadOnlyList<BaseBackup> baseBackups, int keep)
    {
        ArgumentNullException.ThrowIfNull(baseBackups);

        var kept = Math.Max(1, keep);
        return
        [
            .. baseBackups
                .OrderBy(backup => backup.TakenAt)
                .Take(Math.Max(0, baseBackups.Count - kept)),
        ];
    }

    /// <summary>
    /// The archived WAL segments that are no longer needed, given the base backups
    /// being kept.
    ///
    /// <para><b>Nothing at or after the oldest kept base backup is ever dropped.</b>
    /// A base backup without the WAL that follows it can only be restored to the
    /// instant it was taken — which is a backup, but not point-in-time recovery, and
    /// the difference would not be noticed until somebody needed the difference.</para>
    ///
    /// <para>A segment's own timestamp is used rather than its name: WAL file names
    /// are a hex sequence with no time in them, and pinqops has no business decoding
    /// postgres' numbering to decide what to delete.</para>
    /// </summary>
    public static IReadOnlyList<string> WalToDelete(
        IReadOnlyList<(string Name, DateTimeOffset ArchivedAt)> segments,
        IReadOnlyList<BaseBackup> keptBaseBackups)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(keptBaseBackups);

        if (keptBaseBackups.Count == 0)
        {
            // Nothing to anchor the archive to. Keeping it all is the safe answer:
            // the alternative deletes the only thing that could still be useful.
            return [];
        }

        var oldestKept = keptBaseBackups.Min(backup => backup.TakenAt);
        return
        [
            .. segments
                .Where(segment => segment.ArchivedAt < oldestKept)
                .OrderBy(segment => segment.ArchivedAt)
                .Select(segment => segment.Name),
        ];
    }

    /// <summary>
    /// How far back a recovery can go and how recent it can be, given what is on
    /// hand. Null when there is nothing to recover from.
    /// </summary>
    public static (DateTimeOffset From, DateTimeOffset To)? Window(
        IReadOnlyList<BaseBackup> baseBackups, DateTimeOffset? lastArchivedAt, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(baseBackups);

        if (baseBackups.Count == 0)
        {
            return null;
        }

        // The end of the window is the last segment that reached the archive, not
        // now: the WAL since then is still in the live server and would be lost with
        // it. Saying "up to now" would be a promise the archive cannot keep.
        return (baseBackups.Min(backup => backup.TakenAt), lastArchivedAt ?? baseBackups.Max(backup => backup.TakenAt));
    }
}
