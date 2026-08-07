using System.Globalization;
using static System.Globalization.CultureInfo;
using PinqOps;
using PinqOps.Proxy;
using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>
/// The compose project view and its <c>.env</c> editor (with the apply that
/// recreates the containers).
/// </summary>
public static class ComposeEndpoints
{
    public static void MapComposeEndpoints(this IEndpointRouteBuilder app, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logger);

        app.MapGet("/api/compose", async Task<object?> (HttpContext context, UiConfigStore store, DockerService docker) =>
        {
            var composeFile = ResolveApp(store, context).ComposeFile;
            if (!File.Exists(composeFile))
            {
                return new { composeFile, exists = false, items = new List<System.Text.Json.JsonElement>() };
            }

            return new { composeFile, exists = true, items = await docker.ComposeServicesAsync(composeFile) };
        });

        app.MapGet("/api/compose/env", async Task<object?> (HttpContext context, UiConfigStore store) =>
        {
            await Task.CompletedTask;
            var envFile = PinqOpsStatePaths.EnvFile(ResolveApp(store, context).ComposeFile);
            return new
            {
                envFile,
                items = EnvFileStore.GetAll(envFile).Select(pair => new
                {
                    key = pair.Key,
                    // Values are secrets by assumption; the UI only ever sees a mask.
                    masked = pair.Value.Length > 4 ? $"••••{pair.Value[^4..]}" : "••••",
                    managed = Deployer.IsDeployManagedVariable(pair.Key),
                }),
            };
        });

        app.MapPost("/api/compose/env", async Task<object?> (
            HttpContext context, UiConfigStore store, ProxyService proxy, GitHubDashboardService gitHub) =>
        {
            var request = await context.Request.ReadFromJsonAsync<ComposeEnvRequest>()
                ?? throw new ArgumentException("Invalid request body.");
            var connection = ResolveApp(store, context);
            var envFile = PinqOpsStatePaths.EnvFile(connection.ComposeFile);

            int? exposedPort = null;
            try
            {
                exposedPort = await gitHub.GetDockerfileExposedPortAsync(connection);
            }
            catch (Exception exception)
            {
                logger.LogInformation(
                    exception, "Could not read Dockerfile EXPOSE while editing .env for {AppId}", connection.Id);
            }

            // Same predicate the GET projection reports as `managed`, so the editor
            // never offers an edit the write path will reject.
            static void RejectIfDeployManaged(string key)
            {
                if (Deployer.IsDeployManagedVariable(key))
                {
                    throw new ArgumentException($"{key} is managed by pinqops deploy/rollback.");
                }
            }

            // A bad port here only surfaces as a failed `up -d` later — and because
            // compose removes the old container before creating the new one, that
            // takes the app down. Catch it while it is still just a form value.
            // The ports the proxy publishes FOR THIS APP, so an enrolled app is not
            // told its own port is taken — by the proxy, on its behalf. Scoped to
            // this app on purpose: the full published set also holds 80, 443 and
            // every other app's enrolled port, and exempting those waved through
            // exactly the collision this check exists to catch. Loaded once per
            // request rather than per key.
            var domainConfig = proxy.Store.Load();
            var ownProxyPorts = new HashSet<int>(domainConfig.Ports
                .Where(entry => string.Equals(entry.Target, connection.Id, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.HostPort));
            var foreignProxyPorts = new HashSet<int>(ProxyPortSet.HostPorts(domainConfig));
            foreignProxyPorts.ExceptWith(ownProxyPorts);

            void ValidatePortChange(string envFile, string key, string value)
            {
                if (key != SetupTemplates.HostPortVariable && key != SetupTemplates.ContainerPortVariable)
                {
                    return;
                }

                if (!int.TryParse(value.Trim(), NumberStyles.None, InvariantCulture, out var port) || !HostPort.IsValid(port))
                {
                    throw new ArgumentException($"'{value}' is not a valid port for {key} (1-65535).");
                }

                // The container port is bound inside the container's namespace, and
                // re-saving the current host port would flag the app's own container.
                if (key != SetupTemplates.HostPortVariable || StoredPort(envFile, key) == port)
                {
                    return;
                }

                // Named explicitly rather than left to the bind probe: the probe
                // only sees the port while the proxy is up, and "in use on this
                // server" points someone at the wrong culprit anyway.
                if (foreignProxyPorts.Contains(port))
                {
                    throw new ArgumentException(
                        $"Port {port} is published by the proxy — as its own listener or for another app. "
                        + "Pick a different one.");
                }

                if (!ownProxyPorts.Contains(port) && !HostPort.IsAvailable(port))
                {
                    throw new ArgumentException(
                        $"Port {port} is already in use on this server. Pick a free one — "
                        + "the deploy would fail on 'port is already allocated' and leave the app stopped.");
                }
            }

            // The stored port as a number. EnvFileStore returns the value verbatim, so
            // comparing it to the incoming port as text made a hand-edited
            // "PINQOPS_HOST_PORT= 8080" miss — and re-saving that unchanged value was
            // then rejected as "already in use" by the app's own running container.
            static int? StoredPort(string envFile, string key) =>
                int.TryParse(
                    EnvFileStore.GetValue(envFile, key)?.Trim(), NumberStyles.None, InvariantCulture, out var stored)
                    ? stored
                    : null;

            foreach (var key in request.Remove ?? [])
            {
                RejectIfDeployManaged(key);
                EnvFileStore.RemoveValue(envFile, key);
            }

            foreach (var (key, value) in request.Set ?? new Dictionary<string, string>())
            {
                RejectIfDeployManaged(key);

                // A JSON null is valid JSON and deserializes to a null string, so it
                // reached ValidatePortChange and threw a NullReferenceException (500) for
                // a port key while every other key got a 400 from SetValue's own null
                // check. One guard here covers both, and every validator added later.
                if (value is null)
                {
                    throw new ArgumentException($"A value is required for '{key}'.");
                }

                var writeValue = value;
                if (key == SetupTemplates.ContainerPortVariable
                    && exposedPort is { } exposed
                    && int.TryParse(value.Trim(), NumberStyles.None, InvariantCulture, out var requested)
                    && requested != exposed)
                {
                    // Same rule as create-compose: EXPOSE wins over a stale host-port
                    // mirror in the form (e.g. both fields set to 8087 while the app
                    // listens on 8083).
                    logger.LogInformation(
                        "Ignoring .env container port {Requested} for {AppId}; Dockerfile EXPOSE is {Exposed}",
                        requested, connection.Id, exposed);
                    writeValue = exposed.ToString(InvariantCulture);
                }

                ValidatePortChange(envFile, key, writeValue);
                EnvFileStore.SetValue(envFile, key, writeValue);
            }

            // An enrolled app's compose file publishes nothing — the proxy's port
            // entry is what actually serves it. An edited port that stopped at the
            // .env changed nothing (host port) or broke the route (container port)
            // while the editor reported success, so the entry moves with the edit.
            if (domainConfig.Ports.Find(entry =>
                    string.Equals(entry.Target, connection.Id, StringComparison.OrdinalIgnoreCase)) is { } enrolled)
            {
                var hostPort = StoredPort(envFile, SetupTemplates.HostPortVariable) ?? enrolled.HostPort;
                var containerPort = StoredPort(envFile, SetupTemplates.ContainerPortVariable) ?? enrolled.TargetPort;
                if (hostPort != enrolled.HostPort || containerPort != enrolled.TargetPort)
                {
                    // SetAppPortAsync replaces the entry wholesale, so the balancing
                    // (a replica set, or a blue-green colour alias) is put back the
                    // way PutBackOnTheProxyAsync does it.
                    await proxy.SetAppPortAsync(
                        connection.Id, hostPort, enrolled.TargetContainer, containerPort);
                    await proxy.SetAppBalancingAsync(connection.Id, enrolled.Upstream?.Balancing);
                    await proxy.RepublishAsync();
                    logger.LogWarning(
                        "{App} enrolled port moved with the .env edit: {Host} -> {Container}",
                        connection.Id,
                        hostPort,
                        containerPort);
                }
            }

            logger.LogWarning("Compose .env edited from the dashboard");
            return new { ok = true };
        });

        // New env only takes effect when the containers are recreated.
        app.MapPost("/api/compose/apply", async Task<object?> (HttpContext context, UiConfigStore store, DeployService deploys) =>
        {
            var composeFile = ResolveApp(store, context).ComposeFile;
            if (!File.Exists(composeFile))
            {
                throw new InvalidOperationException($"{composeFile} does not exist.");
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

            // Through DeployService so this takes the same gate a rollback does: it is
            // the same `compose up -d` against the same project, and running it
            // concurrently with a rollback recreates the containers from under it.
            return await deploys.ApplyComposeAsync(composeFile, cts.Token)
                ?? throw new InvalidOperationException(
                    "A deploy or rollback is in progress for this project — try again when it finishes.");
        });
    }
}
