using System.Text.Json;
using System.Text.RegularExpressions;

namespace PinqOps.Backups;

/// <summary>
/// Scheduled backup targets. Server-global (one schedule per server), stored
/// next to <c>ui.json</c>, 0600.
/// </summary>
public sealed class BackupConfig
{
    public List<BackupTarget> Targets { get; set; } = [];
}

/// <summary>One thing to back up on a schedule: a database container or a volume.</summary>
public sealed class BackupTarget
{
    public string Id { get; set; } = string.Empty;

    /// <summary><c>db</c> or <c>volume</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The container name (db) or the docker volume name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>postgres | mysql | mariadb | mongo | redis | volume.</summary>
    public string Engine { get; set; } = string.Empty;

    /// <summary>hourly | daily | weekly.</summary>
    public string Schedule { get; set; } = "daily";

    /// <summary>UTC hour (0-23) a daily/weekly backup runs at.</summary>
    public int AtHour { get; set; } = 3;

    /// <summary>How many snapshots to keep; older ones are pruned.</summary>
    public int RetentionCount { get; set; } = 7;

    public bool Enabled { get; set; } = true;
}

/// <summary>Whether a target is due to run now (pure, tick-driven).</summary>
public static class BackupSchedule
{
    /// <summary>
    /// How far past its period an established daily target may drift before it runs
    /// at whatever hour the next tick happens to be. A whole hour of slack past the
    /// period, so an ordinary run that lands a few minutes late never trips it.
    /// </summary>
    private static readonly TimeSpan DailyOverdue = TimeSpan.FromHours(25);

    /// <inheritdoc cref="DailyOverdue"/>
    private static readonly TimeSpan WeeklyOverdue = TimeSpan.FromDays(8);

    public static bool IsDue(BackupTarget target, DateTimeOffset now, DateTimeOffset? lastRun) => target.Schedule switch
    {
        // Just under an hour so a per-minute tick fires once per hour without drift.
        "hourly" => lastRun is null || now - lastRun >= TimeSpan.FromMinutes(59),
        "weekly" => IsDueAtWindow(
            now.DayOfWeek == DayOfWeek.Monday && now.Hour == target.AtHour,
            now, lastRun, TimeSpan.FromDays(6), WeeklyOverdue),
        // daily (default): the >=23h guard keeps the AtHour window from double-firing.
        _ => IsDueAtWindow(
            now.Hour == target.AtHour, now, lastRun, TimeSpan.FromHours(23), DailyOverdue),
    };

    /// <summary>
    /// A window-based schedule fires inside its window, or — once it has run at
    /// least once — as soon as it is clearly overdue.
    ///
    /// The catch-up is the point. Without it the whole schedule was one hour a day
    /// (one hour a week, for weekly): a host that is off overnight, a dashboard
    /// restarted across 03:00, or a tick that overran the window, silently skipped
    /// the backup until the same hour came round again — and the only symptom is a
    /// backup that never ran, which is exactly what nobody notices until they need
    /// the snapshot.
    ///
    /// A target that has never run keeps waiting for its window: it was just added,
    /// nothing is overdue yet, and starting an unexpected dump of a live database
    /// the moment someone saves the form is not what the hour field promised.
    /// </summary>
    private static bool IsDueAtWindow(
        bool inWindow, DateTimeOffset now, DateTimeOffset? lastRun, TimeSpan minimumGap, TimeSpan overdueAfter)
    {
        if (lastRun is not { } previous)
        {
            return inWindow;
        }

        var since = now - previous;
        // A clock stepped backwards must not make an already-run target look due
        // again; a negative gap satisfies neither branch.
        return inWindow ? since >= minimumGap : since >= overdueAfter;
    }
}

/// <summary>Snapshot file naming and validation (path-traversal safe).</summary>
public static class BackupNaming
{
    /// <summary>
    /// A docker volume rather than a database. It is archived wholesale instead of
    /// dumped by a database client, so it takes a different path through backup,
    /// restore and naming — and it has no dump plan, which is not a gap to be
    /// validated against but the point.
    ///
    /// <para>Named here because the page sends it as the kind <em>and</em> as the
    /// engine, so every check has to accept either. Written out at each site, one
    /// of them asked for a dump plan anyway and no volume target could be
    /// created at all.</para>
    /// </summary>
    public const string VolumeKind = "volume";

    /// <summary>Whether a target with this kind and engine is a volume.</summary>
    public static bool IsVolume(string? kind, string? engine) =>
        string.Equals(kind, VolumeKind, StringComparison.Ordinal)
        || string.Equals(engine, VolumeKind, StringComparison.Ordinal);

    private static readonly Regex SnapshotPattern = new(@"^\d{8}-\d{6}\.(sql|archive|rdb|tgz)$", RegexOptions.Compiled);
    private static readonly Regex IdPattern = new(@"^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.Compiled);

    public static string Extension(string engine) => engine switch
    {
        "postgres" or "mysql" or "mariadb" => "sql",
        "mongo" => "archive",
        "redis" => "rdb",
        _ => "tgz",
    };

    public static string FileName(string engine, DateTimeOffset timestamp) =>
        $"{timestamp.UtcDateTime:yyyyMMdd-HHmmss}.{Extension(engine)}";

    /// <summary>A snapshot filename that is safe to join to a path.</summary>
    public static bool IsValidSnapshot(string name) => SnapshotPattern.IsMatch(name);

    /// <summary>A target id that is safe to use as a directory name.</summary>
    public static bool IsValidId(string id) => IdPattern.IsMatch(id);
}

/// <summary>Loads and saves <see cref="BackupConfig"/> (camelCase JSON, 0600).</summary>
public sealed class BackupConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly Lock _gate = new();

    public BackupConfigStore(string path) => _path = path;

    public string Path_ => _path;

    /// <summary>
    /// Load, mutate and save under one lock, returning whatever the callback
    /// returns.
    ///
    /// Three endpoints do a full read-modify-write on this file (create/update,
    /// toggle, delete). Two that both loaded before either saved produced a lost
    /// update — the second write persisting a config that had never seen the first
    /// change, so a target someone just added quietly vanished. This is the same
    /// guard AlertRuleStore.Update exists for.
    /// </summary>
    public T Update<T>(Func<BackupConfig, T> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_gate)
        {
            var config = Load();
            var result = mutate(config);
            Save(config);
            return result;
        }
    }

    public BackupConfig Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<BackupConfig>(SecureFile.ReadAllText(_path), SerializerOptions)
                    ?? new BackupConfig();
            }
        }
        catch (JsonException)
        {
            // A corrupt config means "no scheduled backups", never a crash.
        }

        return new BackupConfig();
    }

    public void Save(BackupConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // Atomic + owner-only (0600 from the first byte): the previous
        // File.WriteAllText left the mode unfixed on every save after the first,
        // and a torn write would be read back as corrupt and silently reset the
        // schedule.
        SecureFile.WriteAllText(_path, JsonSerializer.Serialize(config, SerializerOptions));
    }
}
