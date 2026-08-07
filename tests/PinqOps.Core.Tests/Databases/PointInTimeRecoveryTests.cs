using PinqOps.Databases;
using Xunit;

namespace PinqOps.Tests.Databases;

/// <summary>
/// The decisions behind a point-in-time recovery. Every one of them fails silently
/// if it is wrong — a missing WAL segment, a target before the base backup, an
/// archive pruned too far — and each is discovered during a recovery, which is the
/// worst moment to discover anything.
/// </summary>
public class PointInTimeRecoveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private static BaseBackup Backup(int daysAgo) =>
        new($"base-{daysAgo}", Now.AddDays(-daysAgo), 1024);

    // ---- the archive configuration -------------------------------------------

    /// <summary>
    /// Not belt and braces: postgres treats a successful archive command as "safely
    /// stored" and is then free to recycle the segment. A command that overwrote an
    /// existing file would report success for a segment it had just destroyed, and
    /// the gap only shows up during a recovery.
    /// </summary>
    [Fact]
    public void TheArchiveCommandRefusesToOverwriteAnExistingSegment()
    {
        var settings = string.Join('\n', PointInTimeRecovery.ArchiveSettings());

        Assert.Contains("test ! -f", settings, StringComparison.Ordinal);
        Assert.Contains("archive_mode = on", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWalLevelIsHighEnoughToArchive() =>
        Assert.Contains(
            "wal_level = replica",
            string.Join('\n', PointInTimeRecovery.ArchiveSettings()),
            StringComparison.Ordinal);

    /// <summary>
    /// A segment is archived when it fills or when this elapses, so what can be lost
    /// is bounded by time rather than by how busy the database happens to be.
    /// </summary>
    [Fact]
    public void AnIdleDatabaseStillArchivesOnATimer() =>
        Assert.Contains(
            "archive_timeout",
            string.Join('\n', PointInTimeRecovery.ArchiveSettings()),
            StringComparison.Ordinal);

    // ---- the recovery configuration ------------------------------------------

    /// <summary>
    /// Postgres reads a bare timestamp in the server's timezone, and "which
    /// timezone" is exactly the question nobody wants to be answering during a
    /// recovery.
    /// </summary>
    [Fact]
    public void TheRecoveryTargetCarriesItsOffset()
    {
        var settings = string.Join('\n', PointInTimeRecovery.RecoverySettings(Now));

        Assert.Contains("recovery_target_time = '2026-08-02 12:00:00+00:00'", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void ATargetInAnotherZoneIsWrittenInUtc()
    {
        var settings = string.Join(
            '\n', PointInTimeRecovery.RecoverySettings(new DateTimeOffset(2026, 8, 2, 15, 0, 0, TimeSpan.FromHours(3))));

        Assert.Contains("2026-08-02 12:00:00+00:00", settings, StringComparison.Ordinal);
    }

    /// <summary>
    /// The alternative leaves the server paused, which looks like a hung restore to
    /// anyone who did not choose it.
    /// </summary>
    [Fact]
    public void TheServerComesUpWritableWhenItReachesTheTarget() =>
        Assert.Contains(
            "recovery_target_action = 'promote'",
            string.Join('\n', PointInTimeRecovery.RecoverySettings(Now)),
            StringComparison.Ordinal);

    // ---- whether a recovery is possible at all --------------------------------

    [Fact]
    public void ARecoveryInsideTheWindowIsPossible() =>
        Assert.True(PointInTimeRecovery.Check([Backup(7)], Now.AddDays(-1), Now).Possible);

    [Fact]
    public void WithNoBaseBackupThereIsNothingToRecoverFrom()
    {
        var verdict = PointInTimeRecovery.Check([], Now.AddDays(-1), Now);

        Assert.False(verdict.Possible);
        Assert.Contains("WAL alone is not a backup", string.Join(" ", verdict.Blockers));
    }

    /// <summary>
    /// Recovery replays forward from a base backup, so a target before every one of
    /// them is not something this archive can do however much WAL is kept.
    /// </summary>
    [Fact]
    public void ATargetBeforeEveryBaseBackupIsRefused()
    {
        var verdict = PointInTimeRecovery.Check([Backup(3)], Now.AddDays(-10), Now);

        Assert.False(verdict.Possible);
        Assert.Contains("replays forward", string.Join(" ", verdict.Blockers));
    }

    [Fact]
    public void ATargetInTheFutureIsRefused() =>
        Assert.Contains(
            "has not happened yet",
            string.Join(" ", PointInTimeRecovery.Check([Backup(3)], Now.AddHours(1), Now).Blockers));

    // ---- which base backup to start from --------------------------------------

    /// <summary>
    /// The most recent one at or before the target, because replay time is the cost:
    /// starting from a month-old base and replaying a month of WAL takes about as
    /// long as the month did.
    /// </summary>
    [Fact]
    public void RecoveryStartsFromTheMostRecentBaseBackupBeforeTheTarget()
    {
        var backups = new[] { Backup(10), Backup(5), Backup(1) };

        var start = PointInTimeRecovery.StartingPointFor(backups, Now.AddDays(-3));

        Assert.Equal("base-5", start!.Name);
    }

    [Fact]
    public void ATargetBeforeEveryBaseBackupHasNoStartingPoint() =>
        Assert.Null(PointInTimeRecovery.StartingPointFor([Backup(3)], Now.AddDays(-10)));

    // ---- retention ------------------------------------------------------------

    [Fact]
    public void RetentionDropsTheOldestBaseBackupsFirst()
    {
        var backups = new[] { Backup(10), Backup(5), Backup(1) };

        var deleted = PointInTimeRecovery.BaseBackupsToDelete(backups, keep: 2);

        Assert.Equal(["base-10"], deleted.Select(backup => backup.Name));
    }

    /// <summary>
    /// A sweep that could empty the set would turn "keep 3 backups" into "keep no
    /// way to recover".
    /// </summary>
    [Fact]
    public void RetentionNeverEmptiesTheSet()
    {
        var backups = new[] { Backup(10), Backup(5) };

        Assert.Single(PointInTimeRecovery.BaseBackupsToDelete(backups, keep: 0));
        Assert.Single(PointInTimeRecovery.BaseBackupsToDelete(backups, keep: -5));
    }

    [Fact]
    public void KeepingMoreThanThereAreDeletesNothing() =>
        Assert.Empty(PointInTimeRecovery.BaseBackupsToDelete([Backup(1)], keep: 10));

    /// <summary>
    /// A base backup without the WAL that follows it restores only to the instant it
    /// was taken — a backup, but not point-in-time recovery, and the difference is
    /// not noticed until somebody needs the difference.
    /// </summary>
    [Fact]
    public void NoWalAtOrAfterTheOldestKeptBaseBackupIsEverDropped()
    {
        // Two segments predate the kept base backup and one follows it. Only the
        // ones before it can go; the one after is what makes recovery *to a point*
        // possible rather than only to the instant the base was taken.
        var segments = new[]
        {
            ("000000010000000000000001", Now.AddDays(-9)),
            ("000000010000000000000002", Now.AddDays(-6)),
            ("000000010000000000000003", Now.AddDays(-2)),
        };

        var deleted = PointInTimeRecovery.WalToDelete(segments, [Backup(5)]);

        Assert.Equal(["000000010000000000000001", "000000010000000000000002"], deleted);
    }

    [Fact]
    public void WithNoBaseBackupKeptTheArchiveIsLeftAlone()
    {
        // The alternative deletes the only thing that could still be useful.
        var segments = new[] { ("000000010000000000000001", Now.AddDays(-9)) };

        Assert.Empty(PointInTimeRecovery.WalToDelete(segments, []));
    }

    // ---- the window -----------------------------------------------------------

    /// <summary>
    /// The end of the window is the last segment that reached the archive, not now:
    /// the WAL since then is still in the live server and would be lost with it.
    /// </summary>
    [Fact]
    public void TheWindowEndsAtTheLastArchivedSegmentRatherThanNow()
    {
        var window = PointInTimeRecovery.Window([Backup(7)], Now.AddMinutes(-4), Now);

        Assert.Equal(Now.AddDays(-7), window!.Value.From);
        Assert.Equal(Now.AddMinutes(-4), window.Value.To);
    }

    [Fact]
    public void WithNothingArchivedTheWindowIsTheBaseBackupItself()
    {
        var window = PointInTimeRecovery.Window([Backup(7)], lastArchivedAt: null, Now);

        Assert.Equal(Now.AddDays(-7), window!.Value.To);
    }

    [Fact]
    public void WithNoBaseBackupThereIsNoWindow() =>
        Assert.Null(PointInTimeRecovery.Window([], Now, Now));
}
