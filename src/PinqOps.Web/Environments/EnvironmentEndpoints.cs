using PinqOps;
using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>
/// The docker hosts this dashboard manages, over SSH or the local daemon.
/// </summary>
public static class EnvironmentEndpoints
{
    public static void MapEnvironmentEndpoints(this IEndpointRouteBuilder app, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logger);

        // Readable by anyone signed in — the switcher needs somewhere to send requests —
        // but the SSH details are the reconnaissance half of reaching those hosts, so
        // only an admin sees them. The keys are never returned to anyone.
        app.MapGet("/api/environments", async Task<object?> (
            HttpContext context, EnvironmentService environments, ResourceVisibility visibility) =>
        {
            await Task.CompletedTask;
            var isAdmin = (context.Items["scope"] as string) == "admin";
            // A host nobody has claimed stays listed for everyone, as it always was;
            // one granted to a team is offered only to that team. This is the list
            // the topbar's server switcher is built from, so it decides which hosts
            // a person is even shown.
            //
            // Keyed on the fixed environment id, not the one `?env=` selected: a
            // grant on an environment is written against that key — it is what the
            // middleware's own addressability check looks it up by — so measuring
            // these rows against the selected host found no grant at all, and every
            // host another team holds came back as unclaimed to anyone who named
            // one of their own.
            var visible = visibility.Visible(
                context,
                ResourceKinds.Environment,
                environments.All(),
                environment => environment.Id,
                ManagedEnvironment.LocalId);

            return new
            {
                items = visible.Select(environment => new
                {
                    environment.Id,
                    environment.Name,
                    environment.Transport,
                    environment.ReadOnly,
                    isLocal = environment.IsLocal,
                    host = isAdmin ? environment.Host : null,
                    user = isAdmin ? environment.User : null,
                    port = isAdmin && !environment.IsLocal ? environment.Port : (int?)null,
                    hasKey = !string.IsNullOrEmpty(environment.PrivateKey),
                    hostKeyPinned = !string.IsNullOrEmpty(environment.HostKey),
                }),
            };
        });

        app.MapPost("/api/environments", async Task<object?> (HttpContext context, EnvironmentService environments) =>
        {
            var request = await context.Request.ReadFromJsonAsync<EnvironmentRequest>()
                ?? throw new ArgumentException("An environment is required.");

            var environment = new ManagedEnvironment
            {
                Id = (request.Id ?? string.Empty).Trim().ToLowerInvariant(),
                Name = (request.Name ?? string.Empty).Trim(),
                Transport = string.IsNullOrWhiteSpace(request.Transport)
                    ? ManagedEnvironment.TransportSsh
                    : request.Transport.Trim().ToLowerInvariant(),
                Host = request.Host?.Trim(),
                User = request.User?.Trim(),
                Port = request.Port is > 0 ? request.Port.Value : 22,
                PrivateKey = string.IsNullOrWhiteSpace(request.PrivateKey) ? null : request.PrivateKey,
                HostKey = string.IsNullOrWhiteSpace(request.HostKey) ? null : request.HostKey.Trim(),
                ReadOnly = request.ReadOnly ?? false,
            };

            if (string.Equals(environment.Id, ManagedEnvironment.LocalId, StringComparison.OrdinalIgnoreCase)
                && !environment.IsLocal)
            {
                throw new ArgumentException($"'{ManagedEnvironment.LocalId}' is reserved for this server.");
            }

            environments.Save(environment);
            logger.LogWarning("Environment '{Id}' saved ({Transport})", environment.Id, environment.Transport);
            return new { ok = true, id = environment.Id };
        });

        app.MapDelete("/api/environments/{id}", async Task<object?> (
            string id, EnvironmentService environments, TeamStore teams, ContainerOwnershipStore ownership) =>
        {
            await Task.CompletedTask;
            environments.Remove(id);

            // What was recorded against this host goes with it, the same way a team's
            // grants go with the team. Left behind, they name whatever is registered
            // under this id next — a rebuild, a replacement server, the same obvious
            // name reused — and hand the new machine to whoever held the old one.
            var grants = teams.RemoveEnvironment(id);
            var owners = ownership.RemoveEnvironment(id);
            logger.LogWarning(
                "Environment '{Id}' removed with {Grants} grant(s) and {Owners} ownership record(s)",
                id, grants, owners);
            return new { ok = true };
        });

        // Runs `docker version` against the environment, which is the cheapest call that
        // proves the whole path works: SSH auth, the pinned host key, and a daemon that
        // answers.
        app.MapPost("/api/environments/{id}/test", async Task<object?> (string id, EnvironmentService environments, DockerService docker) =>
        {
            // Same answer the environment middleware gives for an id that names
            // nothing, rather than the 400 a bare Resolve would surface.
            var environment = environments.Find(id)
                ?? throw new KeyNotFoundException($"Unknown environment '{id}'.");
            var version = await docker.For(DockerEndpoint.For(environment)).VersionAsync();

            // `docker version` exits non-zero AND still prints client-only JSON when the
            // daemon cannot be reached, and VersionAsync turns a non-zero exit into null
            // rather than throwing — so returning ok:true unconditionally reported
            // "connected" for every failure this button exists to detect: a wrong key, a
            // host-key mismatch, no docker on the far side. The check is here rather
            // than in VersionAsync because /api/docker/version deliberately relies on
            // the null form to render "daemon unreachable" on the System view.
            var serverReached = version is { } payload
                && payload.TryGetProperty("Server", out var server)
                && server.ValueKind is not System.Text.Json.JsonValueKind.Null;
            if (!serverReached)
            {
                throw new InvalidOperationException(
                    "docker could not reach the daemon for this environment — check the private key, the pinned "
                    + "host key, and that docker is installed and reachable there.");
            }

            return new { ok = true, version };
        });
    }
}
