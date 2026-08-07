using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PinqOps.Web;

/// <summary>One recorded action: who did what, to which target, and whether it worked.</summary>
public sealed record AuditEntry(
    [property: JsonPropertyName("ts")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("user")] string User,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("result")] string Result,
    [property: JsonPropertyName("status")] int Status)
{
    /// <summary>
    /// The client the request came from. Carried so a burst of denials can be
    /// attributed to one source; empty for entries written before it existed.
    /// </summary>
    [JsonPropertyName("client")]
    public string Client { get; init; } = string.Empty;

    /// <summary>
    /// The Docker host the request acted on. Without it a line reading
    /// "POST /api/docker/containers/db/action" is ambiguous the moment more than
    /// one environment exists — and which host something was stopped on is
    /// usually the first question asked of an audit trail.
    /// </summary>
    [JsonPropertyName("env")]
    public string Environment { get; init; } = string.Empty;

    /// <summary>
    /// Hex SHA-256 binding this entry to the one before it. Set by
    /// <see cref="AuditLog.Append"/>; never supplied by a caller.
    /// </summary>
    [JsonPropertyName("hash")]
    public string Hash { get; init; } = string.Empty;
}

/// <summary>The outcome of re-walking the trail's hash chain.</summary>
/// <param name="Ok">True when every linked entry matches its recomputed hash.</param>
/// <param name="Entries">How many entries were read.</param>
/// <param name="Verified">How many links were checkable (the first entry has no predecessor on file).</param>
/// <param name="FirstBrokenIndex">Index of the first entry whose hash did not match, or -1.</param>
public sealed record AuditVerification(bool Ok, int Entries, int Verified, int FirstBrokenIndex);

/// <summary>One page of the audit trail, plus how many entries the filter matched.</summary>
/// <param name="Items">The entries on this page, newest first.</param>
/// <param name="Total">Every entry matching the filter, not just this page.</param>
public sealed record AuditPage(IReadOnlyList<AuditEntry> Items, int Total);

/// <summary>
/// An append-only audit trail of every action worth recording, stored as JSONL so
/// it is cheap to append and easy to tail. The file is rotated once past
/// <see cref="MaxBytes"/>, keeping <see cref="Generations"/> previous ones, which
/// bounds disk use without a background job.
///
/// Each entry carries a SHA-256 that covers the entry and the previous entry's
/// hash. Editing, removing or inserting a line therefore breaks the chain from
/// that point on, which <see cref="Verify"/> reports. This detects tampering with
/// the file — it is not proof against a compromised pinqops process, which holds
/// the whole chain and could rewrite it end to end. Shipping entries off the host
/// is the only defence against that.
/// </summary>
public sealed class AuditLog
{
    private const long MaxBytes = 10 * 1024 * 1024;

    /// <summary>Previous generations kept alongside the live file (<c>.1</c>…<c>.N</c>).</summary>
    public const int Generations = 5;

    /// <summary>Recorded actor for a request that never authenticated.</summary>
    public const string Anonymous = "anonymous";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private string? _lastHash;

    public AuditLog(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public string Path_ => _path;

    /// <summary>Appends one entry. A logging failure must never break the request, so it is swallowed.</summary>
    public void Append(AuditEntry entry)
    {
        try
        {
            lock (_gate)
            {
                var directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // The chain's predecessor is read BEFORE rotating. Rotation moves the
                // live file to <path>.1 and leaves an empty one behind, so a process
                // whose first append triggers a rotation would have read the tail of
                // the new empty file — chaining from "" while the real predecessor sat
                // in <path>.1, which Verify() still reads. That is a break the
                // dashboard reports as tampering.
                _lastHash ??= ReadLastHash();

                RotateIfNeeded();
                EnsureOwnerOnlyFile();

                var chained = entry with { Hash = ChainHash(_lastHash, entry) };
                File.AppendAllText(_path, JsonSerializer.Serialize(chained, SerializerOptions) + "\n");
                _lastHash = chained.Hash;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The audit trail is best-effort; losing a line is preferable to
            // failing the action it was recording.
        }
    }

    /// <summary>
    /// The most recent entries (newest first), optionally filtered by user or by
    /// a substring of the action. Reads the live file and the rotated generations
    /// so a rotation does not hide recent history.
    /// </summary>
    public IReadOnlyList<AuditEntry> Read(int limit = 200, string? user = null, string? action = null) =>
        ReadPage(limit, 0, user, action).Items;

    /// <summary>
    /// One page of the trail, newest first, with the total the filter matched so
    /// the caller can say "page 3 of 40" rather than just handing over a window.
    ///
    /// The trail is the one view that only grows, and on a server that has been
    /// up for months it grows past anything worth rendering at once. Paging is
    /// applied after filtering, so a page number means the same thing whether or
    /// not a user or action filter is set.
    /// </summary>
    public AuditPage ReadPage(int limit = 200, int offset = 0, string? user = null, string? action = null)
    {
        IEnumerable<AuditEntry> query = ReadAll();
        if (!string.IsNullOrWhiteSpace(user))
        {
            query = query.Where(entry => string.Equals(entry.User, user, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(entry => entry.Action.Contains(action, StringComparison.OrdinalIgnoreCase));
        }

        var matched = query.OrderByDescending(entry => entry.Timestamp).ToList();
        var items = matched
            .Skip(Math.Max(offset, 0))
            .Take(Math.Clamp(limit, 1, 2000))
            .ToList();

        return new AuditPage(items, matched.Count);
    }

    /// <summary>
    /// Re-walks the chain oldest-first and reports the first entry whose hash does
    /// not follow from its predecessor. The oldest entry on file has no
    /// predecessor to check against — rotation legitimately drops what came
    /// before it — so it is counted but not verified.
    /// </summary>
    public AuditVerification Verify()
    {
        var entries = ReadAll(oldestFirst: true);
        for (var index = 1; index < entries.Count; index++)
        {
            if (!string.Equals(entries[index].Hash, ChainHash(entries[index - 1].Hash, entries[index]), StringComparison.Ordinal))
            {
                return new AuditVerification(false, entries.Count, index - 1, index);
            }
        }

        return new AuditVerification(true, entries.Count, Math.Max(entries.Count - 1, 0), -1);
    }

    /// <summary>Every entry on file, oldest generation first.</summary>
    private List<AuditEntry> ReadAll(bool oldestFirst = false)
    {
        var entries = new List<AuditEntry>();
        lock (_gate)
        {
            foreach (var file in Files(oldestFirst))
            {
                if (!File.Exists(file))
                {
                    continue;
                }

                foreach (var line in File.ReadAllLines(file))
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    try
                    {
                        if (JsonSerializer.Deserialize<AuditEntry>(line, SerializerOptions) is { } entry)
                        {
                            entries.Add(entry);
                        }
                    }
                    catch (JsonException)
                    {
                        // Skip a corrupt line rather than dropping the whole trail.
                    }
                }
            }
        }

        return entries;
    }

    /// <summary>The live file and its generations, newest first unless asked otherwise.</summary>
    private IEnumerable<string> Files(bool oldestFirst)
    {
        var files = new List<string> { _path };
        for (var generation = 1; generation <= Generations; generation++)
        {
            files.Add($"{_path}.{generation}");
        }

        if (oldestFirst)
        {
            files.Reverse();
        }

        return files;
    }

    /// <summary>The hash of the newest entry already on file, or empty for a fresh trail.</summary>
    private string ReadLastHash()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return string.Empty;
            }

            foreach (var line in File.ReadLines(_path).Reverse())
            {
                if (line.Length == 0)
                {
                    continue;
                }

                return JsonSerializer.Deserialize<AuditEntry>(line, SerializerOptions)?.Hash ?? string.Empty;
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            // An unreadable tail just restarts the chain; Verify reports the break.
        }

        return string.Empty;
    }

    /// <summary>
    /// SHA-256 over the previous hash and this entry, serialized with an empty
    /// hash field so the value is defined by the entry's content alone.
    /// </summary>
    private static string ChainHash(string previousHash, AuditEntry entry)
    {
        var payload = JsonSerializer.Serialize(entry with { Hash = string.Empty }, SerializerOptions);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(previousHash + payload)));
    }

    /// <summary>
    /// Creates the trail 0600 before anything is written to it. The log names who
    /// did what and to which resource, so it must not land on a world-readable
    /// inode — the same rule <see cref="SecureFile"/> applies to the other stores.
    /// </summary>
    private void EnsureOwnerOnlyFile()
    {
        if (File.Exists(_path))
        {
            return;
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using var stream = new FileStream(_path, options);
        }
        catch (IOException)
        {
            // Another writer won the race and created it; appending is still fine.
        }
    }

    private void RotateIfNeeded()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < MaxBytes)
        {
            return;
        }

        // Shift the generations along, dropping the oldest.
        for (var generation = Generations - 1; generation >= 1; generation--)
        {
            var source = $"{_path}.{generation}";
            if (File.Exists(source))
            {
                File.Move(source, $"{_path}.{generation + 1}", overwrite: true);
            }
        }

        File.Move(_path, $"{_path}.1", overwrite: true);
    }
}
