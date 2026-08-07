using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>Where a governed route gets the id of the resource it acts on.</summary>
public enum ResourceIdSource
{
    /// <summary>The route's <c>{id}</c> value, verbatim.</summary>
    RouteId,

    /// <summary>The route's <c>{id}</c> is a catalog app id; the resource is the
    /// container it runs under, e.g. <c>/api/apps/{id}/uninstall</c>.</summary>
    CatalogAppRouteId,
}

/// <summary>
/// Marks a route as governed, and says what it acts on.
///
/// <para>Declared on the route itself rather than recovered by re-matching the
/// path, so renaming a route can no longer silently drop its gate — and so a test
/// can enumerate exactly which routes are governed and at what level.</para>
/// </summary>
public sealed record ResourceGateMetadata(string Kind, ResourceIdSource Source, string Access);

public static class ResourceGateExtensions
{
    /// <summary>
    /// Requires the caller to be allowed to act on the resource this route targets:
    /// an admin always, the personal owner of a container, or a member of a team the
    /// resource has been granted to. See <see cref="ResourceAccess"/>.
    ///
    /// <para>The kind and access level are validated <em>here</em>, at map time, so
    /// a typo is a startup failure rather than a route that quietly governs nothing
    /// and is only noticed when someone reaches something they should not.</para>
    /// </summary>
    public static RouteHandlerBuilder RequireResourceAccess(
        this RouteHandlerBuilder builder,
        string kind,
        ResourceIdSource source,
        string access = GrantAccess.Manage)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!ResourceKinds.IsKnown(kind))
        {
            throw new ArgumentException($"'{kind}' is not a known resource kind.", nameof(kind));
        }

        if (!GrantAccess.IsValid(access))
        {
            throw new ArgumentException($"'{access}' is not a valid access level.", nameof(access));
        }

        builder.WithMetadata(new ResourceGateMetadata(kind, source, access));
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;

            // Fail closed at every branch: a governed route whose resource cannot be
            // identified, or whose caller cannot be, is refused rather than run.
            var resourceId = Resolve(http, source);
            if (resourceId is null)
            {
                return Refused();
            }

            var scope = http.Items["scope"] as string;
            var user = http.Items["user"] as string;
            if (string.IsNullOrEmpty(scope) || string.IsNullOrEmpty(user))
            {
                return Refused();
            }

            // Scoped to the environment the request selected, so a record or a grant
            // on one host never governs a same-named resource on another.
            var environmentId = EnvId(http);
            var teams = http.RequestServices.GetRequiredService<TeamStore>();

            var ownership = string.Equals(kind, ResourceKinds.Container, StringComparison.Ordinal)
                ? http.RequestServices.GetRequiredService<ContainerOwnershipStore>().Get(environmentId, resourceId)
                : null;

            var allowed = ResourceAccess.CanAccess(
                scope,
                user,
                kind,
                ownership,
                teams.TeamsOf(user),
                teams.GrantsFor(kind, environmentId, resourceId),
                access);

            return allowed ? await next(context) : Refused();
        });

        return builder;
    }

    /// <summary>
    /// One message for every refusal here, deliberately. Saying <em>why</em> — that
    /// the resource exists but is someone else's, or that it does not exist at all —
    /// would let a caller map the host by probing.
    /// </summary>
    private static IResult Refused() => Error(403, "You do not manage this resource.");

    private static string? Resolve(HttpContext context, ResourceIdSource source)
    {
        if (context.Request.RouteValues["id"] is not string id || id.Length == 0)
        {
            return null;
        }

        return source == ResourceIdSource.CatalogAppRouteId ? ProtectedResource.ContainerForApp(id) : id;
    }
}
