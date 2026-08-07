using System.Text.Json;

namespace PinqOps.Web;

/// <summary>
/// Records who owns each container and whether it is private or public
/// (<c>~/.config/pinqops/container-owners.json</c>, 0600). Ownership is keyed by
/// container name — the same identifier the dashboard and the action endpoints
/// use. Only admins can (re)assign ownership; a non-admin can fully manage the
/// containers they own (or any marked public), and nothing else.
/// </summary>
public sealed class ContainerOwnershipStore
{
    public const string AccessPrivate = "private";
    public const string AccessPublic = "public";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly Lock _gate = new();

    public ContainerOwnershipStore(string? path = null)
    {
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "pinqops", "container-owners.json");
    }

    public sealed class ContainerOwnership
    {
        public string Owner { get; set; } = "";
        public string Access { get; set; } = AccessPrivate;
    }

    /// <summary>
    /// The storage key for a container on an environment. Ownership used to be
    /// keyed by container name alone, which was fine while there was one Docker
    /// host — with several, owning <c>web</c> on staging would have granted
    /// <c>web</c> in production. Neither part can contain '/' (container names
    /// and environment ids are both validated against narrower sets), so the
    /// composite key is unambiguous.
    /// </summary>
    public static string KeyFor(string environmentId, string containerName) =>
        $"{environmentId.ToLowerInvariant()}/{containerName}";

    public ContainerOwnership? Get(string environmentId, string containerName)
    {
        lock (_gate)
        {
            return Load().GetValueOrDefault(KeyFor(environmentId, containerName));
        }
    }

    /// <summary>The records for one environment, keyed by container name.</summary>
    public IReadOnlyDictionary<string, ContainerOwnership> All(string environmentId)
    {
        var prefix = $"{environmentId.ToLowerInvariant()}/";
        lock (_gate)
        {
            return Load()
                .Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal))
                .ToDictionary(entry => entry.Key[prefix.Length..], entry => entry.Value, StringComparer.Ordinal);
        }
    }

    public void Set(string environmentId, string containerName, string owner, string access)
    {
        var normalizedAccess = access == AccessPublic ? AccessPublic : AccessPrivate;
        lock (_gate)
        {
            var all = Load();
            all[KeyFor(environmentId, containerName)] = new ContainerOwnership { Owner = owner, Access = normalizedAccess };
            Save(all);
        }
    }

    /// <summary>
    /// Drops every ownership record for one host, and says how many went. Called when
    /// the host is de-registered: a record keyed to an id that is gone would name the
    /// containers of whatever is registered under that id next.
    /// </summary>
    public int RemoveEnvironment(string environmentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentId);

        lock (_gate)
        {
            var all = Load();
            var prefix = KeyFor(environmentId, string.Empty);
            var stale = all.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var key in stale)
            {
                all.Remove(key);
            }

            if (stale.Count > 0)
            {
                Save(all);
            }

            return stale.Count;
        }
    }

    public void Remove(string environmentId, string containerName)
    {
        lock (_gate)
        {
            var all = Load();
            if (all.Remove(KeyFor(environmentId, containerName)))
            {
                Save(all);
            }
        }
    }

    private Dictionary<string, ContainerOwnership> Load()
    {
        // This runs inside the per-container ownership middleware, on every
        // governed request and outside Safe(), so anything that escapes here is an
        // unhandled 500 on every container action at once. The catch list is
        // therefore everything reading a file can raise, not just bad JSON.
        try
        {
            if (File.Exists(_path))
            {
                var stored = JsonSerializer.Deserialize<Dictionary<string, ContainerOwnership>>(
                    SecureFile.ReadAllText(_path), SerializerOptions) ?? new();

                // Records written before environments existed are keyed by bare
                // container name; they described the only host there was, so they
                // belong to the local environment.
                //
                // Built entry by entry rather than with ToDictionary: a file holding
                // both a legacy "web" and an already-migrated "local/web" folds them
                // onto one key, and ToDictionary throws for that — the one failure
                // this method's whole contract says must not happen. The
                // already-namespaced record wins, because it is the one the running
                // version wrote.
                var migrated = new Dictionary<string, ContainerOwnership>(StringComparer.Ordinal);
                foreach (var (key, value) in stored)
                {
                    if (key is null || value is null)
                    {
                        continue;
                    }

                    if (key.Contains('/', StringComparison.Ordinal))
                    {
                        migrated[key] = value;
                    }
                    else
                    {
                        migrated.TryAdd(KeyFor(ManagedEnvironment.LocalId, key), value);
                    }
                }

                return migrated;
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable ownership file must not lock everyone out; it
            // restarts empty (which falls back to admin-only management, the safe
            // default).
        }

        return new Dictionary<string, ContainerOwnership>();
    }

    private void Save(Dictionary<string, ContainerOwnership> all) =>
        SecureFile.WriteAllText(_path, JsonSerializer.Serialize(all, SerializerOptions));
}
