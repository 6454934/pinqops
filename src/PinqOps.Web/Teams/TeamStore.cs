using System.Text.Json;

namespace PinqOps.Web;

/// <summary>
/// Teams and resource grants, in one file at <c>~/.config/pinqops/teams.json</c>.
///
/// <para><b>Why one file.</b> Deleting a team has to delete its grants in the same
/// write. A dangling grant is not tidiness — a team id that is later re-created
/// would inherit access nobody granted it. Two files cannot be updated atomically
/// together, so they are one.</para>
///
/// <para><b>Why not ui.json.</b> <c>UiConfigStore.Update</c> clones the entire
/// config through JSON on every settings save, and that object holds the GitHub
/// token. A grant table that grows with the install does not belong inside it.</para>
///
/// <para><b>Why the lookups are cached.</b> Once the resource gate is enforcing,
/// this is read on every governed request. Parsing a file per request is what
/// <c>ContainerOwnershipStore</c> does today and is not worth copying, so a
/// snapshot with its indexes already built is published on every write and read
/// without touching the disk.</para>
///
/// <para><b>A file that cannot be read denies.</b> Corrupt or unreadable means an
/// empty directory, which grants nobody anything — every non-admin is refused,
/// while an admin still has everything and can repair it. Loading is on the hot
/// path, outside any exception filter, so an escape here would be a 500 on every
/// action at once.</para>
/// </summary>
public sealed class TeamStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly Lock _gate = new();

    private Snapshot _current;

    public TeamStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "pinqops", "teams.json");
        _current = Snapshot.From(Load());
    }

    public string Path_ => _path;

    /// <summary>Every team, as stored. Never mutated in place.</summary>
    public IReadOnlyList<Team> Teams => _current.Directory.Teams;

    /// <summary>Every grant, as stored.</summary>
    public IReadOnlyList<ResourceGrant> Grants => _current.Directory.Grants;

    /// <summary>
    /// Load, mutate and save under one lock, returning whatever the callback
    /// returns. The callback mutates a clone; the result is published only after it
    /// is on disk, so a reader never sees a half-applied change and a failed write
    /// leaves the previous state in memory rather than a state that exists nowhere.
    /// </summary>
    public T Update<T>(Func<TeamDirectory, T> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_gate)
        {
            var working = Clone(_current.Directory);
            var result = mutate(working);
            Save(working);
            _current = Snapshot.From(working);
            return result;
        }
    }

    /// <summary>The team every existing user is put in when teams first appear.</summary>
    public const string DefaultTeamId = "default";

    /// <summary>
    /// Creates a <c>default</c> team holding everyone, once, on an install that has
    /// users but no teams yet. Global admins go in as owners; everyone else as
    /// members.
    ///
    /// <para><b>It creates no grants, and nothing depends on it.</b> Because a
    /// resource with no grant behaves exactly as it did before teams existed, an
    /// install with this team and an install with no teams at all are
    /// indistinguishable. It is a starting point in the UI — somewhere to grant
    /// from — not a migration anything reads. That is what makes teams safe to turn
    /// on: nobody's access changes on the day they appear.</para>
    ///
    /// <para>Skipped once any team exists, so it cannot resurrect a team an
    /// operator deliberately deleted, and skipped when there are no users, so a
    /// fresh install does not get an empty team before its first admin exists.</para>
    /// </summary>
    public bool SeedDefaultTeam(IReadOnlyList<UserAccount> users)
    {
        ArgumentNullException.ThrowIfNull(users);

        return Update(directory =>
        {
            if (directory.Teams.Count > 0 || users.Count == 0)
            {
                return false;
            }

            directory.Teams.Add(new Team
            {
                Id = DefaultTeamId,
                Name = "Default",
                CreatedAt = DateTimeOffset.UtcNow,
                Members =
                [
                    .. users.Select(user => new TeamMember
                    {
                        Principal = user.Username,
                        Role = user.Role == UserRoles.Admin ? TeamRoles.Owner : TeamRoles.Member,
                    }),
                ],
            });

            return true;
        });
    }

    /// <summary>The teams a principal belongs to. Empty for one that belongs to none.</summary>
    public IReadOnlyList<string> TeamsOf(string? principal)
    {
        if (string.IsNullOrEmpty(principal))
        {
            return [];
        }

        return _current.TeamsByPrincipal.TryGetValue(principal, out var teams) ? teams : [];
    }

    /// <summary>The grants naming one resource. Empty means "unowned", which the gate reads as admin-only.</summary>
    public IReadOnlyList<ResourceGrant> GrantsFor(string kind, string environmentId, string resourceId) =>
        _current.GrantsByResource.TryGetValue(KeyFor(kind, environmentId, resourceId), out var grants)
            ? grants
            : [];

    public Team? Find(string? teamId)
    {
        if (string.IsNullOrEmpty(teamId))
        {
            return null;
        }

        return _current.Directory.Teams.Find(team =>
            string.Equals(team.Id, teamId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Drops every grant recorded against one host, and says how many went.
    ///
    /// <para>The mirror of what <see cref="RemoveTeam"/> does for a team, and for the
    /// same reason: a grant naming a host that is gone is inherited by the next host
    /// registered under that id — a rebuild, a replacement server, the same obvious
    /// name reused — handing the new machine's containers to whoever held grants on
    /// the old one, with nobody having granted anything.</para>
    /// </summary>
    public int RemoveEnvironment(string environmentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentId);

        return Update(directory => directory.Grants.RemoveAll(grant =>
            string.Equals(grant.EnvironmentId, environmentId, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Drops every grant naming one resource (any environment). Used when an app is
    /// purged so a later app reusing the same id does not inherit old access.
    /// </summary>
    public int RemoveResource(string kind, string resourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);

        return Update(directory => directory.Grants.RemoveAll(grant =>
            string.Equals(grant.Kind, kind, StringComparison.Ordinal)
            && string.Equals(grant.ResourceId, resourceId, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Removes a team and, in the same write, every grant that named it. Returns
    /// false when there was no such team.
    /// </summary>
    public bool RemoveTeam(string teamId)
    {
        var id = TeamId.Normalize(teamId);

        return Update(directory =>
        {
            var removed = directory.Teams.RemoveAll(team =>
                string.Equals(team.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                return false;
            }

            // Same write, deliberately: a grant left naming a deleted team would be
            // inherited by a team of that id created later.
            directory.Grants.RemoveAll(grant => string.Equals(grant.TeamId, id, StringComparison.OrdinalIgnoreCase));
            return true;
        });
    }

    /// <summary>
    /// The storage key for one resource. Neither the environment id
    /// (<c>^[a-z0-9][a-z0-9-]{0,31}$</c>) nor a container name or app slug can
    /// contain '/', and the kind is one of a fixed set, so the three parts cannot
    /// run together ambiguously.
    /// </summary>
    private static string KeyFor(string kind, string environmentId, string resourceId) =>
        $"{kind}/{environmentId.ToLowerInvariant()}/{resourceId}";

    private static TeamDirectory Clone(TeamDirectory directory) =>
        JsonSerializer.Deserialize<TeamDirectory>(
            JsonSerializer.Serialize(directory, SerializerOptions), SerializerOptions) ?? new TeamDirectory();

    private TeamDirectory Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<TeamDirectory>(SecureFile.ReadAllText(_path), SerializerOptions)
                    ?? new TeamDirectory();
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // Empty grants nobody anything, which is the safe reading. The same
            // catch list as ContainerOwnershipStore, and for the same reason: this
            // runs on the hot path, outside the API's exception filter.
        }

        return new TeamDirectory();
    }

    private void Save(TeamDirectory directory) =>
        SecureFile.WriteAllText(_path, JsonSerializer.Serialize(directory, SerializerOptions));

    /// <summary>
    /// An immutable view with its lookups already built. Replaced wholesale on
    /// every write, so a request enumerating memberships can never observe a
    /// collection being modified.
    /// </summary>
    private sealed class Snapshot
    {
        private Snapshot(
            TeamDirectory directory,
            Dictionary<string, List<string>> teamsByPrincipal,
            Dictionary<string, List<ResourceGrant>> grantsByResource)
        {
            Directory = directory;
            TeamsByPrincipal = teamsByPrincipal;
            GrantsByResource = grantsByResource;
        }

        public TeamDirectory Directory { get; }

        public Dictionary<string, List<string>> TeamsByPrincipal { get; }

        public Dictionary<string, List<ResourceGrant>> GrantsByResource { get; }

        public static Snapshot From(TeamDirectory directory)
        {
            var teamsByPrincipal = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var team in directory.Teams)
            {
                if (!TeamId.IsValid(team.Id))
                {
                    // A hand-edited entry with an unusable id is skipped rather than
                    // half-honoured; it cannot be addressed by any route anyway.
                    continue;
                }

                foreach (var member in team.Members)
                {
                    if (string.IsNullOrWhiteSpace(member.Principal))
                    {
                        continue;
                    }

                    if (!teamsByPrincipal.TryGetValue(member.Principal, out var teams))
                    {
                        teams = [];
                        teamsByPrincipal[member.Principal] = teams;
                    }

                    if (!teams.Contains(team.Id, StringComparer.OrdinalIgnoreCase))
                    {
                        teams.Add(team.Id);
                    }
                }
            }

            var known = new HashSet<string>(
                directory.Teams.Select(team => team.Id), StringComparer.OrdinalIgnoreCase);

            var grantsByResource = new Dictionary<string, List<ResourceGrant>>(StringComparer.Ordinal);
            foreach (var grant in directory.Grants)
            {
                // A grant naming a team that is not there is ignored, never resolved
                // by name to something else.
                if (!ResourceKinds.IsKnown(grant.Kind)
                    || string.IsNullOrWhiteSpace(grant.ResourceId)
                    || !known.Contains(grant.TeamId))
                {
                    continue;
                }

                var key = KeyFor(grant.Kind, grant.EnvironmentId ?? string.Empty, grant.ResourceId);
                if (!grantsByResource.TryGetValue(key, out var forResource))
                {
                    forResource = [];
                    grantsByResource[key] = forResource;
                }

                forResource.Add(grant);
            }

            return new Snapshot(directory, teamsByPrincipal, grantsByResource);
        }
    }
}
