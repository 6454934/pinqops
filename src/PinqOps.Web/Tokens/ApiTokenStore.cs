using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PinqOps;

namespace PinqOps.Web;

/// <summary>One API token's stored metadata (never its plaintext).</summary>
public sealed class ApiToken
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>read | deploy | admin.</summary>
    public string Scope { get; set; } = "read";

    /// <summary>Hex SHA-256 of the full token — the lookup key.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Last 4 chars of the token, for display only.</summary>
    public string Last4 { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>
    /// The principal that minted this token.
    ///
    /// <para>Recorded now because it can only be captured now — who created a token
    /// is not something that can be worked out afterwards, so a token minted before
    /// this field existed can never have it. Empty on those, and empty is read as
    /// "unknown", never as anybody.</para>
    ///
    /// <para>It is here for the rule a token's team access should follow: derived
    /// from its creator's memberships, so removing someone from a team also removes
    /// the tokens they made. Until that lands a token's teams are only the ones it
    /// was explicitly added to, which is a deliberate admin act rather than an
    /// inheritance — the gap is that a token added explicitly outlives its
    /// creator's membership.</para>
    /// </summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>
    /// When the token stops being accepted. Null means it never expires, which
    /// stays possible for unattended use but is no longer the only option — an
    /// agent token handed out once and forgotten was previously valid forever.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } expiry && expiry <= now;
}

/// <summary>
/// Personal API tokens for the REST API — usable by any HTTP client or AI agent
/// (an OpenAI function-calling tool, a Claude MCP server, curl, CI). A token is
/// high-entropy, so it is stored as a fast SHA-256 hash (not PBKDF2) keyed for
/// O(1) lookup; the plaintext <c>pot_…</c> is shown once at creation.
/// </summary>
public sealed class ApiTokenStore
{
    public const string Prefix = "pot_";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly Lock _gate = new();

    public ApiTokenStore(string path) => _path = path;

    public static bool LooksLikeToken(string value) => value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Marks the synthetic principal an API token authenticates as. The ':' is
    /// deliberate: the username validator accepts only letters, digits, '-', '_'
    /// and '.', so no account can ever be created that collides with a token's
    /// principal — the prefix needs no separate reserved-name list.
    /// </summary>
    public const string PrincipalPrefix = "token:";

    /// <summary>
    /// The principal a token authenticates as. Every token gets its own, so
    /// container ownership actually separates two tokens: they used to share the
    /// literal "api-token", which made every ownership record readable and
    /// destroyable by any token on the host.
    /// </summary>
    public static string PrincipalFor(ApiToken token) => PrincipalPrefix + token.Id;

    /// <summary>Whether a resolved principal is a token rather than a user account.</summary>
    public static bool IsTokenPrincipal(string? user) =>
        user is not null && user.StartsWith(PrincipalPrefix, StringComparison.Ordinal);

    /// <summary>
    /// The principal every API token used to share, before each got its own.
    /// Ownership records written then name nobody who can authenticate now, so they
    /// resolve to "unowned" — which is admin-only, and therefore safe. They are
    /// deliberately not rewritten: reinterpreting them would hand access to whoever
    /// the guess landed on. Surfaced instead, so an admin can reassign the ones that
    /// still matter.
    /// </summary>
    public const string RetiredPrincipal = "api-token";

    /// <summary>Whether an ownership record names a principal that can no longer sign in.</summary>
    public static bool IsRetiredPrincipal(string? user) =>
        string.Equals(user, RetiredPrincipal, StringComparison.Ordinal);

    /// <summary>Mints a token, stores its hash, and returns the plaintext (once).</summary>
    public (ApiToken Token, string Plaintext) Create(
        string name, string scope, DateTimeOffset now, int? expiresInDays = null, string? createdBy = null)
    {
        var secret = Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var token = new ApiToken
        {
            Id = Base64Url(RandomNumberGenerator.GetBytes(6)),
            Name = string.IsNullOrWhiteSpace(name) ? "token" : name.Trim(),
            Scope = scope is "read" or "deploy" or "admin" ? scope : "read",
            Sha256 = Hash(secret),
            Last4 = secret[^4..],
            CreatedAt = now,
            CreatedBy = createdBy ?? string.Empty,
            ExpiresAt = expiresInDays is > 0 ? now.AddDays(expiresInDays.Value) : null,
        };

        lock (_gate)
        {
            var all = LoadAll();
            all.Add(token);
            Save(all);
        }

        return (token, secret);
    }

    /// <summary>The token's scope if it is valid, else null. Touches LastUsedAt (throttled).</summary>
    public string? Validate(string presented, DateTimeOffset now) => Authenticate(presented, now)?.Scope;

    /// <summary>
    /// The matched token if it is valid, else null. Touches LastUsedAt (throttled).
    /// The caller needs the whole token, not just its scope, so each token can
    /// authenticate as its own principal.
    /// </summary>
    public ApiToken? Authenticate(string presented, DateTimeOffset now)
    {
        var hash = Hash(presented);
        lock (_gate)
        {
            var all = LoadAll();
            var match = all.FirstOrDefault(t => CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(t.Sha256), Encoding.ASCII.GetBytes(hash)));
            // An expired token is rejected but kept, so it stays visible in the
            // list as an expired entry rather than silently disappearing.
            if (match is null || match.IsExpired(now))
            {
                return null;
            }

            // Persist "last used" at most once a minute to avoid a write per call.
            if (match.LastUsedAt is null || now - match.LastUsedAt >= TimeSpan.FromMinutes(1))
            {
                match.LastUsedAt = now;
                Save(all);
            }

            return match;
        }
    }

    public IReadOnlyList<ApiToken> List()
    {
        lock (_gate)
        {
            return LoadAll();
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            var all = LoadAll();
            var removed = all.RemoveAll(t => t.Id == id) > 0;
            if (removed)
            {
                Save(all);
            }

            return removed;
        }
    }

    /// <summary>
    /// Deletes every token one person minted, and returns how many. Called when
    /// that person loses the access the token carries — their account is removed,
    /// or their role is lowered.
    ///
    /// <para>A token whose <see cref="ApiToken.CreatedBy"/> is empty predates the
    /// field and belongs to nobody knowable, so it is never matched here. Empty is
    /// read as unknown, never as anybody — including as whoever is being removed.</para>
    /// </summary>
    public int DeleteCreatedBy(string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        lock (_gate)
        {
            var all = LoadAll();
            var removed = all.RemoveAll(token =>
                token.CreatedBy.Length > 0
                && string.Equals(token.CreatedBy, username, StringComparison.OrdinalIgnoreCase));

            if (removed > 0)
            {
                Save(all);
            }

            return removed;
        }
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private List<ApiToken> LoadAll()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<List<ApiToken>>(SecureFile.ReadAllText(_path), SerializerOptions) ?? [];
            }
        }
        catch (JsonException)
        {
        }

        return [];
    }

    private void Save(List<ApiToken> tokens) =>
        // Atomic + owner-only from creation, like every other store. The previous
        // write was neither: a torn write could empty the token list, and the
        // 0600 mode was applied only when the file was new, so a mode set wrong
        // once was never re-asserted.
        SecureFile.WriteAllText(_path, JsonSerializer.Serialize(tokens, SerializerOptions));
}

/// <summary>
/// Maps a request to the API scope it requires and compares scopes.
///
/// The base rule is coarse on purpose — a read is "read", a write is "admin" —
/// so any route nobody classified fails closed. Explicit tables then move
/// individual routes off that default in both directions: reads whose body
/// carries a secret move up, writes a deployer legitimately performs move down.
/// Route families are matched on path <em>segments</em> rather than string
/// prefixes, because a trailing-slash prefix cannot tell <c>{id}/action</c>
/// (operating a container) from <c>{id}/exec</c> (running code inside it).
///
/// Literal segments are compared case-<em>insensitively</em>, because ASP.NET
/// Core routing matches literal template segments that way: <c>/api/APPS/x/credentials</c>
/// reaches the same handler as <c>/api/apps/x/credentials</c>. A case-sensitive
/// classifier here would hand the caller the coarse "read" default for a route
/// this table exists to raise, which is an authorization bypass rather than a
/// cosmetic mismatch. Id segments stay verbatim — container names are
/// case-sensitive to docker.
/// </summary>
public static class ApiScopes
{
    /// <summary>
    /// Reads that return a secret verbatim: database dumps and volume tarballs,
    /// runner journal output, API-token metadata, the user list and the audit
    /// trail. Without this table the "read" default would hand every viewer the
    /// contents of the box.
    ///
    /// <c>/api/docker/ownership</c> and <c>/api/environments</c> are deliberately
    /// absent: the UI needs both to decide what to offer and where to send it, so
    /// instead of blocking them their handlers strip what the caller must not see
    /// — the records governing someone else's access, and the SSH details of a
    /// host respectively.
    /// </summary>
    private static readonly string[] AdminReadPrefixes =
    [
        "/api/backups/download",
        "/api/runner/logs",
        "/api/tokens",
        "/api/users",
        "/api/audit",
        // A Slack incoming-webhook URL is itself the credential — anyone holding
        // one can post into the channel — and it cannot be masked and still leave
        // the form editable. The rest of /api/alerts stays readable: what is
        // firing, and the container names and percentages behind it, are already
        // on the Overview and Containers views.
        "/api/alerts/channels",
        // Same secret, same reasoning: /api/notifications returns the deploy
        // webhook and Slack URLs verbatim. Leaving it at the "read" default made
        // the credential admin-gated on one route and world-readable on the other.
        "/api/notifications",
        // The secret vault, reads included. /reveal obviously returns a
        // credential, but the listing is admin too: the names alone enumerate
        // which credentials this server holds and which app each belongs to, which
        // is most of what an attacker needs to know where to aim. Putting the whole
        // family here also means every read is audited, because the audit
        // middleware records any read this table raises above "read".
        "/api/secrets",
        // A job's definition is a command run on the server, with the container it
        // runs in and the arguments it is given — a connection string, a path, a
        // bucket name. That is configuration rather than status, and listing it
        // tells a reader more about the infrastructure than the container list
        // does. Being here also means every read is audited.
        "/api/jobs",
        // An image's inspect payload carries Config.Env — every build-time variable
        // baked into it — and its history carries the Dockerfile commands verbatim,
        // which is where a token passed as a build argument ends up. The container
        // equivalent already masks those for non-admins; there is no useful masked
        // version of a layer's command, so these are admin outright.
        "/api/docker/images/inspect",
        "/api/docker/images/updates",
        "/api/docker/images/history",
        // Reading what is inside a volume is reading the application's data — the
        // rows of a database, the files somebody uploaded. The trailing slash is
        // deliberate: it covers browse, file and inspect without touching
        // /api/docker/volumes itself, which is the listing the Storage page needs.
        "/api/docker/volumes/",
        // The registry list alone says which registries this server holds
        // credentials for and under which account, which is most of what somebody
        // needs to know where to aim. Same stance as /api/secrets, same reason.
        "/api/registries",
        // The offsite settings name the bucket, the endpoint, the access key id and
        // which vault entry holds the secret — enough to say where every backup on
        // this server is copied to and under whose account.
        "/api/backups/offsite",
        // A connection string is a credential, and the listing alone says which
        // databases this server holds and on what version.
        "/api/databases",
        // A presigned link is a credential in a URL, and the listing says what this
        // account can reach.
        "/api/buckets",
        // The archive an entire database can be rebuilt from, and the window it
        // covers.
        "/api/pitr",
        // A container's output is whatever the application decided to print, which
        // on a bad day is a connection string or somebody's personal data. The live
        // per-container log route is already gated; a searchable archive of every
        // container is strictly more than that.
        "/api/logs",
        // Names every domain this server answers for and every route within them.
        "/api/traffic",
        // The relay host and the account this server signs in to it as. The
        // password is in the vault, but knowing where mail leaves from and under
        // whose name is most of what somebody needs to know where to aim.
        "/api/mail",
        // Who has been invited, at what role, and by whom — a list of accounts
        // about to exist and of the people behind them. The prefix stops at the
        // 's': /api/auth/invite is a different path, and it has to stay anonymous
        // because it runs before the account exists.
        "/api/invites",
    ];

    /// <summary>
    /// Writes every authenticated caller performs on its <em>own</em> session or
    /// credential. Without this table they fall to the coarse "admin" write
    /// default, which locks a viewer out of signing out or rotating its own
    /// password — the opposite of what those handlers are for. The real
    /// authorization stays in the handlers: change-password verifies the current
    /// password, logout revokes only the presented token.
    /// </summary>
    private static readonly string[] SelfServiceWrites =
    [
        "/api/auth/change-password",
        "/api/auth/logout",
        // A second factor belongs to the account, not to the role — a viewer has
        // to be able to protect their own login. The handlers do the rest: taking
        // it off your own account needs a current code, and taking it off somebody
        // else's needs admin.
        "/api/2fa/setup",
        "/api/2fa/enable",
        "/api/2fa/disable",
        "/api/2fa/recovery-codes",
    ];

    /// <summary>
    /// Per-container reads that expose the container's environment
    /// (<c>inspect</c>), its output (<c>logs</c>) or its process list
    /// (<c>top</c>). They sit at "deploy" rather than "admin" so an owner can
    /// still operate what it owns; the per-container ownership gate is what
    /// narrows them to those containers.
    /// </summary>
    private static readonly string[] DeployContainerReads = ["logs", "inspect", "top"];

    /// <summary>
    /// Container mutations a deployer may perform. <c>action</c> is itself
    /// allowlisted down to start/stop/restart/kill/pause/unpause in
    /// <see cref="DockerService"/>. Everything else under
    /// <c>/api/docker/containers/</c> — <c>exec</c>, <c>remove</c>,
    /// <c>commit</c>, <c>rename</c>, <c>restart-policy</c>, <c>owner</c> — is
    /// arbitrary code execution inside the container, destruction, or an
    /// ownership change, so it stays admin-only.
    /// </summary>
    private static readonly string[] DeployContainerWrites = ["action"];

    /// <summary>
    /// Wizard steps a deployer may run. The two that are excluded both reach
    /// host root: <c>install-runner</c> shells out to <c>sudo ./svc.sh</c>, and
    /// <c>create-dockerfile</c> commits caller-supplied content that the
    /// pipeline then builds and runs on the host. The workflow steps stay here
    /// because their content is a fixed server-side template
    /// (<see cref="SetupTemplates"/>), never caller input.
    /// </summary>
    private static readonly string[] DeploySetupActions =
    [
        "trigger-deploy", "create-workflow", "update-workflow", "app-var",
        "create-compose", "start-runner",
    ];

    /// <summary>
    /// Remaining writes a "deploy" token may perform. <c>/api/backups/restore</c>
    /// is deliberately absent: a restore wipes and reloads a live database or
    /// volume, so it is admin-only. Creating a container
    /// (POST <c>/api/docker/containers</c>, no id) is likewise absent and stays
    /// admin-only.
    ///
    /// <c>/api/alerts/</c> is deliberately absent too, so every alert write falls
    /// to the <c>admin</c> default: silencing or disabling a rule turns off paging
    /// for everyone on the host, and the channels route stores a bot token. A
    /// deployer rolling back its own app has no business editing the server's
    /// monitoring.
    /// </summary>
    private static readonly string[] DeployWritePrefixes =
    [
        "/api/deploy/rollback", "/api/compose/apply",
        "/api/apps/",
        "/api/previews/",
    ];

    public static string RequiredFor(string method, string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return HttpMethods.IsGet(method) || HttpMethods.IsHead(method)
            ? RequiredForRead(path, segments)
            : RequiredForWrite(path, segments);
    }

    private static string RequiredForRead(string path, string[] segments)
    {
        if (HasPrefix(path, AdminReadPrefixes))
        {
            return "admin";
        }

        // /api/apps/{id}/credentials — an installed catalog app's generated password.
        if (IsRoute(segments, "api", "apps", null, "credentials"))
        {
            return "admin";
        }

        // /api/ws/ping — an operator's connectivity diagnostic, so it sits with the
        // other operator tools rather than at the read default. Classified per route
        // rather than as an /api/ws/ prefix on purpose: the WebSocket routes that
        // follow are not all admin (tailing a container's log belongs at deploy,
        // beside the container reads), and a prefix would quietly decide that for
        // them.
        if (IsRoute(segments, "api", "ws", "ping"))
        {
            return "admin";
        }

        // GET /api/ws/containers/{id}/console — a shell inside the container, so it
        // belongs with POST /api/docker/containers/{id}/exec rather than at the read
        // default this would otherwise fall to. It is the one route where the method
        // says nothing about what it does: everything else that runs code is a write
        // and is classified as one, while opening a socket is a GET.
        if (IsRoute(segments, "api", "ws", "containers", null, "console"))
        {
            return "admin";
        }

        // GET /api/domains/{domain}/tls — certificate material status and a live
        // handshake probe. The listing stays at "read"; this one names issuer and
        // expiry for a specific host.
        if (IsRoute(segments, "api", "domains", null, "tls"))
        {
            return "admin";
        }

        return ContainerAction(segments) is { } action && Contains(DeployContainerReads, action)
            ? "deploy"
            : "read";
    }

    private static string RequiredForWrite(string path, string[] segments)
    {
        if (Contains(SelfServiceWrites, path.TrimEnd('/')))
        {
            return "read";
        }

        if (ContainerAction(segments) is { } action)
        {
            return Contains(DeployContainerWrites, action) ? "deploy" : "admin";
        }

        if (SetupAction(segments) is { } step)
        {
            return Contains(DeploySetupActions, step) ? "deploy" : "admin";
        }

        // POST /api/backups/run/{id} — matched on segments rather than as a
        // "/api/backups/run" string prefix, which would also cover
        // DELETE /api/backups/{targetId}/snapshots/{snapshot} for any target id
        // beginning with "run" and hand a deployer an admin-only deletion. The exact
        // length matters: the snapshot-delete route has five segments, and a target
        // whose id is literally "run" would otherwise still slip through.
        if (segments.Length == 4 && IsRoute(segments, "api", "backups", "run"))
        {
            return "deploy";
        }

        return HasPrefix(path, DeployWritePrefixes) ? "deploy" : "admin";
    }

    /// <summary>
    /// The trailing action of <c>/api/docker/containers/{id}/{action}</c>, or
    /// null when the path is not a per-container route (so creating a container,
    /// which carries no id, is never mistaken for operating one).
    /// </summary>
    private static string? ContainerAction(string[] segments) =>
        IsRoute(segments, "api", "docker", "containers", null, null) ? segments[4] : null;

    /// <summary>The step of <c>/api/setup/{step}</c>, or null when not a wizard route.</summary>
    private static string? SetupAction(string[] segments) =>
        IsRoute(segments, "api", "setup", null) ? segments[2] : null;

    /// <summary>
    /// Whether <paramref name="segments"/> starts with the given pattern, where a
    /// non-null element is a literal compared case-insensitively (matching how
    /// routing matches literals) and null is a wildcard that only has to exist.
    /// </summary>
    private static bool IsRoute(string[] segments, params string?[] pattern)
    {
        if (segments.Length < pattern.Length)
        {
            return false;
        }

        for (var index = 0; index < pattern.Length; index++)
        {
            if (pattern[index] is { } literal
                && !string.Equals(segments[index], literal, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasPrefix(string path, string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Contains(string[] values, string value) =>
        Array.Exists(values, candidate => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase));

    public static bool Satisfies(string have, string need) => Rank(have) >= Rank(need);

    private static int Rank(string scope) => scope switch { "admin" => 3, "deploy" => 2, _ => 1 };
}
