using System.Text.Json;

namespace PinqOps.Web;

/// <summary>
/// Loads and saves the <see cref="UiConfig"/> JSON file. The file holds a PAT,
/// so it is created with owner-only permissions.
/// </summary>
public sealed class UiConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly SecretBox _secrets;
    private readonly Lock _gate = new();
    private UiConfig _current;

    public UiConfigStore(string? path = null, SecretBox? secrets = null)
    {
        _path = path
            ?? Environment.GetEnvironmentVariable("PINQOPS_UI_CONFIG")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "pinqops", "ui.json");
        _secrets = secrets ?? SecretBox.ForDirectory(Path.GetDirectoryName(_path)!);
        _current = Load();
    }

    public string Path_ => _path;

    public UiConfig Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void Update(Action<UiConfig> mutate)
    {
        lock (_gate)
        {
            // Copy-on-write: mutate a private clone and publish it only after it
            // is safely saved. Readers hold whichever snapshot was current when
            // they called Current; because that object is never mutated in place
            // again, a concurrent GET that enumerates config.Apps / config.Users
            // can't throw "collection was modified" or observe a half-applied
            // change. It also keeps the in-memory state consistent with disk when
            // a Save fails — the old snapshot stays current.
            var next = Clone(_current);
            mutate(next);
            Save(next);
            _current = next;
        }
    }

    /// <summary>A deep copy via the same JSON round-trip used to persist and load
    /// the config, so the clone is independent of the live snapshot.</summary>
    private static UiConfig Clone(UiConfig config) =>
        JsonSerializer.Deserialize<UiConfig>(JsonSerializer.Serialize(config, SerializerOptions)) ?? new UiConfig();

    private UiConfig Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var config = Migrate(JsonSerializer.Deserialize<UiConfig>(SecureFile.ReadAllText(_path)) ?? new UiConfig());
                // A plaintext token from before encryption passes through
                // unchanged and is re-written encrypted on the next save.
                config.Pat = _secrets.Unprotect(config.Pat);
                foreach (var environment in config.Environments)
                {
                    environment.PrivateKey = _secrets.Unprotect(environment.PrivateKey);
                }

                foreach (var user in config.Users)
                {
                    // A TOTP secret is a second password: anyone holding it can
                    // produce that account's codes forever.
                    user.TwoFactorSecret = _secrets.Unprotect(user.TwoFactorSecret);
                }

                return config;
            }
        }
        catch (JsonException)
        {
            // A corrupt config file should not brick the UI; start fresh.
        }

        // A fresh (or unreadable) config goes through the same migration as a
        // loaded one — otherwise a brand-new install would start with no
        // environments at all, and every request would have nothing to resolve.
        return Migrate(new UiConfig());
    }

    /// <summary>
    /// Wraps a legacy single-app / single-user config into
    /// <see cref="UiConfig.Apps"/> and <see cref="UiConfig.Users"/>. The migrated
    /// app keeps its paths and the migrated password becomes the sole <c>admin</c>
    /// user; the PAT is untouched. In-memory only — the file is rewritten in the
    /// new shape on the next <see cref="Update"/>. Idempotent.
    /// </summary>
    public static UiConfig Migrate(UiConfig config)
    {
        // A pre-multi-user config has a top-level password hash and no users; that
        // hash becomes the first admin. The admin must NEVER be locked out, so a
        // migration failure here is not possible — it is a pure list add.
        if (config.Users.Count == 0 && !string.IsNullOrWhiteSpace(config.PasswordHash))
        {
            config.Users.Add(new UserAccount
            {
                Username = UserRoles.LegacyAdmin,
                PasswordHash = config.PasswordHash,
                Role = UserRoles.Admin,
            });
        }

        config.PasswordHash = null;

        // The local daemon is an environment like any other, so it has to exist
        // in the list for the switcher, the ?env= lookup and the audit target to
        // have something to resolve. It is recreated rather than merely defaulted
        // so deleting it by hand cannot leave a config that addresses nothing.
        if (!config.Environments.Any(environment =>
                string.Equals(environment.Id, ManagedEnvironment.LocalId, StringComparison.OrdinalIgnoreCase)))
        {
            config.Environments.Insert(0, ManagedEnvironment.Local());
        }

        if (config.Apps.Count == 0 && !string.IsNullOrWhiteSpace(config.RepoUrl))
        {
            string id;
            try
            {
                id = AppConnection.SlugFor(GitHubRepositoryParser.Parse(config.RepoUrl));
            }
            catch (ArgumentException)
            {
                // A hand-edited garbage URL still must not lose the connection.
                id = ComposeProjectName.Fallback;
            }

            config.Apps.Add(new AppConnection
            {
                Id = id,
                RepoUrl = config.RepoUrl,
                ComposeFile = string.IsNullOrWhiteSpace(config.ComposeFile)
                    ? UiConfig.DefaultComposeFile
                    : config.ComposeFile,
                RunnerDirectory = string.IsNullOrWhiteSpace(config.RunnerDirectory)
                    ? UiConfig.DefaultRunnerDirectory
                    : config.RunnerDirectory,
            });
        }

        config.RepoUrl = null;
        config.ComposeFile = null;
        config.RunnerDirectory = null;
        return config;
    }

    private void Save(UiConfig config)
    {
        // The in-memory model always holds the token in the clear; it is
        // encrypted on the way out only, on a copy, so the live snapshot is never
        // mutated (and a second save cannot double-encrypt).
        var onDisk = Clone(config);
        onDisk.Pat = _secrets.Protect(onDisk.Pat);
        foreach (var environment in onDisk.Environments)
        {
            // An SSH key is shell access to that host, so it gets the same
            // treatment as the GitHub token.
            environment.PrivateKey = _secrets.Protect(environment.PrivateKey);
        }

        foreach (var user in onDisk.Users)
        {
            user.TwoFactorSecret = _secrets.Protect(user.TwoFactorSecret);
        }


        // Atomic + owner-only: the file holds a broad-scope PAT, and a torn
        // write would wipe the password hash and drop the dashboard back to the
        // unauthenticated setup flow.
        SecureFile.WriteAllText(_path, JsonSerializer.Serialize(onDisk, SerializerOptions));
    }
}
