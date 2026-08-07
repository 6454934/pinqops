using System.Text.Json;
using PinqOps.ObjectStorage;
using PinqOps.Secrets;

namespace PinqOps.Web;

/// <summary>
/// Where offsite copies go. The secret key is not here — only the name of the vault
/// entry holding it, for the same reason a registry's password is not in its entry:
/// this file is read to render a form.
/// </summary>
public sealed class OffsiteConfig
{
    private string _endpoint = string.Empty;
    private string _region = "auto";
    private string _bucket = string.Empty;
    private string _accessKeyId = string.Empty;
    private string _secretName = string.Empty;
    private string _prefix = string.Empty;

    /// <summary>Off is every existing install, and off changes nothing.</summary>
    public bool Enabled { get; set; }

    public string Endpoint { get => _endpoint; set => _endpoint = value ?? string.Empty; }

    /// <summary>
    /// The signing region. <c>auto</c> is what R2 wants; AWS wants the bucket's own.
    /// It is part of the signature, so a wrong one fails as an unhelpful 403 — which
    /// is why the failure message names it.
    /// </summary>
    public string Region { get => _region; set => _region = value ?? "auto"; }

    public string Bucket { get => _bucket; set => _bucket = value ?? string.Empty; }

    public string AccessKeyId { get => _accessKeyId; set => _accessKeyId = value ?? string.Empty; }

    /// <summary>The vault entry holding the secret access key.</summary>
    public string SecretName { get => _secretName; set => _secretName = value ?? string.Empty; }

    /// <summary>
    /// A path in front of every key, so one bucket can hold several servers'
    /// backups without them being each other's retention problem.
    /// </summary>
    public string Prefix { get => _prefix; set => _prefix = value ?? string.Empty; }

    /// <summary>
    /// How many copies of each target to keep in the bucket. Separate from the local
    /// count on purpose: the reason to keep a copy offsite is that the server it
    /// came from might not be there, so the two numbers are answering different
    /// questions.
    /// </summary>
    public int RetentionCount { get; set; } = 14;

    public const int MaximumRetentionCount = 365;
}

/// <summary>
/// Copies each finished backup to an S3-compatible bucket, and prunes what is there
/// to the offsite retention count.
///
/// <para><b>Uploading is not what makes a backup a backup, but it is what makes one
/// survive the server.</b> A snapshot on the same disk as the database it came from
/// covers a bad migration and nothing else.</para>
///
/// <para><b>An upload never fails a backup.</b> The local snapshot has already been
/// written and is the copy that matters most; reporting the whole run as failed
/// because a bucket was unreachable would hide a backup that did work, and would
/// send an alert about the wrong thing.</para>
/// </summary>
public sealed class OffsiteBackupService
{
    private readonly OffsiteConfigStore _store;
    private readonly SecretStore _secrets;
    private readonly S3Client _s3;
    private readonly ILogger<OffsiteBackupService> _logger;

    public OffsiteBackupService(
        OffsiteConfigStore store,
        SecretStore secrets,
        S3Client s3,
        ILogger<OffsiteBackupService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(s3);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _secrets = secrets;
        _s3 = s3;
        _logger = logger;
    }

    public OffsiteConfigStore Store => _store;

    /// <summary>The key one target's snapshot is stored under.</summary>
    public static string KeyFor(string targetId, string fileName) => $"{targetId}/{fileName}";

    /// <summary>
    /// Resolves the settings, or says what is missing. Null when offsite copies are
    /// off, which is not a problem to report.
    /// </summary>
    public (S3Settings? Settings, string? Problem) Resolve()
    {
        var config = _store.Load();
        if (!config.Enabled)
        {
            return (null, null);
        }

        if (config.Endpoint.Length == 0 || config.Bucket.Length == 0 || config.AccessKeyId.Length == 0)
        {
            return (null, "Offsite copies are on but the endpoint, bucket or access key is missing.");
        }

        if (!Uri.TryCreate(config.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
        {
            return (null, $"'{config.Endpoint}' is not a storage endpoint URL.");
        }

        string secret;
        try
        {
            secret = _secrets.Reveal(SecretScopes.Global, config.SecretName, version: null).Value;
        }
        catch (Exception exception) when (exception is KeyNotFoundException or ArgumentException)
        {
            // ArgumentException as well: a name outside letters, digits and
            // underscores is one the vault refuses rather than one it has not got,
            // and this method's contract is to hand back a problem. Letting that
            // escape took the whole scheduled backup run with it.
            return (null, $"The vault has no entry called '{config.SecretName}'.");
        }

        return (
            new S3Settings(config.Endpoint, config.Region, config.Bucket, config.AccessKeyId, secret, config.Prefix),
            null);
    }

    /// <summary>
    /// Uploads one snapshot and prunes the target's offsite copies. Returns null
    /// when there is nothing to do or it worked; otherwise what went wrong.
    /// </summary>
    public async Task<string?> UploadAsync(
        string targetId, string snapshotPath, CancellationToken cancellationToken = default)
    {
        var (settings, problem) = Resolve();
        if (settings is null)
        {
            return problem;
        }

        if (!File.Exists(snapshotPath))
        {
            return $"{snapshotPath} is not there to upload.";
        }

        var key = KeyFor(targetId, Path.GetFileName(snapshotPath));

        await using (var file = File.OpenRead(snapshotPath))
        {
            var result = await _s3.PutAsync(settings, key, file, cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
            {
                return result.Error;
            }
        }

        _logger.LogWarning("Backup {Key} copied offsite", key);
        return await PruneAsync(targetId, settings, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the target's oldest offsite copies past the retention count.
    ///
    /// <para>Scoped to the one target, by listing and filtering rather than by
    /// asking for a per-target prefix: the retention count is per target, and a
    /// sweep that saw every target's objects at once would keep fourteen backups in
    /// total rather than fourteen of each.</para>
    /// </summary>
    private async Task<string?> PruneAsync(
        string targetId, S3Settings settings, CancellationToken cancellationToken)
    {
        var keep = Math.Clamp(_store.Load().RetentionCount, 1, OffsiteConfig.MaximumRetentionCount);
        var (objects, error) = await _s3.ListAsync(settings, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var prefix = S3Client.FullKey(settings, targetId + "/");
        var mine = objects
            .Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();

        // Oldest first from the listing, so the ones to drop are at the front.
        foreach (var stale in mine.Take(Math.Max(0, mine.Count - keep)))
        {
            // The key comes back with the prefix already on it; Delete adds one, so
            // what is passed back is the part after it.
            var withoutPrefix = settings.Prefix.Trim('/').Length == 0
                ? stale.Key
                : stale.Key[(settings.Prefix.Trim('/').Length + 1)..];

            var deleted = await _s3.DeleteAsync(settings, withoutPrefix, cancellationToken).ConfigureAwait(false);
            if (!deleted.Ok)
            {
                // Reported, not fatal: the upload succeeded, and a bucket that keeps
                // one snapshot too many is not an emergency.
                _logger.LogWarning("Could not remove the offsite copy {Key}: {Detail}", stale.Key, deleted.Error);
            }
        }

        return null;
    }

    /// <summary>Every offsite copy of one target, newest first.</summary>
    public async Task<(IReadOnlyList<S3Object> Objects, string? Error)> ListAsync(
        string targetId, CancellationToken cancellationToken = default)
    {
        var (settings, problem) = Resolve();
        if (settings is null)
        {
            return ([], problem);
        }

        var (objects, error) = await _s3.ListAsync(settings, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return ([], error);
        }

        var prefix = S3Client.FullKey(settings, targetId + "/");
        return (
            [.. objects.Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal))
                .OrderByDescending(entry => entry.LastModified)],
            null);
    }

    /// <summary>
    /// Fetches one offsite copy back to <paramref name="destinationPath"/> — which
    /// is what makes this a backup rather than an archive.
    /// </summary>
    public async Task<string?> DownloadAsync(
        string targetId, string fileName, string destinationPath, CancellationToken cancellationToken = default)
    {
        var (settings, problem) = Resolve();
        if (settings is null)
        {
            return problem ?? "Offsite copies are off.";
        }

        // Written to a partial name and renamed only once the download finished, so
        // a connection that drops midway cannot leave a truncated file that the
        // snapshot list presents as an ordinary backup.
        var partial = destinationPath + ".part";
        try
        {
            await using (var file = File.Create(partial))
            {
                var result = await _s3
                    .GetAsync(settings, KeyFor(targetId, fileName), file, cancellationToken)
                    .ConfigureAwait(false);
                if (!result.Ok)
                {
                    return result.Error;
                }
            }

            File.Move(partial, destinationPath, overwrite: true);
            return null;
        }
        finally
        {
            if (File.Exists(partial))
            {
                try
                {
                    File.Delete(partial);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }
}

/// <summary>Reads and writes the offsite settings. Corrupt means "off", never a crash.</summary>
public sealed class OffsiteConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly Lock _gate = new();

    public OffsiteConfigStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public string Path_ => _path;

    public OffsiteConfig Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<OffsiteConfig>(SecureFile.ReadAllText(_path), SerializerOptions)
                    ?? new OffsiteConfig();
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
        }

        return new OffsiteConfig();
    }

    public void Save(OffsiteConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        lock (_gate)
        {
            SecureFile.WriteAllText(_path, JsonSerializer.Serialize(config, SerializerOptions));
        }
    }
}
