using PinqOps;
using PinqOps.Secrets;

namespace PinqOps.Web;

/// <summary>
/// Named, versioned secrets an operator manages by hand, materialised into each
/// app's <c>.env</c> so a deploy carries them without the runner needing to fetch
/// anything.
///
/// <para>The whole family is admin-only. Reads sit in <c>ApiScopes</c>'
/// admin-read table alongside <c>/api/tokens</c>, which has the useful side effect
/// of putting every one of them in the audit log: the audit middleware records any
/// read the scope table raises above <c>read</c>, so a reveal is written down
/// without a special case. Even listing is admin — the names alone say what
/// credentials this server holds.</para>
/// </summary>
public static class SecretEndpoints
{
    public static void MapSecretEndpoints(this IEndpointRouteBuilder app, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logger);

        app.MapGet("/api/secrets", async Task<object?> (SecretStore secrets, UiConfigStore config) =>
        {
            await Task.CompletedTask;
            return new
            {
                items = secrets.List(),
                // The scopes a secret may be created in, so the picker offers what
                // exists rather than letting someone type an app id that will never
                // match and quietly never materialise.
                scopes = new[] { SecretScopes.Global }
                    .Concat(config.Current.Apps.Select(connection => connection.Id))
                    .ToList(),
                maximumVersions = SecretStore.MaximumVersions,
            };
        });

        app.MapPost("/api/secrets", async Task<object?> (
            HttpContext context, SecretStore secrets, UiConfigStore config, SecretSyncService sync) =>
        {
            var request = await context.Request.ReadFromJsonAsync<SecretWriteRequest>()
                ?? throw new ArgumentException("Invalid request body.");
            var scope = RequireKnownScope(config, request.Scope);
            var actor = Actor(context);
            var now = DateTimeOffset.UtcNow;

            // Rolling back is a write against an existing secret rather than a
            // route of its own: it changes exactly what a new version changes —
            // which value is current — and sharing the route keeps the two from
            // racing each other through separate read-modify-write cycles.
            if (request.UseVersion is { } wanted)
            {
                secrets.UseVersion(scope, request.Name ?? string.Empty, wanted, actor, now);
                logger.LogWarning(
                    "Secret '{Name}' ({Scope}) rolled back to version {Version} by {Actor}",
                    request.Name, scope, wanted, actor);
                return new { ok = true, version = wanted, warnings = sync.Sync() };
            }

            if (string.IsNullOrEmpty(request.Value))
            {
                throw new ArgumentException("A secret value is required.");
            }

            var version = secrets.Set(scope, request.Name ?? string.Empty, request.Value, request.Description, actor, now);
            logger.LogWarning(
                "Secret '{Name}' ({Scope}) set to version {Version} by {Actor}",
                request.Name, scope, version, actor);
            return new { ok = true, version, warnings = sync.Sync() };
        });

        app.MapPost("/api/secrets/{scope}/{name}/rotate", async Task<object?> (
            string scope, string name, HttpContext context, SecretStore secrets, UiConfigStore config, SecretSyncService sync) =>
        {
            var request = await ReadOptionalBody<SecretRotateRequest>(context);
            // Rotation writes through the same Set as creation, which creates when
            // nothing is there — so a scope this does not check is a scope the
            // create route's check can simply be walked around.
            var scoped = RequireKnownScope(config, scope);
            var actor = Actor(context);

            // No value supplied means "give me a new one" — the same generator the
            // catalog installs use, so a rotated secret is as strong as a generated
            // password and is safe to paste into a URL or a -e argument.
            var value = string.IsNullOrEmpty(request?.Value) ? PasswordGenerator.Generate() : request.Value;
            var version = secrets.Set(scoped, name, value, description: null, actor, DateTimeOffset.UtcNow);
            logger.LogWarning("Secret '{Name}' ({Scope}) rotated to version {Version} by {Actor}", name, scoped, version, actor);
            return new { ok = true, version, warnings = sync.Sync() };
        });

        app.MapGet("/api/secrets/{scope}/{name}/reveal", async Task<object?> (
            string scope, string name, HttpContext context, SecretStore secrets) =>
        {
            await Task.CompletedTask;
            var requested = context.Request.Query["version"].ToString();
            int? version = int.TryParse(requested, out var parsed) ? parsed : null;
            var (value, actual) = secrets.Reveal(scope, name, version);
            return new { scope, name, version = actual, value };
        });

        app.MapDelete("/api/secrets/{scope}/{name}", async Task<object?> (
            string scope, string name, HttpContext context, SecretStore secrets, SecretSyncService sync) =>
        {
            await Task.CompletedTask;
            if (!secrets.Remove(scope, name))
            {
                throw new KeyNotFoundException($"No secret named '{name}' in scope '{scope}'.");
            }

            logger.LogWarning("Secret '{Name}' ({Scope}) deleted by {Actor}", name, scope, Actor(context));
            // The name has just left the store, so it is no longer in ManagedNames
            // and the sync would leave the retired value sitting in every .env that
            // held it. Naming it here is what actually withdraws the credential.
            return new { ok = true, warnings = sync.Sync([SecretName.Normalize(name)]) };
        });
    }

    /// <summary>
    /// The scope, checked against what exists. A secret filed under an app id
    /// nobody has connected would be stored, listed and revealed while never
    /// reaching a container — a credential that looks deployed and is not.
    /// </summary>
    private static string RequireKnownScope(UiConfigStore config, string? scope)
    {
        var normalized = SecretScopes.Normalize(scope);
        if (string.Equals(normalized, SecretScopes.Global, StringComparison.Ordinal))
        {
            return normalized;
        }

        var known = config.Current.Apps.Exists(connection =>
            string.Equals(connection.Id, normalized, StringComparison.OrdinalIgnoreCase));

        return known
            ? normalized
            : throw new ArgumentException($"'{scope}' is not a connected app, so a secret cannot be scoped to it.");
    }

    /// <summary>The principal performing the write, as the audit trail names it.</summary>
    private static string Actor(HttpContext context) => context.Items["user"] as string ?? AuditLog.Anonymous;

    /// <summary>
    /// Reads a body that may legitimately be absent — rotating with no payload
    /// means "generate a value", and a caller expressing that as an empty request
    /// should not get "invalid request body".
    /// </summary>
    private static async Task<T?> ReadOptionalBody<T>(HttpContext context)
        where T : class
    {
        if (context.Request.ContentLength is null or 0)
        {
            return null;
        }

        return await context.Request.ReadFromJsonAsync<T>();
    }
}

/// <summary>Create a secret, add a version, or roll back to an earlier one.</summary>
public sealed class SecretWriteRequest
{
    public string? Scope { get; set; }

    public string? Name { get; set; }

    public string? Value { get; set; }

    public string? Description { get; set; }

    /// <summary>When set, points the secret at an existing version instead of adding one.</summary>
    public int? UseVersion { get; set; }
}

/// <summary>Rotate a secret. A missing value means "generate one".</summary>
public sealed class SecretRotateRequest
{
    public string? Value { get; set; }
}
