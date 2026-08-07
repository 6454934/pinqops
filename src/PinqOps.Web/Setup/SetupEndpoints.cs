using System.Globalization;
using static System.Globalization.CultureInfo;
using PinqOps;
using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>
/// The setup wizard: pick a repo and the dashboard readies the workflow,
/// Dockerfile, compose project and self-hosted runner. The runner-install
/// gate and its progress buffer travel in from the composition root.
/// </summary>
public static class SetupEndpoints
{
    public static void MapSetupEndpoints(this IEndpointRouteBuilder app, ILogger logger, SemaphoreSlim runnerInstallGate, ProgressBuffer runnerInstallProgress)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logger);

        app.MapGet("/api/setup/status", async Task<object?> (HttpContext context, UiConfigStore store, GitHubDashboardService gitHub, LocalRunnerService runner) =>
        {
            if (store.Current.Apps.Count == 0 || !gitHub.HasToken)
            {
                return new { configured = false };
            }

            var app = ResolveApp(store, context);
            if (!gitHub.IsConfiguredFor(app))
            {
                return new { configured = false };
            }

            var repoTask = gitHub.CheckRepoSetupAsync(app);
            var runnersTask = gitHub.GetRunnersSummaryAsync(app);

            // The same courtesy the runner row gets below, for the same reason. A
            // token that cannot read the repository is a permission problem with a
            // fix, and answering the whole card with 502 hid the fix along with
            // everything else on it — including the compose and runner rows, which
            // are read locally and were never in doubt.
            object? repo = null;
            string? repoError = null;
            try
            {
                repo = await repoTask;
            }
            catch (GitHubApiException exception)
            {
                repoError = exception.Message;
            }

            // Listing runners needs repo-admin (Administration: read). A token
            // without it must only degrade the runner row, not kill the card.
            var online = 0;
            var total = 0;
            string? runnersError = null;
            try
            {
                (online, total) = await runnersTask;
            }
            catch (GitHubApiException exception)
            {
                runnersError = exception.Message;
            }

            // "Installed" must mean "registered to THIS repo": a leftover runner
            // from an earlier repository would otherwise short-circuit the setup
            // flow into starting the wrong repo's runner service. One .runner read
            // answers installed/mismatch/registered-to alike.
            var runnerRegisteredTo = LocalRunnerService.GetRegisteredUrl(app.RunnerDirectory);
            var runnerInstalled = runnerRegisteredTo is not null
                && LocalRunnerService.MatchesRepo(runnerRegisteredTo, app.RepoUrl);

            // Whether the systemd service is up lets the dashboard auto-start a
            // stopped runner (safe, idempotent) and avoid offering a useless "start"
            // button when the service is running but just can't reach GitHub.
            var runnerServiceActive = runnerInstalled
                ? await runner.IsServiceActiveAsync(app.RunnerDirectory)
                : null;

            return (object)new
            {
                configured = true,
                appId = app.Id,
                repo,
                repoError,
                runnersOnline = online,
                runnersTotal = total,
                runnersError,
                runnerInstalled,
                runnerServiceActive,
                runnerMismatch = !runnerInstalled && runnerRegisteredTo is not null,
                runnerRegisteredTo,
                composeFile = app.ComposeFile,
                composeExists = File.Exists(app.ComposeFile),
            };
        });

        // The workflow is committed to the repository's default branch, so that is the
        // branch it must trigger on — a hardcoded one would simply never fire.
        app.MapPost("/api/setup/create-workflow", async Task<object?> (HttpContext context, UiConfigStore store, GitHubDashboardService gitHub) =>
        {
            var app = ResolveApp(store, context);
            var defaultBranch = await gitHub.GetDefaultBranchAsync(app);
            var result = await gitHub.CreateWorkflowFileAsync(app, SetupTemplates.DeployWorkflowYaml(defaultBranch));
            logger.LogWarning("Deploy workflow committed, triggering on {Branch}", defaultBranch);
            return result;
        });

        // Updates the deploy workflow in place to the current shape (e.g. adding the
        // preview jobs to a v1 repo). The wizard offers this when a repo's workflow
        // version is behind — a contents PUT with the file's sha, so it replaces.
        app.MapPost("/api/setup/update-workflow", async Task<object?> (HttpContext context, UiConfigStore store, GitHubDashboardService gitHub) =>
        {
            var app = ResolveApp(store, context);
            var defaultBranch = await gitHub.GetDefaultBranchAsync(app);
            var result = await gitHub.UpdateWorkflowFileAsync(app, SetupTemplates.DeployWorkflowYaml(defaultBranch));
            logger.LogWarning("Deploy workflow updated to v{Version} for {Repo}", SetupTemplates.CurrentWorkflowVersion, app.RepoUrl);
            return result;
        });

        // Pins the repository variable the generated workflow reads its compose path
        // from. A standalone, idempotent step (create-compose is skipped once the file
        // exists, so it could never repair a missing/stale variable on a republish).
        app.MapPost("/api/setup/app-var", async Task<object?> (HttpContext context, UiConfigStore store, GitHubDashboardService gitHub) =>
        {
            var app = ResolveApp(store, context);
            await gitHub.SetRepositoryVariableAsync(app, "APP_COMPOSE_PATH", app.ComposeFile);
            logger.LogWarning("APP_COMPOSE_PATH set to {Path} for {Repo}", app.ComposeFile, app.RepoUrl);
            return new { ok = true, name = "APP_COMPOSE_PATH", value = app.ComposeFile };
        });

        // Detects the stack of a repo that has no Dockerfile and returns a generated,
        // editable Dockerfile per candidate — pinqops' answer to "zero config".
        app.MapGet("/api/setup/detect-stack", async Task<object?> (HttpContext context, UiConfigStore store, GitHubDashboardService gitHub) =>
        {
            var app = ResolveApp(store, context);
            var branch = await gitHub.GetDefaultBranchAsync(app);
            var (paths, truncated) = await gitHub.GetRepoTreeAsync(app, branch);

            // First pass (no contents) finds the candidate directories and kinds;
            // then fetch only those dirs' manifests to enrich the build hints.
            var firstPass = StackDetector.Detect(paths, _ => null);
            var contents = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var candidate in firstPass)
            {
                var prefix = candidate.ManifestDir.Length == 0 ? "" : candidate.ManifestDir + "/";
                foreach (var manifest in paths.Where(p => IsDirectManifest(p, prefix)))
                {
                    contents.TryAdd(manifest, null);
                }
            }

            foreach (var key in contents.Keys.ToList())
            {
                contents[key] = await gitHub.GetFileContentAsync(app, key);
            }

            var results = StackDetector.Detect(paths, p => contents.GetValueOrDefault(p));
            return new
            {
                truncated,
                candidates = results.Select(r => new
                {
                    kind = r.Kind.ToString().ToLowerInvariant(),
                    suggestedPort = r.SuggestedPort,
                    dir = r.ManifestDir,
                    hints = r.BuildHints,
                    dockerfile = DockerfileTemplates.For(r),
                }),
            };
        });

        // Commits the (user-edited) Dockerfile verbatim. For a monorepo subdirectory it
        // also pins PINQOPS_BUILD_CONTEXT so the workflow builds from there.
        app.MapPost("/api/setup/create-dockerfile", async Task<object?> (HttpContext context, UiConfigStore store, GitHubDashboardService gitHub) =>
        {
            var app = ResolveApp(store, context);
            var request = await context.Request.ReadFromJsonAsync<CreateDockerfileRequest>()
                ?? throw new ArgumentException("Invalid request body.");
            if (string.IsNullOrWhiteSpace(request.Content))
            {
                throw new ArgumentException("Dockerfile content is required.");
            }

            if (request.Content.Length > 48 * 1024)
            {
                throw new ArgumentException("Dockerfile is too large.");
            }

            // The directory becomes a GitHub contents API path and is pinned into the
            // PINQOPS_BUILD_CONTEXT repository variable, so it has to be a plain
            // relative subdirectory — '..' would reach outside the repository root
            // and a leading '/' or a backslash would change what the path means.
            var dir = (request.Dir ?? string.Empty).Trim().Trim('/');
            if (dir.Contains("..", StringComparison.Ordinal)
                || dir.Contains('\\', StringComparison.Ordinal)
                || dir.AsSpan().IndexOfAny('\0', '\n', '\r') >= 0)
            {
                throw new ArgumentException("Directory must be a relative path inside the repository.");
            }

            var path = dir.Length == 0 ? "Dockerfile" : $"{dir}/Dockerfile";
            object result;
            try
            {
                result = await gitHub.CreateFileAsync(
                    app, path, "chore: add Dockerfile (generated by pinqops-ui)", request.Content);
            }
            catch (GitHubApiException exception) when (exception.StatusCode == 422)
            {
                throw new InvalidOperationException($"{path} already exists in the repository.");
            }

            if (dir.Length > 0)
            {
                await gitHub.SetRepositoryVariableAsync(app, "PINQOPS_BUILD_CONTEXT", dir);
                logger.LogWarning("PINQOPS_BUILD_CONTEXT set to {Dir} for {Repo}", dir, app.RepoUrl);
            }

            logger.LogWarning("Dockerfile committed to {Path} for {Repo}", path, app.RepoUrl);
            return result;
        });

        app.MapPost("/api/setup/start-runner", async Task<object?> (HttpContext context, UiConfigStore store, LocalRunnerService runner) =>
            await runner.StartServiceAsync(ResolveApp(store, context).RunnerDirectory));

        app.MapPost("/api/setup/create-compose", async Task<object?> (HttpContext context, UiConfigStore store, DockerService docker, GitHubDashboardService gitHub, ProxyService proxy, SecretSyncService secretSync) =>
        {
            // The wizard sends its port choices; older callers send no body at all.
            ComposeCreateRequest? request = null;
            try
            {
                request = await context.Request.ReadFromJsonAsync<ComposeCreateRequest>();
            }
            catch (System.Text.Json.JsonException)
            {
            }

            var appConnection = ResolveApp(store, context);
            var repository = GitHubRepositoryParser.Parse(appConnection.RepoUrl);
            var project = ComposeProjectName.FromRepository(repository.Name);

            if (File.Exists(appConnection.ComposeFile))
            {
                // One compose project per path. Silently sharing it between two
                // repositories is the worst outcome: the second repository's deploy
                // pins ITS tag onto the FIRST one's image and dies pulling a tag that
                // only exists in the other package.
                var owner = ComposeProjectName.ReadFrom(await File.ReadAllTextAsync(appConnection.ComposeFile));
                if (owner is not null && owner != project)
                {
                    throw new InvalidOperationException(
                        $"{appConnection.ComposeFile} already belongs to '{owner}', not '{project}'. pinqops manages "
                        + $"one application per compose file. Give this app its own path (Advanced → compose file, "
                        + $"e.g. /opt/pinqops/apps/{project}/docker-compose.yml) — the publish step keeps the "
                        + $"APP_COMPOSE_PATH repository variable in sync automatically.");
                }

                throw new InvalidOperationException($"{appConnection.ComposeFile} already exists.");
            }

            // A newly published app gets its own network rather than the shared one.
            // The proxy is connected to it — that is what keeps the domain and the
            // published port working — and nothing else is, so this app cannot reach
            // another app's database until somebody connects the two.
            //
            // Apps published before this keep the shared network: their compose file
            // is not rewritten, because moving them would cut them off from the
            // database they are already using.
            var appNetwork = AppNetwork.NameFor(project);
            await docker.EnsureNetworkAsync(appNetwork);
            try
            {
                await docker.ConnectIfMissingAsync(appNetwork, ProxyService.ContainerName);
            }
            catch (InvalidOperationException exception)
            {
                // No proxy installed yet is the common case, and it is not a failure:
                // installing one connects it to every app network it finds.
                logger.LogInformation(
                    exception, "Could not attach the proxy to {Network} yet", appNetwork);
            }

            var directory = Path.GetDirectoryName(appConnection.ComposeFile);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Publishing a port is what makes the deployed app actually reachable.
            // The container side comes from the repo's own Dockerfile so the mapping
            // is right without asking; the host side is a safe default the user can
            // change later from the .env editor. Reading the Dockerfile is only a
            // hint — a GitHub hiccup must not block creating the project.
            int? exposedPort = null;
            try
            {
                exposedPort = await gitHub.GetDockerfileExposedPortAsync(appConnection);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception, "Could not read the Dockerfile's EXPOSE; defaulting the container port");
            }

            var containerPort = SetupPorts.ResolveContainer(request?.ContainerPort, exposedPort);
            if (exposedPort is { } detected
                && request?.ContainerPort is { } requested
                && requested != detected)
            {
                logger.LogWarning(
                    "Ignoring requested container port {Requested} for {AppId}; Dockerfile EXPOSE is {Exposed}",
                    requested, appConnection.Id, detected);
            }

            // Nothing owns the app's port yet, so this is the one moment a bind test
            // is meaningful. Taking the next free port beats generating a project
            // whose first deploy dies on "port is already allocated". The bind probe
            // alone is not enough: a port recorded for an app that is stopped, or
            // that has not deployed yet, is bound to nothing right now — and handing
            // it out again kills whichever app deploys second.
            var reserved = SetupPorts.ReservedHostPorts(store.Current, proxy.Store.Load(), appConnection.Id);
            bool PortFree(int port) => !reserved.Contains(port) && HostPort.IsAvailable(port);
            var hostPort = SetupPorts.ResolveHost(
                request?.HostPort,
                UiConfig.DefaultHostPort,
                PortFree,
                preferred => HostPort.FindAvailable(preferred, PortFree));
            if (request?.HostPort is null && hostPort != UiConfig.DefaultHostPort)
            {
                logger.LogWarning(
                    "Host port {Default} is in use; publishing on {Port} instead", UiConfig.DefaultHostPort, hostPort);
            }

            await File.WriteAllTextAsync(
                appConnection.ComposeFile,
                SetupTemplates.ComposeYaml(
                    repository.Owner, repository.Name, hostPort, containerPort, appNetwork));

            // Seed the .env so both ports are discoverable (and editable) in the
            // dashboard instead of being invisible defaults inside the YAML.
            var envFile = PinqOpsStatePaths.EnvFile(appConnection.ComposeFile);
            EnvFileStore.SetValue(envFile, SetupTemplates.HostPortVariable, hostPort.ToString(InvariantCulture));
            EnvFileStore.SetValue(envFile, SetupTemplates.ContainerPortVariable, containerPort.ToString(InvariantCulture));

            // Secrets stored before this moment had nowhere to go: SecretSyncService
            // skips an app whose compose directory does not exist yet, and it only
            // runs on a secret write. Without this, everything entered on the app's
            // Secrets tab before publishing stayed out of the .env until some later,
            // unrelated secret write happened to sync it — so the first deploy of a
            // new app started without the values it was told it had.
            var secretWarnings = secretSync.Sync();
            foreach (var warning in secretWarnings)
            {
                logger.LogWarning("Seeding secrets into the new compose project: {Warning}", warning);
            }

            logger.LogWarning(
                "Compose project created at {File} publishing {HostPort}->{ContainerPort}",
                appConnection.ComposeFile, hostPort, containerPort);
            return new { ok = true, composeFile = appConnection.ComposeFile, hostPort, containerPort };
        });

        // Pre-publish data for the wizard's port form: the detected container port and
        // a suggested free host port, plus the current .env values once the compose
        // project exists. The generic .env endpoint masks every value, so the wizard
        // needs this dedicated, ports-only view.
        app.MapGet("/api/setup/publish-info", async Task<object?> (HttpContext context, UiConfigStore store, GitHubDashboardService gitHub, ProxyService proxy) =>
        {
            var app = ResolveApp(store, context);

            // The Dockerfile read is a hint — a GitHub hiccup must degrade to
            // "nothing detected", not break the form.
            int? detectedContainerPort = null;
            try
            {
                detectedContainerPort = await gitHub.GetDockerfileExposedPortAsync(app);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not read the Dockerfile's EXPOSE for the publish form");
            }

            var composeExists = File.Exists(app.ComposeFile);
            var envFile = PinqOpsStatePaths.EnvFile(app.ComposeFile);
            var currentHostPort = composeExists
                ? TryParsePort(EnvFileStore.GetValue(envFile, SetupTemplates.HostPortVariable))
                : null;
            var currentContainerPort = composeExists
                ? TryParsePort(EnvFileStore.GetValue(envFile, SetupTemplates.ContainerPortVariable))
                : null;

            // Suggested from the same "spoken for" set create-compose allocates
            // with, so the form never proposes a port that step would then refuse.
            var reserved = SetupPorts.ReservedHostPorts(store.Current, proxy.Store.Load(), app.Id);
            return new
            {
                composeExists,
                detectedContainerPort,
                fallbackContainerPort = DockerfileInspector.DefaultPort,
                suggestedHostPort = currentHostPort
                    ?? HostPort.FindAvailable(
                        UiConfig.DefaultHostPort,
                        port => !reserved.Contains(port) && HostPort.IsAvailable(port))
                    ?? UiConfig.DefaultHostPort,
                currentHostPort,
                currentContainerPort,
            };
        });

        // Live validation while the user types a host port in the wizard.
        app.MapGet("/api/setup/port-check", async Task<object?> (HttpContext context, UiConfigStore store, ProxyService proxy) =>
        {
            await Task.CompletedTask;
            var raw = context.Request.Query["port"].ToString().Trim();
            if (!int.TryParse(raw, NumberStyles.None, InvariantCulture, out var port) || !HostPort.IsValid(port))
            {
                return (object)new { port = raw, valid = false, available = false };
            }

            // The app's own container legitimately owns its current host port — a
            // bind probe would flag it as busy, so treat "unchanged" as free.
            //
            // Compared as a number, not as text: EnvFileStore returns the stored value
            // verbatim, so a hand-edited "PINQOPS_HOST_PORT= 8080" did not match
            // "8080" as a string and the wizard reported the app's own, currently
            // bound port as taken. Every other read of these values already goes
            // through TryParsePort.
            var appConnection = ResolveApp(store, context);
            var envFile = PinqOpsStatePaths.EnvFile(appConnection.ComposeFile);
            var currentHostPort = File.Exists(appConnection.ComposeFile)
                ? TryParsePort(EnvFileStore.GetValue(envFile, SetupTemplates.HostPortVariable))
                : null;

            // Checked against the recorded owners as well as a live bind: another
            // app that is merely stopped still owns its port, and saying "free"
            // here sets up the collision create-compose exists to prevent.
            var reserved = SetupPorts.ReservedHostPorts(store.Current, proxy.Store.Load(), appConnection.Id);
            var available = currentHostPort == port
                || (!reserved.Contains(port) && HostPort.IsAvailable(port));
            return (object)new { port, valid = true, available };
        });

        // Live state of the deployed app for the wizard's "your app is live" card:
        // whether the compose container runs and on which host port it is reachable.
        app.MapGet("/api/setup/app-status", async Task<object?> (
            HttpContext context, UiConfigStore store, DockerService docker, ProxyService proxy) =>
        {
            var app = ResolveApp(store, context);
            var composeExists = File.Exists(app.ComposeFile);
            var envFile = PinqOpsStatePaths.EnvFile(app.ComposeFile);

            string? state = null;
            int? publishedPort = null;
            var dockerOk = true;
            if (composeExists)
            {
                try
                {
                    (state, publishedPort) = ComposeAppStatus.FromServices(
                        await docker.ComposeServicesAsync(app.ComposeFile));
                }
                catch (InvalidOperationException)
                {
                    dockerOk = false;
                }
            }

            // An app that handed its port to the proxy publishes nothing of its
            // own, so the container's bindings are silent about where it answers
            // and the .env is only a record of what the proxy was asked to take.
            // The proxy's route is the one thing that says what is actually
            // listening, so it wins when there is one.
            var proxyPort = proxy.Store.Load().Ports.Find(entry =>
                entry.Enabled && string.Equals(entry.Target, app.Id, StringComparison.OrdinalIgnoreCase));

            return new
            {
                composeExists,
                dockerOk,
                state,
                running = string.Equals(state, "running", StringComparison.OrdinalIgnoreCase),
                // The proxy's route first, then the port docker actually bound;
                // before the first deploy fall back to what the .env says will be
                // published.
                hostPort = proxyPort?.HostPort
                    ?? publishedPort
                    ?? TryParsePort(EnvFileStore.GetValue(envFile, SetupTemplates.HostPortVariable)),
                publishedByProxy = proxyPort is not null,
                currentTag = EnvFileStore.GetValue(envFile, Deployer.TagVariable),
                currentDeployedAt = new DeployHistoryStore(app.ComposeFile).LastSuccessful()?.StartedAt,
            };
        });

        // Starts the first deploy right from the wizard instead of waiting for a push:
        // dispatches the generated workflow on the repository's default branch.
        app.MapPost("/api/setup/trigger-deploy", async Task<object?> (HttpContext context, UiConfigStore store, GitHubDashboardService gitHub) =>
        {
            var app = ResolveApp(store, context);
            var branch = await gitHub.GetDefaultBranchAsync(app);

            // A workflow the wizard committed seconds ago may not be indexed by
            // the Actions API yet — 404s briefly even though the file is there.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await gitHub.TriggerDeployWorkflowAsync(app, branch);
                    break;
                }
                catch (GitHubApiException exception) when (exception.StatusCode == 404 && attempt < 5)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }
            }

            logger.LogWarning("First deploy triggered via workflow_dispatch on {Branch}", branch);
            return new { ok = true, branch };
        });

        // Full runner install driven from the dashboard: registration token via the
        // stored PAT, then download + config.sh + systemd service (same code path as
        // `pinqops install-runner`).
        app.MapPost("/api/setup/install-runner", async (HttpContext context, UiConfigStore store, IProcessRunner processRunner) =>
        {
            // One install at a time across ALL apps: the installer sets a process-wide
            // env var and downloads are huge — serialize, and stamp the buffer with the
            // app so a poller for another app can ignore foreign lines.
            AppConnection appConnection;
            try
            {
                appConnection = ResolveApp(store, context);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Error(400, exception.Message);
            }

            if (!await runnerInstallGate.WaitAsync(0))
            {
                var busyFor = runnerInstallProgress.AppId;
                return Error(409, busyFor is null
                    ? "A runner install is already in progress."
                    : $"A runner install is already in progress (app '{busyFor}').");
            }

            runnerInstallProgress.Start(appConnection.Id);
            var succeeded = false;
            try
            {
                var config = store.Current;
                if (string.IsNullOrWhiteSpace(appConnection.RepoUrl) || string.IsNullOrWhiteSpace(config.Pat))
                {
                    return Error(400, "Connect GitHub first.");
                }

                runnerInstallProgress.Add("requesting a runner registration token…");
                var repository = GitHubRepositoryParser.Parse(appConnection.RepoUrl);
                string registrationToken;
                string? removalToken = null;
                using (var apiClient = new GitHubApiClient())
                {
                    try
                    {
                        registrationToken = await apiClient.CreateRegistrationTokenAsync(repository, config.Pat);
                    }
                    catch (GitHubApiException exception)
                    {
                        runnerInstallProgress.Add("error: " + exception.Message);
                        return Results.Json(new { succeeded = false, log = runnerInstallProgress.Text() });
                    }

                    // A leftover runner registered to another repository must be
                    // de-registered first; mint a removal token for THAT repo. Best
                    // effort — cleanup falls back to deleting local files without it.
                    var registeredUrl = LocalRunnerService.GetRegisteredUrl(appConnection.RunnerDirectory);
                    if (registeredUrl is not null)
                    {
                        try
                        {
                            var oldRepository = GitHubRepositoryParser.Parse(registeredUrl);
                            runnerInstallProgress.Add($"existing runner is registered to {oldRepository.Owner}/{oldRepository.Name}; requesting a removal token…");
                            removalToken = await apiClient.CreateRemovalTokenAsync(oldRepository, config.Pat);
                        }
                        catch (Exception exception) when (exception is GitHubApiException or ArgumentException)
                        {
                            runnerInstallProgress.Add("could not get a removal token for the old runner: " + exception.Message);
                        }
                    }
                }

                runnerInstallProgress.Add("token received; installing the runner…");
                var options = RunnerInstallOptions.Create(
                        appConnection.RepoUrl, registrationToken,
                        // Per-app agent name so `docker ps`-style debugging reads well;
                        // names are repo-scoped on GitHub, so this is cosmetic but nice.
                        runnerName: $"{Environment.MachineName}-{appConnection.Id}",
                        installDirectory: appConnection.RunnerDirectory)
                    with { RemovalToken = removalToken };
                using var downloader = new HttpFileDownloader();
                var installer = new RunnerInstaller(processRunner, downloader, runnerInstallProgress.Add);
                var serviceUser = Environment.GetEnvironmentVariable("SUDO_USER") ?? Environment.UserName;
                // The runner tarball is ~180 MB, so the bound has to be generous — the
                // same 30-minute leash PullImageAsync gives a large image pull. Without a
                // token at all the download had only HttpClient's 100-second default,
                // which no slow uplink could beat.
                using var installTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                succeeded = await installer.InstallAsync(options, serviceUser, installTimeout.Token);
                logger.LogWarning("Dashboard runner install finished: {Succeeded}", succeeded);
                return Results.Json(new { succeeded, log = runnerInstallProgress.Text() });
            }
            catch (Exception exception)
            {
                // The wizard may only ever see the progress buffer (when the POST is
                // severed by a proxy timeout), so the failure reason must land there.
                runnerInstallProgress.Add("error: " + exception.Message);
                return Error(500, exception.Message);
            }
            finally
            {
                runnerInstallProgress.Finish(succeeded);
                runnerInstallGate.Release();
            }
        });

        // Polled by the setup wizard while the install POST above is in flight, so the
        // user sees download/extract/configure/service lines live.
        app.MapGet("/api/setup/install-runner/progress", () => Results.Json(runnerInstallProgress.Snapshot()));
    }
}
