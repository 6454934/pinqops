namespace PinqOps.Web;

/// <summary>
/// A group of principals that resources can be granted to.
///
/// <para>A team answers <em>which</em> resources someone may act on. It does not
/// answer <em>what</em> they may do — that stays with the global role
/// (<see cref="UserRoles"/>) and the scope table, unchanged. Keeping the two
/// orthogonal is what stops this from doubling the model: a viewer in a team with
/// full access still cannot deploy, because the policy gate refuses first.</para>
/// </summary>
public sealed class Team
{
    public required string Id { get; set; }

    public required string Name { get; set; }

    public List<TeamMember> Members { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One principal's place in a team.</summary>
public sealed class TeamMember
{
    /// <summary>A username, or an API token principal (<c>token:&lt;id&gt;</c>).</summary>
    public required string Principal { get; set; }

    public string Role { get; set; } = TeamRoles.Member;
}

/// <summary>
/// Roles inside a team. Two, deliberately: an <c>owner</c> manages the membership
/// and the grants, a <c>member</c> is covered by them.
///
/// <para>There is no team-level <c>viewer</c> — it would be exactly redundant with
/// the global viewer role, which already refuses every mutation at the policy gate
/// before a team is ever consulted.</para>
/// </summary>
public static class TeamRoles
{
    public const string Owner = "owner";

    public const string Member = "member";

    public static bool IsValid(string? role) => role is Owner or Member;

    public static string Normalize(string? role) => IsValid(role) ? role! : Member;
}

/// <summary>
/// How much a grant confers. Bounded by the caller's global scope, never widening
/// it: the effective permission is the lesser of the two.
/// </summary>
public static class GrantAccess
{
    /// <summary>Act on the resource, within what the caller's global role allows.</summary>
    public const string Manage = "manage";

    /// <summary>See the resource in listings and read it.</summary>
    public const string View = "view";

    public static bool IsValid(string? access) => access is Manage or View;

    /// <summary>
    /// An unrecognised value is read as the <em>lowest</em> access, never the
    /// highest. A hand-edited file with a typo must not become an escalation.
    /// </summary>
    public static string Normalize(string? access) => access == Manage ? Manage : View;

    public static bool Satisfies(string have, string need) => Rank(have) >= Rank(need);

    private static int Rank(string access) => access == Manage ? 2 : 1;
}

/// <summary>
/// The kinds of thing a grant can name.
///
/// <para>All of them are declared now — constants are free, and having the
/// vocabulary settled means a new resource type is one entry rather than a
/// decision. Only the three that exist today are wired to anything; each of the
/// rest is connected as part of building the feature that introduces it.</para>
///
/// <para>String constants rather than an enum: these are written into a JSON file
/// that survives upgrades, and an enum's ordinals would silently renumber the day
/// someone inserted a value in the middle.</para>
/// </summary>
public static class ResourceKinds
{
    public const string Container = "container";

    public const string App = "app";

    public const string CatalogApp = "catalogApp";

    public const string Environment = "environment";

    public const string Domain = "domain";

    public const string BackupTarget = "backupTarget";

    public const string Stack = "stack";

    public const string Secret = "secret";

    public const string Registry = "registry";

    public const string ScheduledJob = "scheduledJob";

    public const string Database = "database";

    public static readonly string[] All =
    [
        Container, App, CatalogApp, Environment, Domain,
        BackupTarget, Stack, Secret, Registry, ScheduledJob, Database,
    ];

    public static bool IsKnown(string? kind) =>
        kind is not null && Array.Exists(All, known => string.Equals(known, kind, StringComparison.Ordinal));
}

/// <summary>Validation for a team id.</summary>
public static class TeamId
{
    public const int MaximumLength = 32;

    public const int MaximumNameLength = 64;

    public static bool IsValid(string? id) =>
        !string.IsNullOrEmpty(id)
        && id.Length <= MaximumLength
        && char.IsAsciiLetterOrDigit(id[0])
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    public static string Normalize(string? id)
    {
        var value = (id ?? string.Empty).Trim().ToLowerInvariant();
        return IsValid(value)
            ? value
            : throw new ArgumentException(
                $"'{id}' is not a valid team id — use letters, digits and hyphens, starting with a letter or digit.");
    }
}

/// <summary>
/// One team's access to one resource.
///
/// <para>The resource is identified by all three of kind, environment and id.
/// Environment matters as much as the other two: a container called
/// <c>postgres</c> on staging and one on production are different resources, and a
/// grant that ignored which host it meant would hand out production by way of
/// staging. <c>ContainerOwnershipStore</c> already keys ownership the same way, for
/// the same reason.</para>
/// </summary>
public sealed class ResourceGrant
{
    public required string Kind { get; set; }

    public required string EnvironmentId { get; set; }

    public required string ResourceId { get; set; }

    public required string TeamId { get; set; }

    public string Access { get; set; } = GrantAccess.Manage;

    /// <summary>Who granted it, for the audit trail's benefit.</summary>
    public string GrantedBy { get; set; } = string.Empty;

    public DateTimeOffset GrantedAt { get; set; }
}

/// <summary>The file: teams and every grant, together. See <see cref="TeamStore"/>.</summary>
public sealed class TeamDirectory
{
    public List<Team> Teams { get; set; } = [];

    public List<ResourceGrant> Grants { get; set; } = [];
}
