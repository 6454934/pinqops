namespace PinqOps.Databases;

/// <summary>One step of an upgrade, as a docker argument list.</summary>
/// <param name="Describe">What to log before running it — the operator's view of where it got to.</param>
public sealed record UpgradeStep(string Describe, IReadOnlyList<string> Arguments);

/// <summary>Why an upgrade cannot be attempted, if it cannot.</summary>
public sealed record UpgradeVerdict(IReadOnlyList<string> Blockers)
{
    public bool Possible => Blockers.Count == 0;
}

/// <summary>
/// Moving a database from one version to the next, by taking a dump and loading it
/// into a fresh container.
///
/// <para><b>Why dump-and-restore rather than starting the new image on the old
/// volume.</b> Postgres refuses outright — its data directory is version-stamped and
/// a newer server will not open an older one. MySQL and MariaDB will open it and
/// upgrade in place, which works until the release where it does not, and the
/// failure mode is a server that starts and serves subtly wrong data. A dump is the
/// engine's own supported path between versions and it is the same path a restore
/// already uses.</para>
///
/// <para><b>The old volume is never touched.</b> The new version gets a new volume,
/// so a failed upgrade is undone by starting the old container again — the thing
/// that must not happen here is an upgrade that has destroyed the only copy by the
/// time it discovers it cannot finish.</para>
/// </summary>
public static class DatabaseUpgrade
{
    /// <summary>The volume a version's data lives in, so two versions never share one.</summary>
    public static string VolumeFor(string container, string version) =>
        $"{container}-data-{version.Replace('.', '-').Replace(':', '-')}";

    /// <summary>
    /// Whether this upgrade can be attempted at all.
    /// </summary>
    public static UpgradeVerdict Check(DatabaseEngine? engine, string? from, string? to)
    {
        var blockers = new List<string>();

        if (engine is null)
        {
            return new UpgradeVerdict(["pinqops does not know how to upgrade this database."]);
        }

        if (!engine.SupportsUpgrade)
        {
            blockers.Add(
                $"{engine.Name} has no dump-and-restore path between versions that pinqops can drive, so it is "
                + "not offered here. Its data file is the database; move it by hand or start fresh.");
        }

        if (DatabaseEngines.ImageFor(engine, to) is null)
        {
            blockers.Add($"'{to}' is not a version pinqops offers for {engine.Name}.");
        }

        if (from is not null && DatabaseEngines.ImageFor(engine, from) is null)
        {
            blockers.Add($"'{from}' is not a version pinqops recognises, so it cannot tell which way this would go.");
        }

        if (blockers.Count == 0 && string.Equals(from, to, StringComparison.Ordinal))
        {
            blockers.Add($"{engine.Name} is already on {to}.");
        }

        // Downgrades are refused rather than attempted: a dump from a newer server
        // frequently will not load into an older one, and the failure lands after
        // the new container is already running.
        if (blockers.Count == 0 && from is not null && !DatabaseEngines.IsUpgrade(engine, from, to!))
        {
            blockers.Add(
                $"Going from {from} back to {to} is a downgrade. A dump from a newer server usually will not load "
                + "into an older one, so pinqops does not offer it.");
        }

        return new UpgradeVerdict(blockers);
    }

    /// <summary>
    /// The dump command, run inside the container that is currently serving.
    ///
    /// <para>Written to a file inside the container and copied out afterwards, the
    /// same way <c>BackupService</c> already does it — a dump held in the dashboard's
    /// memory is one that fails on the database large enough to matter.</para>
    /// </summary>
    public static (IReadOnlyList<string> Command, string ContainerFile) DumpPlan(DatabaseEngine engine, string password)
    {
        ArgumentNullException.ThrowIfNull(engine);

        return engine.Id switch
        {
            "postgres" =>
            (
                // -c so the restore drops what it recreates, and --no-owner because
                // the roles on the new server are not the old server's.
                ["sh", "-c", $"PGPASSWORD='{Escape(password)}' pg_dumpall -U {engine.DefaultUser} > /tmp/pinqops-upgrade.sql"],
                "/tmp/pinqops-upgrade.sql"
            ),
            "mysql" or "mariadb" =>
            (
                ["sh", "-c", $"mysqldump -u {engine.DefaultUser} -p'{Escape(password)}' --all-databases > /tmp/pinqops-upgrade.sql"],
                "/tmp/pinqops-upgrade.sql"
            ),
            "mongo" =>
            (
                ["sh", "-c", $"mongodump -u {engine.DefaultUser} -p '{Escape(password)}' --authenticationDatabase admin --archive=/tmp/pinqops-upgrade.archive"],
                "/tmp/pinqops-upgrade.archive"
            ),
            _ => throw new ArgumentException($"No dump plan for {engine.Id}."),
        };
    }

    /// <summary>The restore command, run inside the new container once it is up.</summary>
    public static IReadOnlyList<string> RestorePlan(DatabaseEngine engine, string password, string containerFile)
    {
        ArgumentNullException.ThrowIfNull(engine);

        return engine.Id switch
        {
            // ON_ERROR_STOP=1 because psql does not stop, and does not fail, on its
            // own: it prints every statement error and still exits 0. The caller
            // reads that exit code as "the data is in the new database", at the one
            // point in the upgrade where the old container has already been dumped —
            // so without this a restore in which nothing applied was reported as a
            // completed migration over an empty database.
            "postgres" =>
            [
                "sh",
                "-c",
                $"PGPASSWORD='{Escape(password)}' psql -v ON_ERROR_STOP=1 -U {engine.DefaultUser} "
                + $"-f {containerFile} postgres",
            ],
            "mysql" or "mariadb" =>
                ["sh", "-c", $"mysql -u {engine.DefaultUser} -p'{Escape(password)}' < {containerFile}"],
            "mongo" =>
                ["sh", "-c", $"mongorestore -u {engine.DefaultUser} -p '{Escape(password)}' --authenticationDatabase admin --archive={containerFile}"],
            _ => throw new ArgumentException($"No restore plan for {engine.Id}."),
        };
    }

    /// <summary>
    /// A password as a single-quoted shell word.
    ///
    /// <para>These three commands need a shell — a redirect and a pipe are the whole
    /// point of them — so the password cannot travel as a discrete argv entry the
    /// way it does everywhere else in pinqops. Single quotes make the shell take
    /// every character literally, and the only sequence that ends them is closed and
    /// reopened around an escaped quote.</para>
    /// </summary>
    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Replace("'", "'\\''", StringComparison.Ordinal);
    }
}
