using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>
/// Teams and the grants that give them resources.
///
/// <para><b>Every write is admin.</b> They fall there by the scope table's default
/// and stay there deliberately: granting is how access is widened, so a principal
/// that is not an admin must not be able to grant itself anything — including a
/// deployer, and including an API token whose scope would otherwise let it write.</para>
///
/// <para><b>Reads are filtered rather than blocked.</b> The dashboard needs to know
/// which teams the signed-in user is in to decide what to offer, so these stay at
/// the read scope and the handlers strip what the caller has no business seeing —
/// the same stance <c>/api/docker/ownership</c> already takes for other people's
/// ownership records.</para>
/// </summary>
public static class TeamEndpoints
{
    public static void MapTeamEndpoints(this IEndpointRouteBuilder app, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logger);

        app.MapGet("/api/teams", async Task<object?> (HttpContext context, TeamStore teams) =>
        {
            await Task.CompletedTask;
            var visible = IsAdmin(context)
                ? teams.Teams
                : [.. teams.Teams.Where(team => teams.TeamsOf(Actor(context))
                    .Contains(team.Id, StringComparer.OrdinalIgnoreCase))];

            return new
            {
                items = visible.Select(team => new
                {
                    team.Id,
                    team.Name,
                    team.CreatedAt,
                    members = team.Members.Select(member => new { member.Principal, member.Role }),
                }),
                kinds = ResourceKinds.All,
            };
        });

        app.MapPost("/api/teams", async Task<object?> (HttpContext context, TeamStore teams) =>
        {
            var request = await context.Request.ReadFromJsonAsync<TeamWriteRequest>()
                ?? throw new ArgumentException("Invalid request body.");
            var id = TeamId.Normalize(request.Id);
            var name = RequireName(request.Name, id);

            teams.Update<object?>(directory =>
            {
                var existing = directory.Teams.Find(team =>
                    string.Equals(team.Id, id, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    existing.Name = name;
                    return null;
                }

                directory.Teams.Add(new Team { Id = id, Name = name, CreatedAt = DateTimeOffset.UtcNow });
                return null;
            });

            logger.LogWarning("Team '{Team}' saved by {Actor}", id, Actor(context));
            return new { ok = true, id };
        });

        app.MapDelete("/api/teams/{id}", async Task<object?> (string id, HttpContext context, TeamStore teams) =>
        {
            await Task.CompletedTask;
            if (!teams.RemoveTeam(id))
            {
                throw new KeyNotFoundException($"There is no team called '{id}'.");
            }

            // The grants went with it, in the same write — see TeamStore.
            logger.LogWarning("Team '{Team}' and its grants deleted by {Actor}", id, Actor(context));
            return new { ok = true };
        });

        app.MapPost("/api/teams/{id}/members", async Task<object?> (
            string id, HttpContext context, TeamStore teams) =>
        {
            var request = await context.Request.ReadFromJsonAsync<TeamMemberRequest>()
                ?? throw new ArgumentException("Invalid request body.");
            var teamId = TeamId.Normalize(id);
            var principal = RequirePrincipal(request.Principal);
            var role = TeamRoles.Normalize(request.Role);

            teams.Update<object?>(directory =>
            {
                var team = RequireTeam(directory, teamId);
                var existing = team.Members.Find(member =>
                    string.Equals(member.Principal, principal, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    existing.Role = role;
                    return null;
                }

                team.Members.Add(new TeamMember { Principal = principal, Role = role });
                return null;
            });

            logger.LogWarning("'{Principal}' added to team '{Team}' as {Role} by {Actor}",
                principal, teamId, role, Actor(context));
            return new { ok = true };
        });

        app.MapDelete("/api/teams/{id}/members/{principal}", async Task<object?> (
            string id, string principal, HttpContext context, TeamStore teams) =>
        {
            await Task.CompletedTask;
            var teamId = TeamId.Normalize(id);

            var removed = teams.Update(directory =>
                RequireTeam(directory, teamId).Members.RemoveAll(member =>
                    string.Equals(member.Principal, principal, StringComparison.OrdinalIgnoreCase)));

            if (removed == 0)
            {
                throw new KeyNotFoundException($"'{principal}' is not in team '{teamId}'.");
            }

            logger.LogWarning("'{Principal}' removed from team '{Team}' by {Actor}", principal, teamId, Actor(context));
            return new { ok = true };
        });

        app.MapGet("/api/grants", async Task<object?> (HttpContext context, TeamStore teams) =>
        {
            await Task.CompletedTask;
            var kind = context.Request.Query["kind"].ToString();
            var environmentId = context.Request.Query["environmentId"].ToString();
            var resourceId = context.Request.Query["resourceId"].ToString();

            var mine = teams.TeamsOf(Actor(context));
            var items = teams.Grants
                .Where(grant => kind.Length == 0 || string.Equals(grant.Kind, kind, StringComparison.OrdinalIgnoreCase))
                .Where(grant => environmentId.Length == 0
                    || string.Equals(grant.EnvironmentId, environmentId, StringComparison.OrdinalIgnoreCase))
                .Where(grant => resourceId.Length == 0
                    || string.Equals(grant.ResourceId, resourceId, StringComparison.Ordinal))
                // A non-admin learns which resources its own teams hold, and nothing
                // about anyone else's.
                .Where(grant => IsAdmin(context) || mine.Contains(grant.TeamId, StringComparer.OrdinalIgnoreCase))
                .Select(grant => new
                {
                    grant.Kind,
                    grant.EnvironmentId,
                    grant.ResourceId,
                    grant.TeamId,
                    grant.Access,
                    grant.GrantedBy,
                    grant.GrantedAt,
                });

            return new { items };
        });

        app.MapPost("/api/grants", async Task<object?> (HttpContext context, TeamStore teams) =>
        {
            var request = await context.Request.ReadFromJsonAsync<GrantWriteRequest>()
                ?? throw new ArgumentException("Invalid request body.");
            var grant = ValidatedGrant(request, Actor(context));

            teams.Update<object?>(directory =>
            {
                if (!directory.Teams.Exists(team =>
                    string.Equals(team.Id, grant.TeamId, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new KeyNotFoundException($"There is no team called '{grant.TeamId}'.");
                }

                // One grant per team per resource: re-granting changes the level
                // rather than stacking two entries whose combination nobody reads.
                directory.Grants.RemoveAll(existing => SameResource(existing, grant));
                directory.Grants.Add(grant);
                return null;
            });

            logger.LogWarning(
                "{Kind} '{Resource}' on {Environment} granted to team '{Team}' ({Access}) by {Actor}",
                grant.Kind, grant.ResourceId, grant.EnvironmentId, grant.TeamId, grant.Access, grant.GrantedBy);
            return new { ok = true };
        });

        // Identified by query rather than by path: the resource takes four values to
        // name, and a DELETE body is not something every client sends.
        //
        // The host the grant is recorded against is named `environmentId`, matching the
        // field the create side takes in its body — and deliberately not `env`, which
        // the environment middleware reads as "aim this request at that host". Sharing
        // the name meant revoking a grant recorded against a read-only host was refused
        // as a change to that host, and one recorded against a host since de-registered
        // was refused as unknown: a grant that could be created and never taken back.
        app.MapDelete("/api/grants", async Task<object?> (HttpContext context, TeamStore teams) =>
        {
            await Task.CompletedTask;
            var target = ValidatedGrant(
                new GrantWriteRequest
                {
                    Kind = context.Request.Query["kind"],
                    EnvironmentId = context.Request.Query["environmentId"],
                    ResourceId = context.Request.Query["resourceId"],
                    TeamId = context.Request.Query["teamId"],
                },
                Actor(context));

            var removed = teams.Update(directory => directory.Grants.RemoveAll(grant => SameResource(grant, target)));
            if (removed == 0)
            {
                throw new KeyNotFoundException("There is no such grant.");
            }

            logger.LogWarning(
                "{Kind} '{Resource}' on {Environment} revoked from team '{Team}' by {Actor}",
                target.Kind, target.ResourceId, target.EnvironmentId, target.TeamId, Actor(context));
            return new { ok = true };
        });
    }

    private static bool IsAdmin(HttpContext context) => context.Items["scope"] as string == "admin";

    private static string Actor(HttpContext context) => context.Items["user"] as string ?? AuditLog.Anonymous;

    private static bool SameResource(ResourceGrant left, ResourceGrant right) =>
        string.Equals(left.Kind, right.Kind, StringComparison.Ordinal)
        && string.Equals(left.EnvironmentId, right.EnvironmentId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.ResourceId, right.ResourceId, StringComparison.Ordinal)
        && string.Equals(left.TeamId, right.TeamId, StringComparison.OrdinalIgnoreCase);

    private static Team RequireTeam(TeamDirectory directory, string teamId) =>
        directory.Teams.Find(team => string.Equals(team.Id, teamId, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"There is no team called '{teamId}'.");

    private static string RequireName(string? name, string fallback)
    {
        var value = (name ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return fallback;
        }

        return value.Length <= TeamId.MaximumNameLength
            ? value
            : throw new ArgumentException($"A team name may be at most {TeamId.MaximumNameLength} characters.");
    }

    /// <summary>
    /// A member is either a user account or an API token principal. Validated the
    /// same way <c>UserEndpoints</c> validates a username, plus the
    /// <c>token:&lt;id&gt;</c> form — which no account can collide with, because the
    /// username rule excludes ':'.
    /// </summary>
    private static string RequirePrincipal(string? principal)
    {
        var value = (principal ?? string.Empty).Trim();
        if (ApiTokenStore.IsTokenPrincipal(value) && value.Length > ApiTokenStore.PrincipalPrefix.Length)
        {
            return value;
        }

        var valid = value.Length >= 2
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

        return valid
            ? value
            : throw new ArgumentException($"'{principal}' is not a user name or an API token principal.");
    }

    private static ResourceGrant ValidatedGrant(GrantWriteRequest request, string actor)
    {
        var kind = (request.Kind ?? string.Empty).Trim();
        if (!ResourceKinds.IsKnown(kind))
        {
            throw new ArgumentException($"'{request.Kind}' is not a kind of resource pinqops grants access to.");
        }

        var resourceId = (request.ResourceId ?? string.Empty).Trim();
        if (resourceId.Length == 0 || resourceId.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException("A grant needs the id of the resource it applies to.");
        }

        var environmentId = (request.EnvironmentId ?? string.Empty).Trim();
        if (environmentId.Length == 0)
        {
            // A grant that named no host would apply to whichever one happened to be
            // asked about, which is how staging becomes production.
            environmentId = ManagedEnvironment.LocalId;
        }

        if (!ManagedEnvironment.IsValidId(environmentId))
        {
            throw new ArgumentException($"'{environmentId}' is not a valid environment id.");
        }

        return new ResourceGrant
        {
            Kind = kind,
            EnvironmentId = environmentId.ToLowerInvariant(),
            ResourceId = resourceId,
            TeamId = TeamId.Normalize(request.TeamId),
            Access = GrantAccess.Normalize(request.Access),
            GrantedBy = actor,
            GrantedAt = DateTimeOffset.UtcNow,
        };
    }
}

/// <summary>Create or rename a team.</summary>
public sealed class TeamWriteRequest
{
    public string? Id { get; set; }

    public string? Name { get; set; }
}

/// <summary>Add a principal to a team, or change its role there.</summary>
public sealed class TeamMemberRequest
{
    public string? Principal { get; set; }

    public string? Role { get; set; }
}

/// <summary>Grant or revoke one team's access to one resource.</summary>
public sealed class GrantWriteRequest
{
    public string? Kind { get; set; }

    public string? EnvironmentId { get; set; }

    public string? ResourceId { get; set; }

    public string? TeamId { get; set; }

    public string? Access { get; set; }
}
