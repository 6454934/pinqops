using System.Text.Json.Serialization;

namespace PinqOps.Web;

/// <summary>
/// Persisted state of the web UI: the dashboard password hash, the GitHub
/// account connection, and the connected app repositories. Stored as JSON with
/// 0600 permissions (it contains the PAT).
/// </summary>
public sealed class UiConfig
{
    public const string DefaultComposeFile = "/opt/pinqops/docker-compose.yml";
    public const string DefaultRunnerDirectory = "/opt/actions-runner";

    /// <summary>Where apps added after the multi-app upgrade keep their compose projects.</summary>
    public const string DefaultAppsRoot = "/opt/pinqops/apps";

    /// <summary>Where apps added after the multi-app upgrade keep their runners.</summary>
    public const string DefaultRunnersRoot = "/opt/pinqops/runners";

    /// <summary>
    /// Host port a newly generated compose project publishes the app on. Kept off
    /// 80/443 so the first deploy cannot collide with something already bound.
    /// </summary>
    public const int DefaultHostPort = 8080;

    /// <summary>The dashboard's user accounts (username, password hash, role).</summary>
    public List<UserAccount> Users { get; set; } = [];

    /// <summary>Optional GitHub username; when set the PAT is sent as Basic auth.</summary>
    public string? Username { get; set; }

    /// <summary>GitHub personal access token (or OAuth device-flow token) for the API.</summary>
    public string? Pat { get; set; }

    /// <summary>Optional OAuth App client id enabling "Sign in with GitHub" (device flow).</summary>
    public string? GithubClientId { get; set; }

    /// <summary>
    /// Whether every account has to have two-factor set up.
    ///
    /// <para>Switching it on does not sign anybody out or lock anybody out: an
    /// account without it can still sign in with its password, and is then required
    /// to finish enrolment before it can do anything else. An admin who turned this
    /// on from their phone and then could not reach the dashboard would have no way
    /// back in, and there is no support desk here.</para>
    /// </summary>
    public bool RequireTwoFactor { get; set; }

    /// <summary>The app repositories this server manages.</summary>
    public List<AppConnection> Apps { get; set; } = [];

    /// <summary>
    /// The Docker hosts this dashboard manages. Always contains the local
    /// environment; remote ones are added by an admin.
    /// </summary>
    public List<ManagedEnvironment> Environments { get; set; } = [];

    // ---- Legacy single-app / single-user fields ---------------------------------
    // Read for migration only and nulled afterwards, so a saved config carries
    // nothing but `apps` and `users`.

    /// <summary>Legacy: the single dashboard password hash (migrated into <see cref="Users"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PasswordHash { get; set; }

    /// <summary>Legacy: the single connected repository URL.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RepoUrl { get; set; }

    /// <summary>Legacy: the single compose file path.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ComposeFile { get; set; }

    /// <summary>Legacy: the single runner install directory.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunnerDirectory { get; set; }
}

/// <summary>
/// One connected app repository: where its compose project and runner live.
/// Account-scoped state (PAT, OAuth client id) stays on <see cref="UiConfig"/> —
/// the token belongs to the account, not to any one repository.
/// </summary>
public sealed class AppConnection
{
    public required string Id { get; set; }

    public required string RepoUrl { get; set; }

    public required string ComposeFile { get; set; }

    public required string RunnerDirectory { get; set; }

    /// <summary>
    /// The app id for a repository: <c>&lt;owner&gt;-&lt;repo&gt;</c> reduced by the
    /// compose-name rules. Including the owner keeps two same-named repos from
    /// different owners apart; the id also seeds the default paths.
    /// </summary>
    public static string SlugFor(GitHubRepository repository) =>
        ComposeProjectName.FromRepository($"{repository.Owner}-{repository.Name}");

    /// <summary>Default compose path for a new app.</summary>
    public static string DefaultComposeFileFor(string id) =>
        $"{UiConfig.DefaultAppsRoot}/{id}/docker-compose.yml";

    /// <summary>Default runner directory for a new app.</summary>
    public static string DefaultRunnerDirectoryFor(string id) =>
        $"{UiConfig.DefaultRunnersRoot}/{id}";
}

/// <summary>One dashboard user: a login, a password hash, and a role.</summary>
public sealed class UserAccount
{
    public required string Username { get; set; }

    /// <summary>PBKDF2 hash of the password ("salt.hash" base64).</summary>
    public required string PasswordHash { get; set; }

    /// <summary>One of <see cref="UserRoles"/>: viewer, deployer, or admin.</summary>
    public string Role { get; set; } = UserRoles.Admin;

    /// <summary>
    /// The TOTP secret, base32, encrypted at rest beside the PAT.
    ///
    /// <para>Present but with <see cref="TwoFactorEnabled"/> false means enrolment
    /// has started and no code has been proved yet. Keeping the two apart is what
    /// stops a half-finished setup from locking somebody out of their own
    /// account.</para>
    /// </summary>
    public string? TwoFactorSecret { get; set; }

    public bool TwoFactorEnabled { get; set; }

    /// <summary>Hashes of the unused recovery codes. Each is struck off the moment it works.</summary>
    public List<string> RecoveryCodeHashes { get; set; } = [];

    /// <summary>
    /// The last TOTP step this account signed in with. Without it a code stays
    /// usable for the rest of its window, so anyone who watched it being typed can
    /// sign in again within the minute.
    /// </summary>
    public long LastTotpCounter { get; set; } = -1;
}

/// <summary>
/// The three dashboard roles and their mapping to the API token scopes, so a
/// single permission check covers both a session (role) and a token (scope):
/// viewer→read, deployer→deploy, admin→admin.
/// </summary>
public static class UserRoles
{
    public const string Viewer = "viewer";
    public const string Deployer = "deployer";
    public const string Admin = "admin";

    /// <summary>The legacy username a pre-multi-user password migrates to.</summary>
    public const string LegacyAdmin = "admin";

    public static bool IsValid(string? role) => role is Viewer or Deployer or Admin;

    /// <summary>Normalizes an incoming role, defaulting anything unknown to the least privilege.</summary>
    public static string Normalize(string? role) => IsValid(role) ? role! : Viewer;

    /// <summary>The API scope a role is equivalent to (viewer→read, deployer→deploy, admin→admin).</summary>
    public static string ScopeFor(string role) => role switch
    {
        Admin => "admin",
        Deployer => "deploy",
        _ => "read",
    };
}
