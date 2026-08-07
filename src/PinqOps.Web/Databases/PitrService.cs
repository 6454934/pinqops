using System.Globalization;
using System.Text.Json;
using PinqOps.Databases;

namespace PinqOps.Web;

/// <summary>Where a project's PITR archive lives and how much of it is kept.</summary>
public sealed class PitrConfig
{
    private string _container = string.Empty;

    /// <summary>Off is every existing install, and off changes nothing.</summary>
    public bool Enabled { get; set; }

    /// <summary>The PostgreSQL container this archives. One per install, deliberately.</summary>
    public string Container { get => _container; set => _container = value ?? string.Empty; }

    /// <summary>How many base backups to keep. Never fewer than one.</summary>
    public int KeepBaseBackups { get; set; } = 3;

    public DateTimeOffset? LastBaseBackupAt { get; set; }
}

/// <summary>
/// Continuous archiving and recovery to a point in time, for PostgreSQL.
///
/// <para><b>The volumes are what make this work.</b> The archive and the base
/// backups live in named docker volumes mounted into the database container, so they
/// survive the container being recreated — which is exactly what an upgrade or a
/// restore does. They do not survive the disk; copying them offsite is the offsite
/// backup feature, and this page says so.</para>
///
/// <para><b>A restore never overwrites the running database.</b> It builds a second
/// container from the base backup and replays into that. What comes up is a server
/// holding the data as of the chosen moment, beside the one that is still serving —
/// switching to it is a separate, deliberate act.</para>
/// </summary>
public sealed class PitrService
{
    /// <summary>The volumes holding the archive. Named so they are recognisable in `docker volume ls`.</summary>
    public const string ArchiveVolume = "pinqops-pitr-wal";

    public const string BaseVolume = "pinqops-pitr-base";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(30);

    private readonly IProcessRunner _processRunner;
    private readonly DockerService _docker;
    private readonly PitrConfigStore _store;
    private readonly AppCredentialStore _credentials;
    private readonly ILogger<PitrService> _logger;

    public PitrService(
        IProcessRunner processRunner,
        DockerService docker,
        PitrConfigStore store,
        AppCredentialStore credentials,
        ILogger<PitrService> logger)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(docker);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(logger);
        _processRunner = processRunner;
        _docker = docker;
        _store = store;
        _credentials = credentials;
        _logger = logger;
    }

    public PitrConfigStore Store => _store;

    /// <summary>
    /// The base backups on hand, newest first, and when the archive last received a
    /// segment.
    /// </summary>
    public async Task<(IReadOnlyList<BaseBackup> Backups, DateTimeOffset? LastArchivedAt)> StateAsync(
        CancellationToken cancellationToken = default)
    {
        var backups = await ListAsync(BaseVolume, cancellationToken).ConfigureAwait(false);
        var segments = await ListAsync(ArchiveVolume, cancellationToken).ConfigureAwait(false);

        return (
            [.. backups.OrderByDescending(backup => backup.TakenAt)],
            segments.Count == 0 ? null : segments.Max(segment => segment.TakenAt));
    }

    /// <summary>
    /// Reads a volume's entries through a throwaway container, the same way the
    /// volume browser does — the dashboard has no access to <c>/var/lib/docker</c>.
    /// </summary>
    private async Task<IReadOnlyList<BaseBackup>> ListAsync(string volume, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);

        var result = await _processRunner.RunAsync(
            "docker",
            [
                "run", "--rm", "-v", $"{volume}:/v:ro", "alpine",
                "sh", "-c", "find /v -maxdepth 1 -mindepth 1 -exec stat -c '%Y|%s|%n' {} + 2>/dev/null || true",
            ],
            null,
            timeout.Token).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            // Never "the archive is empty". The shell above already swallows its own
            // errors and ends in `|| true`, and a named volume that does not exist is
            // created empty and still exits 0 — so a non-zero exit can only mean the
            // read did not happen. Reported as no base backups, that told an operator
            // there was nothing to recover from, during the outage that had them
            // reading the page, while the archive sat intact in the volume.
            var detail = DockerDaemonError.Describe(result.StandardError)
                ?? (result.StandardError.Trim() is { Length: > 0 } stderr
                    ? stderr
                    : $"docker exited with code {result.ExitCode}.");

            _logger.LogWarning("The recovery archive in '{Volume}' could not be read: {Detail}", volume, detail);
            throw new InvalidOperationException($"The recovery archive could not be read: {detail}");
        }

        var entries = new List<BaseBackup>();
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|', 3);
            if (parts.Length < 3
                || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var epoch))
            {
                continue;
            }

            _ = long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size);
            entries.Add(new BaseBackup(
                Path.GetFileName(parts[2].Trim()),
                DateTimeOffset.FromUnixTimeSeconds(epoch),
                size));
        }

        return entries;
    }

    /// <summary>
    /// Takes a base backup with <c>pg_basebackup</c>, and prunes the archive to the
    /// retention count.
    ///
    /// <para>The prune runs after the new backup exists, never before: the order the
    /// other way round is the one where a failed backup leaves fewer copies than
    /// there were.</para>
    /// </summary>
    public async Task<string?> TakeBaseBackupAsync(CancellationToken cancellationToken = default)
    {
        var config = _store.Load();
        if (!config.Enabled || config.Container.Length == 0)
        {
            return "Point-in-time recovery is not turned on.";
        }

        var password = _credentials.Get(ManagedEnvironment.LocalId, "postgres") is { } env
            && env.TryGetValue("POSTGRES_PASSWORD", out var stored)
                ? stored
                : null;
        if (password is null)
        {
            return "pinqops has no stored password for PostgreSQL.";
        }

        var name = $"base-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
        var command = new[]
        {
            "sh", "-c",
            $"PGPASSWORD='{DatabaseUpgrade.Escape(password)}' pg_basebackup -U postgres -D "
            + $"{PointInTimeRecovery.BaseBackupMount}/{name} -Ft -z -Xf",
        };

        try
        {
            await _docker.ExecAsync(config.Container, command).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            return $"The base backup failed: {exception.Message}";
        }

        _logger.LogWarning("Base backup {Name} taken from {Container}", name, config.Container);

        config.LastBaseBackupAt = DateTimeOffset.UtcNow;
        _store.Save(config);

        await PruneAsync(config, cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Drops the base backups past the retention count, and then the WAL that only
    /// they needed.
    ///
    /// <para>In that order: the WAL decision is made from the backups that remain, so
    /// computing it first would use a list that is about to change.</para>
    /// </summary>
    private async Task PruneAsync(PitrConfig config, CancellationToken cancellationToken)
    {
        var (backups, _) = await StateAsync(cancellationToken).ConfigureAwait(false);
        var stale = PointInTimeRecovery.BaseBackupsToDelete(backups, config.KeepBaseBackups);

        foreach (var backup in stale)
        {
            await RemoveAsync(BaseVolume, backup.Name, cancellationToken).ConfigureAwait(false);
        }

        var kept = backups.Except(stale).ToList();
        var segments = await ListAsync(ArchiveVolume, cancellationToken).ConfigureAwait(false);
        var droppable = PointInTimeRecovery.WalToDelete(
            [.. segments.Select(segment => (segment.Name, segment.TakenAt))], kept);

        foreach (var segment in droppable)
        {
            await RemoveAsync(ArchiveVolume, segment, cancellationToken).ConfigureAwait(false);
        }

        if (stale.Count > 0 || droppable.Count > 0)
        {
            _logger.LogWarning(
                "Pruned {Backups} base backups and {Segments} archived segments", stale.Count, droppable.Count);
        }
    }

    private async Task RemoveAsync(string volume, string name, CancellationToken cancellationToken)
    {
        // The name comes from the volume's own listing rather than from a caller, and
        // it is bound positionally so the shell never parses it.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);

        await _processRunner.RunAsync(
            "docker",
            ["run", "--rm", "-v", $"{volume}:/v", "alpine", "sh", "-c", "rm -rf -- \"/v/$1\"", "sh", name],
            null,
            timeout.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether recovery to <paramref name="target"/> is possible, and the settings it
    /// would use.
    ///
    /// <para>Returned rather than applied: a recovery is not something to start from
    /// a form submission that could have been a mis-click, and the settings are what
    /// the page shows before anyone commits.</para>
    /// </summary>
    public async Task<(RecoveryVerdict Verdict, BaseBackup? From, IReadOnlyList<string> Settings)> PlanAsync(
        DateTimeOffset target, CancellationToken cancellationToken = default)
    {
        var (backups, _) = await StateAsync(cancellationToken).ConfigureAwait(false);
        var verdict = PointInTimeRecovery.Check(backups, target, DateTimeOffset.UtcNow);

        return verdict.Possible
            ? (verdict, PointInTimeRecovery.StartingPointFor(backups, target), PointInTimeRecovery.RecoverySettings(target))
            : (verdict, null, []);
    }
}

/// <summary>Reads and writes the PITR settings. Corrupt means "off", never a crash.</summary>
public sealed class PitrConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly Lock _gate = new();

    public PitrConfigStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public string Path_ => _path;

    public PitrConfig Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<PitrConfig>(SecureFile.ReadAllText(_path), SerializerOptions)
                    ?? new PitrConfig();
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
        }

        return new PitrConfig();
    }

    public void Save(PitrConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        config.KeepBaseBackups = Math.Clamp(config.KeepBaseBackups, 1, 60);
        lock (_gate)
        {
            SecureFile.WriteAllText(_path, JsonSerializer.Serialize(config, SerializerOptions));
        }
    }
}
