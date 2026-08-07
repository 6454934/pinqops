using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing.Patterns;

namespace PinqOps.Web;

/// <summary>
/// The dashboard's request authorization, expressed as ASP.NET Core authorization
/// policies instead of hand-rolled checks.
///
/// Three things make it fail-closed:
/// <list type="number">
/// <item>every <c>/api</c> route is given an explicit scope policy at startup, so
/// nothing runs unauthorized;</item>
/// <item>the fallback policy denies anything that somehow declared none — a new
/// route left unclassified is refused, not served;</item>
/// <item>the required scope is still <see cref="ApiScopes.RequiredFor"/>, computed
/// once per route from its template, so this is the same authorization the old
/// per-request middleware enforced — only where and how it runs has changed.</item>
/// </list>
/// </summary>
public static class ApiAuthorization
{
    /// <summary>The claim a resolved principal carries its API scope in.</summary>
    public const string ScopeClaim = "pinq:scope";

    public const string ReadPolicy = "scope:read";
    public const string DeployPolicy = "scope:deploy";
    public const string AdminPolicy = "scope:admin";

    /// <summary>
    /// The only routes reachable without a session: the lock screen's state probe
    /// and the two handshake posts. Everything else is authorized. Matched by
    /// (method, template) so a stray same-path route of another method is not
    /// accidentally opened.
    /// </summary>
    private static readonly HashSet<(string Method, string Path)> AnonymousRoutes = new()
    {
        ("GET", "/api/auth/state"),
        ("POST", "/api/auth/setup"),
        ("POST", "/api/auth/login"),
        // The second step of a login is by definition not signed in yet. What it
        // accepts is not a credential of its own: a challenge is minted only by a
        // correct password, expires in minutes, resolves to a username and nothing
        // else, and the same throttle counts against the same account bucket.
        ("POST", "/api/auth/login/2fa"),
        // Accepting an invitation happens before the account exists, so it cannot
        // be behind one. What it takes is a link with 32 random bytes in it that
        // works once, and the two routes answer the same way for expired, withdrawn,
        // already used and never existed.
        ("GET", "/api/auth/invite"),
        ("POST", "/api/auth/invite/accept"),
    };

    private static string PolicyFor(string scope) => scope switch
    {
        "admin" => AdminPolicy,
        "deploy" => DeployPolicy,
        _ => ReadPolicy,
    };

    public static void AddPinqOpsAuthorization(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            options.AddPolicy(ReadPolicy, RequireScope("read"));
            options.AddPolicy(DeployPolicy, RequireScope("deploy"));
            options.AddPolicy(AdminPolicy, RequireScope("admin"));

            // Fail closed: an endpoint that declared no policy is refused rather
            // than served. Every /api route is given one below, so this only ever
            // catches a route someone forgot to classify — which must not default
            // to open.
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAssertion(_ => false)
                .Build();
        });

        // Write the 401/403 as the same JSON body the dashboard already expects,
        // and keep the unauthenticated-vs-forbidden distinction (a missing or bad
        // token is a 401; a valid token of too low a scope is a 403).
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, JsonAuthorizationResultHandler>();
    }

    private static Action<AuthorizationPolicyBuilder> RequireScope(string scope) => builder =>
        builder
            .RequireAuthenticatedUser()
            .RequireAssertion(context =>
                context.User.FindFirst(ScopeClaim) is { Value: { } have } && ApiScopes.Satisfies(have, scope));

    /// <summary>
    /// Resolves the caller once, before authorization runs: an API token or a
    /// session becomes an authenticated principal carrying its scope, and the
    /// scope/user land in <c>HttpContext.Items</c> where the audit line and the
    /// handlers already read them. A bad or missing token leaves the request
    /// unauthenticated, and the authorization layer answers 401.
    /// </summary>
    public static IApplicationBuilder UsePinqOpsScopeResolution(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api")
                || context.Request.Path.StartsWithSegments("/mcp"))
            {
                var token = EndpointHelpers.ReadBearerToken(context);
                string? scope = null;
                string? user = null;

                if (token is { } bearer && ApiTokenStore.LooksLikeToken(bearer))
                {
                    var tokens = context.RequestServices.GetRequiredService<ApiTokenStore>();
                    var apiToken = tokens.Authenticate(bearer, DateTimeOffset.UtcNow);
                    if (apiToken is not null)
                    {
                        scope = apiToken.Scope;
                        // Each token is its own principal, so an ownership record
                        // written by one token is not manageable by every other.
                        user = ApiTokenStore.PrincipalFor(apiToken);
                    }
                }
                else if (token is { } sessionToken)
                {
                    var sessions = context.RequestServices.GetRequiredService<SessionStore>();
                    if (sessions.Resolve(sessionToken) is { } principal)
                    {
                        scope = UserRoles.ScopeFor(principal.Role);
                        user = principal.Username;
                    }
                }

                if (scope is not null && user is not null)
                {
                    var identity = new ClaimsIdentity("PinqOps");
                    identity.AddClaim(new Claim(ScopeClaim, scope));
                    identity.AddClaim(new Claim(ClaimTypes.Name, user));
                    context.User = new ClaimsPrincipal(identity);
                    context.Items["scope"] = scope;
                    context.Items["user"] = user;
                }
            }

            await next();
        });

    /// <summary>
    /// The startup convention that gives every <c>/api</c> endpoint its scope
    /// policy (or marks the three handshake routes anonymous). Applied to the whole
    /// API group so a new endpoint cannot be added without being classified here.
    /// </summary>
    public static void ApplyScopePolicy(EndpointBuilder builder)
    {
        if (builder is not RouteEndpointBuilder route)
        {
            return;
        }

        var template = route.RoutePattern.RawText ?? string.Empty;
        if (!template.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var method = builder.Metadata.OfType<HttpMethodMetadata>().FirstOrDefault()?.HttpMethods.FirstOrDefault()
            ?? "GET";

        if (AnonymousRoutes.Contains((method.ToUpperInvariant(), template)))
        {
            builder.Metadata.Add(new AllowAnonymousAttribute());
            return;
        }

        var scope = ApiScopes.RequiredFor(method, template);
        builder.Metadata.Add(new AuthorizeAttribute(PolicyFor(scope)));
    }
}

/// <summary>
/// Renders an authorization failure as the dashboard's <c>{ error }</c> JSON with
/// the right status: 401 when the caller is not authenticated, 403 when they are
/// but their scope is too low. Replaces the framework default, which would empty
/// the body and needs an authentication scheme to challenge.
/// </summary>
public sealed class JsonAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        if (authorizeResult.Challenged)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized." });
            return;
        }

        if (authorizeResult.Forbidden)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Your role or token does not grant the scope this action requires.",
            });
            return;
        }

        await next(context);
    }
}
