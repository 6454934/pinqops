using System.Security.Cryptography;
using System.Text.Json;

namespace PinqOps;

/// <summary>
/// Persists deploy history as JSON in the <c>.pinqops</c> state directory next
/// to the compose file (newest first, capped). A corrupt file is treated as
/// empty history rather than failing a deploy.
/// </summary>
public sealed class DeployHistoryStore
{
    public const int MaxEntries = 100;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// One gate per file, shared by every instance addressing it — the same shape
    /// <see cref="PinqOps.Deploy.DeploySettingsStore"/> uses, for the same reason:
    /// every caller news up its own store, so an instance field would serialise
    /// nothing. Append is a read-modify-write, and two of them racing (a deploy
    /// finishing while a rollback is recorded) silently dropped whichever record
    /// saved first.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lock> Gates =
        new(StringComparer.Ordinal);

    private readonly string _path;

    public DeployHistoryStore(string composeFilePath)
    {
        _path = PinqOpsStatePaths.HistoryFile(composeFilePath);
    }

    private Lock Gate => Gates.GetOrAdd(_path, _ => new Lock());

    public string Path_ => _path;

    /// <summary>Returns all records, newest first.</summary>
    public IReadOnlyList<DeployRecord> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var document = JsonSerializer.Deserialize<HistoryDocument>(SecureFile.ReadAllText(_path), SerializerOptions);
                return document?.Deployments ?? new List<DeployRecord>();
            }
        }
        catch (JsonException)
        {
            // A corrupt history file must not brick deploys; start fresh.
        }

        return Array.Empty<DeployRecord>();
    }

    /// <summary>Prepends a record and persists, trimming to <see cref="MaxEntries"/>.</summary>
    public void Append(DeployRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (Gate)
        {
            var records = new List<DeployRecord> { record };
            records.AddRange(Load());
            if (records.Count > MaxEntries)
            {
                records.RemoveRange(MaxEntries, records.Count - MaxEntries);
            }

            Save(records);
        }
    }

    /// <summary>
    /// The tag that was running before <paramref name="currentTag"/> — the default
    /// rollback target.
    ///
    /// A rollback is itself a successful deployment of the tag it restored, so its
    /// record counts here even though it is stored as
    /// <see cref="DeployRecordValues.ResultRolledBack"/>. Skipping those made this
    /// walk return the tag the last rollback had just escaped from, so a second
    /// consecutive <c>pinqops rollback</c> rolled <em>forward</em> onto the bad
    /// release instead of one step further back.
    ///
    /// Tags the current release was reached by rolling <em>away from</em> are also
    /// excluded, transitively: after rolling sha-3 → sha-2, the newest other tag in
    /// history is still sha-3, so "newest tag that is not the current one" would send
    /// the next rollback straight back onto the release the operator just escaped.
    /// Each rollback record names what it came from in
    /// <see cref="DeployRecord.PreviousTag"/>, which is what makes the chain
    /// followable. A fresh deploy ends the chain — after it, rolling back one step
    /// is meant to reach the tag that deploy replaced.
    /// </summary>
    public string? LastSuccessfulTagBefore(string? currentTag)
    {
        var records = Load();

        static bool Ran(DeployRecord record) =>
            record.Result is DeployRecordValues.ResultSucceeded or DeployRecordValues.ResultRolledBack
            && !string.IsNullOrEmpty(record.Tag);

        // Follow the rollback chain back from the current tag, collecting what it
        // escaped. The loop is bounded by `visited`, so a hand-edited history that
        // points at itself cannot spin.
        var escaped = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var link = currentTag;
        while (link is not null && visited.Add(link))
        {
            var newest = records.FirstOrDefault(record => Ran(record) && record.Tag == link);
            if (newest is null
                || newest.Result != DeployRecordValues.ResultRolledBack
                || string.IsNullOrEmpty(newest.PreviousTag))
            {
                break;
            }

            escaped.Add(newest.PreviousTag);
            link = newest.PreviousTag;
        }

        foreach (var record in records)
        {
            if (Ran(record)
                && !string.Equals(record.Tag, currentTag, StringComparison.Ordinal)
                && !escaped.Contains(record.Tag))
            {
                return record.Tag;
            }
        }

        return null;
    }

    /// <summary>The most recent successful record, when any.</summary>
    public DeployRecord? LastSuccessful() =>
        Load().FirstOrDefault(record => record.Result == DeployRecordValues.ResultSucceeded);

    public static string NewRecordId() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    private void Save(List<DeployRecord> records)
    {
        // Atomic write so a crash mid-save cannot truncate the history file.
        SecureFile.WriteAllText(
            _path,
            JsonSerializer.Serialize(new HistoryDocument { Deployments = records }, SerializerOptions));
    }

    private sealed class HistoryDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public List<DeployRecord> Deployments { get; set; } = new();
    }
}
