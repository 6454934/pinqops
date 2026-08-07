using System.Text.Json;

namespace PinqOps.Web;

/// <summary>
/// Stores the generated credentials of installed catalog apps
/// (<c>~/.config/pinqops/app-credentials.json</c>, 0600). Credentials are kept
/// retrievable — app volumes survive an uninstall, so a reinstall must reuse
/// the same password or the container env and the persisted data would
/// disagree.
/// </summary>
public sealed class AppCredentialStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// The entry holding an app's raw generated password — the value every
    /// <c>{{password}}</c> substitution is drawn from, whether it ends up in the
    /// container's environment or only on its command line.
    /// </summary>
    public const string PasswordKey = "password";

    private readonly string _path;
    private readonly SecretBox _secrets;
    private readonly Lock _gate = new();

    public AppCredentialStore(string? path = null, SecretBox? secrets = null)
    {
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "pinqops", "app-credentials.json");
        _secrets = secrets ?? SecretBox.ForDirectory(System.IO.Path.GetDirectoryName(_path)!);
    }

    public string Path_ => _path;

    public sealed class AppCredentials
    {
        public Dictionary<string, string> Env { get; set; } = new();
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// The storage key for an app on an environment. Credentials used to be keyed
    /// by app id alone, so the same app installed on two hosts shared one
    /// generated password — compromising the staging database would have handed
    /// over production's. Neither part can contain '/'.
    /// </summary>
    private static string KeyFor(string environmentId, string appId) =>
        $"{environmentId.ToLowerInvariant()}/{appId.ToLowerInvariant()}";

    /// <summary>Stored credential env for an app, or null when none recorded.</summary>
    public IReadOnlyDictionary<string, string>? Get(string environmentId, string appId)
    {
        lock (_gate)
        {
            return Load().GetValueOrDefault(KeyFor(environmentId, appId))?.Env;
        }
    }

    /// <summary>
    /// Returns the app's stored password, generating and persisting one on
    /// first use. This is what makes reinstalls (and cross-app references like
    /// WordPress → MySQL) line up with data in existing volumes.
    /// </summary>
    public string GetOrCreatePassword(string environmentId, string appId)
    {
        lock (_gate)
        {
            var all = Load();
            var key = KeyFor(environmentId, appId);
            if (all.TryGetValue(key, out var existing)
                && existing.Env.TryGetValue(PasswordKey, out var stored)
                && stored.Length > 0)
            {
                return stored;
            }

            var password = PasswordGenerator.Generate();
            var credentials = all.TryGetValue(key, out var current) ? current : new AppCredentials();
            credentials.Env[PasswordKey] = password;
            all[key] = credentials;
            Save(all);
            return password;
        }
    }

    /// <summary>
    /// The entries of a stored credential set that are worth showing.
    ///
    /// <see cref="PasswordKey"/> is the raw generated secret every
    /// <c>{{password}}</c> substitution is drawn from, so for most apps it merely
    /// repeats a named entry (<c>POSTGRES_PASSWORD=…</c>) and is left out. For an
    /// app whose password only ever reaches the container through its command
    /// line — redis' <c>--requirepass</c>, keydb's, nats' <c>--auth</c>, surreal's
    /// <c>--pass</c> — there is no named entry at all, and dropping it
    /// unconditionally left exactly those apps reporting "no stored credentials"
    /// for the password their catalog note promises was generated, with nothing
    /// anywhere that could recover it.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Displayable(
        IReadOnlyDictionary<string, string>? env)
    {
        if (env is null)
        {
            return [];
        }

        var named = env
            .Where(pair => !string.Equals(pair.Key, PasswordKey, StringComparison.Ordinal))
            .ToList();

        if (env.TryGetValue(PasswordKey, out var password)
            && password.Length > 0
            && !named.Any(pair => string.Equals(pair.Value, password, StringComparison.Ordinal)))
        {
            named.Add(new KeyValuePair<string, string>(PasswordKey, password));
        }

        return named;
    }

    /// <summary>Records the resolved credential env values shown to the user.</summary>
    public void SetEnv(string environmentId, string appId, IReadOnlyDictionary<string, string> env)
    {
        lock (_gate)
        {
            var all = Load();
            var key = KeyFor(environmentId, appId);
            var credentials = all.TryGetValue(key, out var current) ? current : new AppCredentials();
            foreach (var (name, value) in env)
            {
                credentials.Env[name] = value;
            }

            all[key] = credentials;
            Save(all);
        }
    }

    private Dictionary<string, AppCredentials> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var stored = JsonSerializer.Deserialize<Dictionary<string, AppCredentials>>(
                    SecureFile.ReadAllText(_path), SerializerOptions) ?? new();

                // Entries written before environments existed describe the only
                // host there was, so they belong to the local environment.
                //
                // Built entry by entry rather than with ToDictionary: a file
                // holding both a legacy "redis" and an already-migrated
                // "local/redis" folds them onto one key, and ToDictionary throws
                // for that — which would fail every credential read and install
                // until the file is hand-edited. The already-namespaced record
                // wins, because it is the one the running version wrote.
                var all = new Dictionary<string, AppCredentials>(StringComparer.Ordinal);
                foreach (var (key, value) in stored)
                {
                    if (key is null || value is null)
                    {
                        continue;
                    }

                    if (key.Contains('/', StringComparison.Ordinal))
                    {
                        all[key] = value;
                    }
                    else
                    {
                        all.TryAdd(KeyFor(ManagedEnvironment.LocalId, key), value);
                    }
                }

                // Values written before encryption pass through unchanged and are
                // re-written encrypted on the next save.
                foreach (var credentials in all.Values)
                {
                    foreach (var (name, value) in credentials.Env.ToList())
                    {
                        credentials.Env[name] = _secrets.Unprotect(value) ?? string.Empty;
                    }
                }

                return all;
            }
        }
        catch (JsonException)
        {
            // A corrupt file must not block installs; credentials restart empty.
        }

        return new Dictionary<string, AppCredentials>();
    }

    private void Save(Dictionary<string, AppCredentials> all)
    {
        // Encrypted on the way out, on a copy, so callers keep seeing plaintext
        // and a second save cannot double-encrypt. Still atomic + owner-only: the
        // key sits beside this file, so file permissions remain the first line of
        // defence and encryption is what survives the file being copied away.
        var onDisk = all.ToDictionary(
            entry => entry.Key,
            entry => new AppCredentials
            {
                CreatedAt = entry.Value.CreatedAt,
                Env = entry.Value.Env.ToDictionary(
                    pair => pair.Key,
                    pair => _secrets.Protect(pair.Value) ?? string.Empty),
            });

        SecureFile.WriteAllText(_path, JsonSerializer.Serialize(onDisk, SerializerOptions));
    }
}
