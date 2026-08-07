using PinqOps;
using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>
/// The app catalog: one-click installs that run as pinqops-&lt;id&gt;
/// containers, plus their install jobs and stored credentials.
/// </summary>
public static class AppEndpoints
{
    public static void MapAppEndpoints(this IEndpointRouteBuilder app, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logger);

        app.MapGet("/api/apps", async Task<object?> (HttpContext context, DockerService docker, AppInstallJobs jobs) =>
        {
            var installedById = new Dictionary<string, (string State, string Ports)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var container in await Env(context, docker).ListContainersAsync())
                {
                    var labels = container.TryGetProperty("Labels", out var l) ? l.GetString() ?? "" : "";
                    var appLabel = labels.Split(',').FirstOrDefault(x => x.StartsWith(AppCatalog.Label + "=", StringComparison.Ordinal));
                    if (appLabel is not null)
                    {
                        installedById[appLabel[(AppCatalog.Label.Length + 1)..]] = (
                            container.TryGetProperty("State", out var s) ? s.GetString() ?? "" : "",
                            container.TryGetProperty("Ports", out var p) ? p.GetString() ?? "" : "");
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // Docker unreachable — the catalog is still browsable.
            }

            var installing = jobs.ActiveAppIds(EnvId(context));
            return new
            {
                items = AppCatalog.Apps.Select(a =>
                {
                    var installed = installedById.TryGetValue(a.Id, out var info);
                    // The Open link must use the port the container actually
                    // binds (the user may have overridden the catalog default).
                    var actualHostPort = installed ? ParseFirstHostPort(info.Ports) : null;
                    return new
                    {
                        id = a.Id,
                        name = a.Name,
                        category = a.Category,
                        image = a.Image,
                        note = a.Note,
                        unauthenticated = a.Unauthenticated,
                        ports = a.Ports.Select(p => new { host = p.Host, container = p.Container }).ToArray(),
                        installed,
                        installing = installing.Contains(a.Id, StringComparer.OrdinalIgnoreCase),
                        state = installed ? info.State : null,
                        hostPort = actualHostPort ?? (a.Ports.Length > 0 ? a.Ports[0].Host : (int?)null),
                        loopbackOnly = installed && IsLoopbackOnly(info.Ports),
                    };
                }),
            };
        });

        // Installs run in the background (docker pull can take minutes): the endpoint
        // returns a job id immediately and the UI polls the job for pulling→starting→
        // done, so progress shows without a page refresh.
        app.MapPost("/api/apps/install", async (HttpContext context, DockerService docker, AppInstallJobs jobs) =>
        {
            AppInstallRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<AppInstallRequest>();
            }
            catch (System.Text.Json.JsonException)
            {
                return Error(400, "Invalid request body.");
            }

            var appSpec = AppCatalog.Find(request?.Id ?? "");
            if (appSpec is null)
            {
                return Error(400, $"Unknown app '{request?.Id}'.");
            }

            var hostPorts = request?.HostPorts
                ?? (request?.HostPort is { } single ? new[] { single } : null);
            if (hostPorts is not null && hostPorts.Any(p => p is not 0 and (< 1 or > 65535)))
            {
                return Error(400, "Host port must be between 1 and 65535.");
            }

            // Exposing an app on every interface is an explicit choice, and one only an
            // admin may make: several catalog services have no authentication at all.
            var publishPublicly = request?.Public ?? false;
            if (publishPublicly && context.Items["scope"] as string != "admin")
            {
                return Error(403, "Publishing an app publicly requires the admin role.");
            }

            // Bound before the background task starts: the request scope is gone by the
            // time the install runs, so resolving the environment later would silently
            // fall back to this server — installing onto the wrong host while the UI
            // showed the one that was chosen.
            var installDocker = Env(context, docker);

            var job = jobs.TryStart(installDocker.EnvironmentId, appSpec.Id);
            if (job is null)
            {
                return Error(409, "An install for this app is already in progress on this environment.");
            }

            var credentialStore = context.RequestServices.GetRequiredService<AppCredentialStore>();

            // Resolved before the background task starts: the request scope (and its
            // HttpContext) is gone by the time the install finishes.
            var installOwnership = context.RequestServices.GetRequiredService<ContainerOwnershipStore>();
            var installer = context.Items["user"] as string ?? string.Empty;

            _ = Task.Run(async () =>
            {
                // Whether the app's container was already on the host when this
                // install reached the point of creating one. Null while that is still
                // unknown — a failure before the check (a pull that never completes)
                // cannot have created anything, so there is nothing to clean up and a
                // container that happens to exist is not this install's to remove.
                bool? containerExistedBeforeInstall = null;

                try
                {
                    // Credential tokens resolve to per-app generated passwords; a
                    // reinstall reuses the stored one so existing volumes keep working.
                    // Passwords are per environment: the same app on two hosts must not
                    // share one, and a cross-app reference ({{password:mysql}}) has to
                    // resolve to the MySQL on *this* host.
                    string PasswordFor(string targetApp) =>
                        credentialStore.GetOrCreatePassword(installDocker.EnvironmentId, targetApp);

                    var (env, credentials) = AppCatalog.ResolveEnv(appSpec, PasswordFor);
                    if (credentials.Count > 0)
                    {
                        credentialStore.SetEnv(installDocker.EnvironmentId, appSpec.Id, credentials);
                    }

                    // Some images take their password only as a command-line flag, so the
                    // command needs the same substitution the env does.
                    var cmd = AppCatalog.ResolveCmd(appSpec, PasswordFor);

                    await installDocker.PullImageAsync(appSpec.Image);
                    job.Phase = "starting";

                    containerExistedBeforeInstall = (await installDocker
                        .ContainerStateAsync($"{AppCatalog.ContainerPrefix}{appSpec.Id}")).Exists;
                    job.Output = await installDocker.InstallAppAsync(appSpec, hostPorts, env, cmd, publishPublicly);

                    // The installer owns the app's container, so it can uninstall what it
                    // installed without being an admin — the same rule container create
                    // applies. Without a record the app would be admin-only to remove.
                    if (installer.Length > 0)
                    {
                        installOwnership.Set(
                            installDocker.EnvironmentId,
                            ProtectedResource.ContainerForApp(appSpec.Id),
                            installer,
                            ContainerOwnershipStore.AccessPrivate);
                    }

                    job.Phase = "done";
                }
                catch (Exception exception)
                {
                    job.Error = exception.Message;
                    logger.LogWarning("App install '{AppId}' failed: {Message}", appSpec.Id, exception.Message);

                    // `docker run` creates the container and then starts it, so a
                    // host port that is already taken exits 125 with the container
                    // left behind in Created state. The ownership write above never
                    // ran, but the container carries the app label and `ps -a` lists
                    // it — the catalog says the app is installed, the uninstall gate
                    // refuses the very person who installed it, and the name makes
                    // every retry fail on a conflict. Only a container this install
                    // created is removed; one that was already there belongs to
                    // someone else and their data is not this failure's to discard.
                    if (containerExistedBeforeInstall is false)
                    {
                        await RemoveFailedInstallAsync(installDocker, appSpec.Id, logger);
                    }

                    // Flipped last: the dashboard reloads the app list the moment a
                    // job reports a terminal phase, and it should not read one that
                    // still lists the container being removed above.
                    job.Phase = "error";
                }
            });

            return Results.Json(new { jobId = job.Id });
        });

        app.MapGet("/api/apps/install/{jobId}", (string jobId, AppInstallJobs jobs) =>
        {
            var job = jobs.Find(jobId);
            return job is null
                ? Error(404, "Unknown install job.")
                : Results.Json(new { appId = job.AppId, phase = job.Phase, done = job.Done, error = job.Error, output = job.Output });
        });

        app.MapPost("/api/apps/{id}/uninstall", async Task<object?> (string id, HttpContext context, DockerService docker, ContainerOwnershipStore ownership) =>
        {
            // The spec's canonical id, not the caller's casing: Find matches
            // case-insensitively, but the container was created as
            // pinqops-<lowercase id> and docker names are case-sensitive — so
            // "REDIS" ran `docker rm -f -- pinqops-REDIS`, which fails, while the
            // ownership record it then cleared was computed from the lowercased name.
            var appToRemove = AppCatalog.Find(id) ?? throw new ArgumentException($"Unknown app '{id}'.");
            var output = await Env(context, docker).UninstallAppAsync(appToRemove.Id);
            // The container is gone, so its ownership record would only linger to be
            // inherited by whatever reuses the name.
            ownership.Remove(EnvId(context), ProtectedResource.ContainerForApp(appToRemove.Id));
            return new { ok = true, output };
        }).RequireContainerOwnership(ContainerOwnershipSource.AppRouteId);

        // Stored generated credentials of an installed catalog app (behind dashboard
        // auth like everything else). Kept retrievable because volumes outlive the
        // container and a reinstall must reuse the same password.
        app.MapGet("/api/apps/{id}/credentials", (string id, HttpContext context, AppCredentialStore credentials) =>
        {
            var appSpec = AppCatalog.Find(id);
            if (appSpec is null)
            {
                return Error(404, $"Unknown app '{id}'.");
            }

            var env = credentials.Get(EnvId(context), appSpec.Id);
            return Results.Json(new
            {
                appId = appSpec.Id,
                items = AppCredentialStore.Displayable(env)
                    .Select(pair => new { key = pair.Key, value = pair.Value })
                    .ToArray<object>(),
                note = appSpec.Note,
            });
        });
    }

    /// <summary>
    /// Removes the container a failed install left behind. Its own failure is
    /// logged rather than thrown: "No such container" is the ordinary answer for a
    /// run that failed before creating anything, and the job has to keep reporting
    /// the error that actually stopped the install.
    /// </summary>
    private static async Task RemoveFailedInstallAsync(DockerService docker, string appId, ILogger logger)
    {
        try
        {
            await docker.UninstallAppAsync(appId);
        }
        catch (Exception cleanupException)
        {
            logger.LogWarning(
                cleanupException,
                "Could not remove the container left behind by the failed install of '{AppId}'.",
                appId);
        }
    }
}
