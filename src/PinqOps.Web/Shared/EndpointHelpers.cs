using System.Globalization;
using PinqOps;
using static System.Globalization.CultureInfo;

namespace PinqOps.Web;

/// <summary>
/// The request helpers the endpoint modules share. These were static local
/// functions in <c>Program.cs</c>; moving them to one shared type lets every
/// feature module reach them via <c>using static PinqOps.Web.EndpointHelpers;</c>
/// without each copy drifting from the others. <c>Safe()</c> is deliberately not
/// here — it captures the logger, so it travels to the modules as a delegate.
/// </summary>
public static class EndpointHelpers
{
    // Binds a docker call to the environment the request selected. The environment
    // middleware has already resolved and validated it, so this cannot address a host
    // the caller was not allowed to name; a request that named none gets the local
    // daemon, which is what every background worker uses too.
    // The environment a request selected, as an id. Resolved by the environment
    // middleware; anything that runs before it (or outside a request) is local.
    public static string EnvId(HttpContext context) =>
        (context.Items["environment"] as ManagedEnvironment)?.Id ?? ManagedEnvironment.LocalId;

    // Always routed through DockerEndpoint.For, which preserves the environment's
    // own id even for a local transport — short-circuiting to the DI default here
    // gave such an environment a DockerService whose EnvironmentId was "local"
    // while every gate and store keyed by EnvId(context) used the environment's id,
    // splitting the ownership/credential/job key space for the same host.
    public static DockerService Env(HttpContext context, DockerService docker) =>
        docker.For(EnvEndpoint(context));

    // The daemon a request is addressed to, for the callers that own a process
    // themselves instead of going through DockerService — the container console.
    // It reads the same context item EnvId does, so what the resource gate
    // authorized and what the command runs against cannot come apart.
    public static DockerEndpoint EnvEndpoint(HttpContext context) =>
        context.Items["environment"] is ManagedEnvironment environment
            ? DockerEndpoint.For(environment)
            : DockerEndpoint.Local;

    // The audit "target" defaults to the ?appId scope, but container/docker routes
    // identify the resource in the path (/api/docker/containers/<id>/...). Surface
    // that id so destructive actions (kill/remove/exec) record which container.
    public static string AuditTarget(HttpContext context)
    {
        // Literals are compared case-insensitively, the way routing matched them, so
        // a request to /api/DOCKER/... is still recorded against its container.
        var segments = context.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is { Length: >= 4 }
            && segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("docker", StringComparison.OrdinalIgnoreCase)
            && segments[2].Equals("containers", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.UnescapeDataString(segments[3]);
        }

        return context.Request.Query["appId"].ToString();
    }

    public static IResult Error(int statusCode, string message) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    public static string ClientKey(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    // Guards the user- and audit-management endpoints: a GET falls to "read" scope
    // in the middleware, so admin-only reads (the user list, the audit trail) are
    // enforced here. Writes are already admin-gated by the scope middleware.
    public static void RequireAdmin(HttpContext context)
    {
        if (context.Items["scope"] as string != "admin")
        {
            throw new UnauthorizedAccessException("This action requires the admin role.");
        }
    }

    /// <summary>First published host port from docker ps's Ports column, e.g.
    /// "0.0.0.0:3005-&gt;3000/tcp, :::3005-&gt;3000/tcp" → 3005.</summary>
    public static int? ParseFirstHostPort(string portsColumn)
    {
        var match = System.Text.RegularExpressions.Regex.Match(portsColumn ?? "", @":(\d+)->");
        return match.Success && int.TryParse(match.Groups[1].Value, out var port) ? port : null;
    }

    /// <summary>
    /// True when every published port binds loopback, i.e. the app is reachable from
    /// this host only. `docker ps` renders the bind address in the Ports column
    /// (<c>127.0.0.1:6379-&gt;6379/tcp</c>) whenever there is one, so an entry without
    /// a loopback prefix is bound on every interface. Used to stop the UI offering an
    /// "open" link that cannot work from anywhere but the server itself.
    /// </summary>
    public static bool IsLoopbackOnly(string portsColumn)
    {
        var published = (portsColumn ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry => entry.Contains("->", StringComparison.Ordinal))
            .ToList();

        return published.Count > 0
            && published.TrueForAll(entry =>
                entry.StartsWith("127.0.0.1:", StringComparison.Ordinal)
                || entry.StartsWith("[::1]:", StringComparison.Ordinal));
    }

    /// <summary>Lowercases and reduces a name to a safe id fragment ([a-z0-9._-]).</summary>
    public static string Slugify(string value)
    {
        var kept = value.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '-')
            .ToArray();
        var slug = new string(kept).Trim('-', '.', '_');
        return slug.Length > 0 ? slug : "target";
    }

    /// <summary>The container name and port a domain target resolves to. Target is
    /// an app id (→ that app's compose container) or "catalog:&lt;id&gt;" (→ a catalog
    /// container). An optional requested port overrides the default.</summary>
    public static (string Container, int Port) ResolveDomainTarget(UiConfig config, string target, int? requestedPort)
    {
        // Every other port input in the codebase is range-checked (SetupPorts,
        // TryParsePort, PreviewManager.ParsePort). Taking this one verbatim let a 0 or a
        // negative value be persisted, which CaddyfileGenerator then silently skips —
        // producing a domain the dashboard lists as enabled and Caddy never routes.
        if (requestedPort is { } wanted && !HostPort.IsValid(wanted))
        {
            throw new ArgumentException($"'{wanted}' is not a valid container port (1-65535).");
        }

        if (target.StartsWith("catalog:", StringComparison.Ordinal))
        {
            var id = target["catalog:".Length..];
            var spec = AppCatalog.Find(id) ?? throw new ArgumentException($"Unknown app '{id}'.");
            var catalogPort = requestedPort
                ?? (spec.Ports.Length > 0 ? spec.Ports[0].Container : throw new ArgumentException("This app exposes no port to route to."));
            // spec.Id, not the caller's casing: AppCatalog.Find matches ids
            // case-insensitively but docker container names are case-sensitive, so
            // "catalog:REDIS" used to derive the container "pinqops-REDIS" — a route that
            // could never be served, and which drift detection reported as healthy.
            return ($"{AppCatalog.ContainerPrefix}{spec.Id}", catalogPort);
        }

        var connection = config.Apps.FirstOrDefault(a => string.Equals(a.Id, target, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown app '{target}'.");
        var repository = GitHubRepositoryParser.Parse(connection.RepoUrl);
        var container = $"{ComposeProjectName.FromRepository(repository.Name)}-app-1";
        var envPort = TryParsePort(
            EnvFileStore.GetValue(PinqOpsStatePaths.EnvFile(connection.ComposeFile), SetupTemplates.ContainerPortVariable));
        return (container, requestedPort ?? envPort ?? DockerfileInspector.DefaultPort);
    }

    /// <summary>Whether <paramref name="path"/> is a build manifest directly in the
    /// directory identified by <paramref name="prefix"/> (no deeper).</summary>
    public static bool IsDirectManifest(string path, string prefix)
    {
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var name = path[prefix.Length..];
        if (name.Contains('/'))
        {
            return false;
        }

        return name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || name is "package.json" or "go.mod" or "Cargo.toml"
                or "requirements.txt" or "pyproject.toml" or "composer.json" or "Gemfile";
    }

    /// <summary>
    /// The app a request targets: <c>?appId=…</c> or the sole/first app the caller
    /// can see.
    ///
    /// <para>The visibility rule is fetched from the request rather than passed in,
    /// so the dozens of call sites stay unchanged — and so none of them can forget
    /// it. <c>GetService</c> rather than <c>GetRequiredService</c>: a caller
    /// assembling a bare context outside the app gets the unfiltered behaviour
    /// instead of an exception.</para>
    /// </summary>
    public static AppConnection ResolveApp(UiConfigStore store, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return ResolveApp(store, context, context.Request.Query["appId"].ToString());
    }

    /// <summary>
    /// The same, for a route that names the app in its path rather than its query.
    ///
    /// <para>Such a route used to reach past this helper to
    /// <see cref="AppResolver.Resolve"/>, whose visibility rule is optional — so
    /// omitting it compiled, read like every other call, and quietly kept the
    /// behaviour from before teams existed.</para>
    /// </summary>
    public static AppConnection ResolveApp(UiConfigStore store, HttpContext context, string? appId)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(context);

        var visibility = context.RequestServices?.GetService<ResourceVisibility>();
        return AppResolver.Resolve(
            store.Current,
            appId,
            visibility is null ? null : app => visibility.CanView(context, ResourceKinds.App, app.Id));
    }

    /// <summary>A stored .env port value as an int, or null when absent/garbage.</summary>
    public static int? TryParsePort(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.None, InvariantCulture, out var port) && HostPort.IsValid(port)
            ? port
            : null;

    public static string? ReadBearerToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return header[7..];
        }

        // A browser cannot set Authorization on a WebSocket handshake, and the token
        // must not go in the query string. Falling back to the subprotocol list here
        // — rather than in a second authorization path — is what lets a WebSocket
        // route reuse the scope table, the policies and the audit line unchanged.
        return WebSocketChannel.TokenFrom(context.Request);
    }
}
