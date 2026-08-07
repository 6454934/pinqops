using System.Text.Json;

namespace PinqOps.Secrets;

/// <summary>
/// Where a secret applies. Either every app on this server, or one app by id.
/// </summary>
public static class SecretScopes
{
    /// <summary>Applies to every connected app.</summary>
    public const string Global = "global";

    /// <summary>
    /// Long enough for any app id, which is what a non-global scope always is.
    ///
    /// <para>An app id is <c>&lt;owner&gt;-&lt;repo&gt;</c> put through
    /// <see cref="ComposeProjectName"/>, and that constrains the characters
    /// without constraining the length — so "already a compose project name" said
    /// nothing about how long one can be. GitHub allows 39 characters of owner and
    /// 100 of repository, making 140 an ordinary id rather than a contrived one,
    /// and the id may carry a uniqueness suffix on top. A ceiling below that does
    /// not reject a bad name; it quietly makes one real app unable to hold a
    /// secret at all, under a name its operator never chose.</para>
    ///
    /// <para>Raised rather than capping the id, because the id also seeds the
    /// app's compose and runner paths — shortening it would rename directories for
    /// apps that already exist, and would not help the ones already registered
    /// with a long one. A scope reaches a URL only as a path segment, where 160 is
    /// nowhere near any limit.</para>
    /// </summary>
    public const int MaximumLength = 160;

    /// <summary>
    /// Rejects the shapes that would break the storage key or a route segment. The
    /// character rule is the load-bearing half; see <see cref="MaximumLength"/> for
    /// why the length is what it is.
    /// </summary>
    public static bool IsValid(string? scope) =>
        !string.IsNullOrWhiteSpace(scope)
        && scope.Length <= MaximumLength
        && scope.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    public static string Normalize(string? scope)
    {
        var value = (scope ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length == 0)
        {
            value = Global;
        }

        if (!IsValid(value))
        {
            throw new ArgumentException($"'{scope}' is not a valid secret scope.");
        }

        return value;
    }
}

/// <summary>
/// Validation for a secret's name. A secret is materialised into an app's
/// <c>.env</c>, so the name has to be a name <see cref="EnvFileStore"/> accepts —
/// validated here rather than at materialisation time, because a name that only
/// fails on the way to disk would be stored, listed and revealed while never
/// actually reaching any container.
/// </summary>
public static class SecretName
{
    /// <summary>
    /// Variables pinqops writes itself. <c>PINQOPS_TAG</c> and
    /// <c>PINQOPS_IMAGE</c> are pinned by every deploy and
    /// <c>PINQOPS_HOST_PORT</c>/<c>PINQOPS_CONTAINER_PORT</c> by the compose
    /// editor, so a secret sharing one of those names would be silently
    /// overwritten on the next deploy — or worse, would overwrite the pinned
    /// image and point the app at something else. The whole prefix is reserved
    /// rather than the four current names, so adding a fifth cannot re-open this.
    /// </summary>
    public const string ReservedPrefix = "PINQOPS_";

    public const int MaximumLength = 128;

    public static bool IsValid(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= MaximumLength
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
        && !char.IsAsciiDigit(name[0]);

    public static string Normalize(string? name)
    {
        var value = (name ?? string.Empty).Trim();
        if (!IsValid(value))
        {
            throw new ArgumentException(
                $"'{name}' is not a valid secret name — use letters, digits and underscores, and do not start with a digit.");
        }

        if (value.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Secret names starting with '{ReservedPrefix}' are reserved for pinqops itself.");
        }

        return value;
    }
}

/// <summary>One stored value of a secret. Versions are never reused or renumbered.</summary>
public sealed class SecretVersion
{
    public int Version { get; set; }

    /// <summary>Encrypted on disk; plaintext in memory. See <see cref="SecretStore"/>.</summary>
    public string Value { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The principal that wrote this version, for the audit trail.</summary>
    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary>A named secret and every version of it pinqops still holds.</summary>
public sealed class ManagedSecret
{
    public string Scope { get; set; } = SecretScopes.Global;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// The version that is materialised into <c>.env</c>. Normally the newest, but
    /// a rollback points it at an older one without discarding what came after —
    /// so rolling forward again is the same operation in the other direction.
    /// </summary>
    public int CurrentVersion { get; set; }

    public List<SecretVersion> Versions { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;
}

/// <summary>The file on disk.</summary>
public sealed class SecretFile
{
    public List<ManagedSecret> Secrets { get; set; } = [];
}

/// <summary>A secret without any of its values — what a listing may return.</summary>
public sealed record SecretSummary(
    string Scope,
    string Name,
    string Description,
    int CurrentVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string UpdatedBy,
    IReadOnlyList<SecretVersionSummary> Versions);

/// <summary>A version without its value.</summary>
public sealed record SecretVersionSummary(int Version, DateTimeOffset CreatedAt, string CreatedBy, bool Current);

/// <summary>
/// The named, versioned secrets an operator manages by hand, kept encrypted at
/// rest and materialised into each app's <c>.env</c> so the runner needs nothing
/// new to deploy them (see <see cref="SecretMaterializer"/>).
///
/// <para>This is the general form of <c>AppCredentialStore</c>, which holds the
/// passwords pinqops <em>generates</em> for catalog apps. The two are deliberately
/// separate: a generated catalog password is derived state that must survive a
/// reinstall to match an existing volume, while these are values the operator
/// owns, rotates and rolls back.</para>
///
/// <para><b>Encryption.</b> Every version's value goes through
/// <see cref="SecretBox"/> on the way out and comes back on the way in, on a copy,
/// so callers always see plaintext and a second save cannot double-encrypt. The
/// key sits beside the file, so this is not a defence against someone who can read
/// the directory as the dashboard user — it is what stops a copied-away
/// <c>secrets.json</c> from being readable. Values written before encryption pass
/// through unchanged and re-encrypt on the next write.</para>
/// </summary>
public sealed class SecretStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// How many versions of one secret are kept. Rotation is meant to be cheap
    /// enough to do often, so the history has to be bounded or the file grows
    /// without limit; ten is deep enough that a rollback past the last few
    /// rotations is still possible.
    /// </summary>
    public const int MaximumVersions = 10;

    /// <summary>An operator-supplied value has to be a value <c>.env</c> can hold.</summary>
    public const int MaximumValueLength = 8192;

    private readonly string _path;
    private readonly SecretBox _secrets;
    private readonly Lock _gate = new();

    public SecretStore(string path, SecretBox? secrets = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _secrets = secrets ?? SecretBox.ForDirectory(Path.GetDirectoryName(path) ?? ".");
    }

    public string Path_ => _path;

    /// <summary>
    /// Load, mutate and save under one lock, returning whatever the callback
    /// returns. Every write route reads-modifies-writes this file; two that both
    /// loaded before either saved would lose one of the changes — a rotation
    /// disappearing, or a deleted secret coming back. The write itself is already
    /// atomic; this closes the in-process window between the read and it.
    /// </summary>
    public T Update<T>(Func<SecretFile, T> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_gate)
        {
            var file = Load();
            var result = mutate(file);
            Save(file);
            return result;
        }
    }

    /// <summary>Every secret, values stripped.</summary>
    public IReadOnlyList<SecretSummary> List()
    {
        lock (_gate)
        {
            return [.. Load().Secrets
                .OrderBy(secret => secret.Scope, StringComparer.Ordinal)
                .ThenBy(secret => secret.Name, StringComparer.Ordinal)
                .Select(Summarize)];
        }
    }

    /// <summary>
    /// Creates a secret or adds a version to an existing one, and points the
    /// current version at it. Returns the new version number.
    /// </summary>
    public int Set(string scope, string name, string value, string? description, string actor, DateTimeOffset now)
    {
        var normalizedScope = SecretScopes.Normalize(scope);
        var normalizedName = SecretName.Normalize(name);
        ValidateValue(value);

        return Update(file =>
        {
            var secret = Find(file, normalizedScope, normalizedName);
            if (secret is null)
            {
                secret = new ManagedSecret
                {
                    Scope = normalizedScope,
                    Name = normalizedName,
                    CreatedAt = now,
                };
                file.Secrets.Add(secret);
            }

            var version = new SecretVersion
            {
                Version = NextVersion(secret),
                Value = value,
                CreatedAt = now,
                CreatedBy = actor,
            };

            secret.Versions.Add(version);
            secret.CurrentVersion = version.Version;
            secret.UpdatedAt = now;
            secret.UpdatedBy = actor;
            if (description is not null)
            {
                secret.Description = description.Trim();
            }

            Trim(secret);
            return version.Version;
        });
    }

    /// <summary>
    /// Points an existing secret at one of its earlier versions. The versions
    /// after it are kept, so this is reversible.
    /// </summary>
    public void UseVersion(string scope, string name, int version, string actor, DateTimeOffset now)
    {
        var normalizedScope = SecretScopes.Normalize(scope);
        var normalizedName = SecretName.Normalize(name);

        Update<object?>(file =>
        {
            var secret = Require(file, normalizedScope, normalizedName);
            if (!secret.Versions.Exists(candidate => candidate.Version == version))
            {
                throw new KeyNotFoundException($"Version {version} of secret '{normalizedName}' does not exist.");
            }

            secret.CurrentVersion = version;
            secret.UpdatedAt = now;
            secret.UpdatedBy = actor;
            return null;
        });
    }

    /// <summary>Removes a secret and every version of it. Returns false when absent.</summary>
    public bool Remove(string scope, string name)
    {
        var normalizedScope = SecretScopes.Normalize(scope);
        var normalizedName = SecretName.Normalize(name);

        return Update(file => file.Secrets.RemoveAll(secret =>
            Matches(secret, normalizedScope, normalizedName)) > 0);
    }

    /// <summary>
    /// The plaintext of one version — the current one when
    /// <paramref name="version"/> is null. The only call that returns a value, so
    /// it is the only one an endpoint has to gate and audit.
    /// </summary>
    public (string Value, int Version) Reveal(string scope, string name, int? version)
    {
        var normalizedScope = SecretScopes.Normalize(scope);
        var normalizedName = SecretName.Normalize(name);

        lock (_gate)
        {
            var secret = Require(Load(), normalizedScope, normalizedName);
            var wanted = version ?? secret.CurrentVersion;
            var stored = secret.Versions.FirstOrDefault(candidate => candidate.Version == wanted)
                ?? throw new KeyNotFoundException($"Version {wanted} of secret '{normalizedName}' does not exist.");
            return (stored.Value, stored.Version);
        }
    }

    /// <summary>
    /// The secrets that apply to an app, as env assignments: the global ones
    /// overlaid with the app's own, so an app-scoped secret shadows a global one
    /// of the same name rather than the two fighting over the same <c>.env</c> key.
    /// </summary>
    public IReadOnlyDictionary<string, string> Resolve(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        var scope = SecretScopes.Normalize(appId);

        lock (_gate)
        {
            var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
            var all = Load().Secrets;
            foreach (var secret in all.Where(candidate =>
                string.Equals(candidate.Scope, SecretScopes.Global, StringComparison.Ordinal)))
            {
                AddCurrentValue(resolved, secret);
            }

            foreach (var secret in all.Where(candidate =>
                string.Equals(candidate.Scope, scope, StringComparison.Ordinal)))
            {
                AddCurrentValue(resolved, secret);
            }

            return resolved;
        }
    }

    /// <summary>
    /// Every name any secret uses, in any scope. This is the set
    /// <see cref="SecretMaterializer"/> is allowed to remove from an app's
    /// <c>.env</c> — a name that is a secret somewhere is a name pinqops owns, so
    /// narrowing an app-scoped secret or deleting a global one actually clears the
    /// value out of the apps that no longer have it.
    /// </summary>
    public IReadOnlyCollection<string> ManagedNames()
    {
        lock (_gate)
        {
            return new HashSet<string>(Load().Secrets.Select(secret => secret.Name), StringComparer.Ordinal);
        }
    }

    private static void AddCurrentValue(Dictionary<string, string> resolved, ManagedSecret secret)
    {
        var current = secret.Versions.FirstOrDefault(version => version.Version == secret.CurrentVersion);
        if (current is null)
        {
            // A hand-edited file can point CurrentVersion at a version that is not
            // there. Skipping leaves the app's .env untouched for that name, which
            // is the conservative reading; writing an empty value would look like a
            // configured-but-blank secret.
            return;
        }

        resolved[secret.Name] = current.Value;
    }

    private static SecretSummary Summarize(ManagedSecret secret) => new(
        secret.Scope,
        secret.Name,
        secret.Description,
        secret.CurrentVersion,
        secret.CreatedAt,
        secret.UpdatedAt,
        secret.UpdatedBy,
        [.. secret.Versions
            .OrderByDescending(version => version.Version)
            .Select(version => new SecretVersionSummary(
                version.Version, version.CreatedAt, version.CreatedBy, version.Version == secret.CurrentVersion))]);

    private static void ValidateValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            throw new ArgumentException("A secret value is required.");
        }

        if (value.Length > MaximumValueLength)
        {
            throw new ArgumentException($"A secret value may be at most {MaximumValueLength} characters.");
        }

        // The value ends up as one KEY=value line, and EnvFileStore refuses a
        // multi-line value at the write boundary. Refusing it here means a secret
        // that could never be materialised is never stored in the first place.
        if (value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal))
        {
            throw new ArgumentException("A secret value must be a single line.");
        }
    }

    private static bool Matches(ManagedSecret secret, string scope, string name) =>
        string.Equals(secret.Scope, scope, StringComparison.Ordinal)
        && string.Equals(secret.Name, name, StringComparison.Ordinal);

    private static ManagedSecret? Find(SecretFile file, string scope, string name) =>
        file.Secrets.Find(secret => Matches(secret, scope, name));

    private static ManagedSecret Require(SecretFile file, string scope, string name) =>
        Find(file, scope, name)
        ?? throw new KeyNotFoundException($"No secret named '{name}' in scope '{scope}'.");

    private static int NextVersion(ManagedSecret secret) =>
        secret.Versions.Count == 0 ? 1 : secret.Versions.Max(version => version.Version) + 1;

    /// <summary>
    /// Drops the oldest versions past <see cref="MaximumVersions"/>, never the
    /// current one.
    ///
    /// <para>Today every caller sets <see cref="ManagedSecret.CurrentVersion"/> to
    /// the version it just added before trimming, so the guard does not fire on
    /// any path through this class — setting a new value is meant to supersede a
    /// rollback, and the pinned version then ages out like any other. The guard
    /// makes trimming correct on its own terms rather than only in the presence of
    /// that ordering: a hand-edited file whose current version is old, or a later
    /// caller that trims without moving the pointer, would otherwise leave the
    /// secret naming a version that is no longer there — which
    /// <see cref="Resolve"/> reads as "not configured" and silently drops out of
    /// every app's env.</para>
    /// </summary>
    private static void Trim(ManagedSecret secret)
    {
        while (secret.Versions.Count > MaximumVersions)
        {
            var oldest = secret.Versions
                .Where(version => version.Version != secret.CurrentVersion)
                .OrderBy(version => version.Version)
                .FirstOrDefault();

            if (oldest is null)
            {
                return;
            }

            secret.Versions.Remove(oldest);
        }
    }

    private SecretFile Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var file = JsonSerializer.Deserialize<SecretFile>(SecureFile.ReadAllText(_path), SerializerOptions)
                    ?? new SecretFile();

                foreach (var secret in file.Secrets)
                {
                    foreach (var version in secret.Versions)
                    {
                        version.Value = _secrets.Unprotect(version.Value) ?? string.Empty;
                    }
                }

                return file;
            }
        }
        catch (JsonException)
        {
            // A corrupt file means "no secrets", never a crash — the same stance
            // every other store takes. It cannot mean "keep the last known values":
            // nothing in memory outlives a restart.
        }

        return new SecretFile();
    }

    private void Save(SecretFile file)
    {
        // Encrypted on the way out, on a copy, so callers keep holding plaintext
        // and a second save cannot double-encrypt. Still atomic and owner-only:
        // the key sits beside this file, so permissions stay the first line of
        // defence and encryption is what survives the file being copied away.
        var onDisk = new SecretFile
        {
            Secrets = [.. file.Secrets.Select(secret => new ManagedSecret
            {
                Scope = secret.Scope,
                Name = secret.Name,
                Description = secret.Description,
                CurrentVersion = secret.CurrentVersion,
                CreatedAt = secret.CreatedAt,
                UpdatedAt = secret.UpdatedAt,
                UpdatedBy = secret.UpdatedBy,
                Versions = [.. secret.Versions.Select(version => new SecretVersion
                {
                    Version = version.Version,
                    Value = _secrets.Protect(version.Value) ?? string.Empty,
                    CreatedAt = version.CreatedAt,
                    CreatedBy = version.CreatedBy,
                })],
            })],
        };

        SecureFile.WriteAllText(_path, JsonSerializer.Serialize(onDisk, SerializerOptions));
    }
}
