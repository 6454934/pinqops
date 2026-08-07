using PinqOps.Deploy;
using PinqOps.Proxy;
using PinqOps.Secrets;

namespace PinqOps.Web;

/// <summary>
/// Tears down everything that belongs to a connected GitHub app — previews,
/// proxy routes, compose project (with volumes), runner, disk, secrets and
/// grants — before the dashboard row is dropped.
/// </summary>
public sealed class AppPurgeService
{
    private static readonly TimeSpan ComposeTimeout = TimeSpan.FromMinutes(5);

    private readonly IProcessRunner _processRunner;
    private readonly ProxyService _proxy;
    private readonly DockerService _docker;
    private readonly SecretStore _secrets;
    private readonly TeamStore _teams;
    private readonly ILogger<AppPurgeService> _logger;

    public AppPurgeService(
        IProcessRunner processRunner,
        ProxyService proxy,
        DockerService docker,
        SecretStore secrets,
        TeamStore teams,
        ILogger<AppPurgeService> logger)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _proxy = proxy ?? throw new ArgumentNullException(nameof(proxy));
        _docker = docker ?? throw new ArgumentNullException(nameof(docker));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _teams = teams ?? throw new ArgumentNullException(nameof(teams));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Best-effort full teardown. Individual step failures are collected in
    /// <c>warnings</c>; the caller still removes the dashboard row so the UI
    /// cannot get stuck on a half-purged app.
    /// </summary>
    public async Task<AppPurgeResult> PurgeAsync(
        AppConnection app,
        string? pat,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        var warnings = new List<string>();
        var removed = new List<string>();
        // Resolve before compose/disk go away — create-compose names the external
        // network from the compose project (and sometimes the app id).
        var project = await ResolveProjectNameAsync(app, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Purge {AppId}: starting (compose project {Project})", app.Id, project);

        _logger.LogInformation("Purge {AppId}: tearing down preview stacks", app.Id);
        await TeardownPreviewsAsync(app, warnings, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Purge {AppId}: clearing proxy domains/ports", app.Id);
        await ClearProxyRoutesAsync(app, warnings, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Purge {AppId}: compose down -p {Project}", app.Id, project);
        await ComposeDownAsync(app, project, warnings, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Purge {AppId}: removing per-app networks", app.Id);
        await RemoveAppNetworksAsync(app, project, warnings).ConfigureAwait(false);
        _logger.LogInformation("Purge {AppId}: uninstalling runner", app.Id);
        await UninstallRunnerAsync(app, pat, warnings, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Purge {AppId}: deleting disk paths", app.Id);
        DeleteDisk(app, warnings, removed);
        _logger.LogInformation("Purge {AppId}: purging secrets and grants", app.Id);
        PurgeSecrets(app, warnings);
        PurgeGrants(app);

        _logger.LogInformation(
            "Purge {AppId}: finished (removed {Count} path(s), {WarningCount} warning(s))",
            app.Id, removed.Count, warnings.Count);
        return new AppPurgeResult(warnings, removed);
    }

    private async Task TeardownPreviewsAsync(
        AppConnection app, List<string> warnings, CancellationToken cancellationToken)
    {
        GitHubRepository repository;
        try
        {
            repository = GitHubRepositoryParser.Parse(app.RepoUrl);
        }
        catch (ArgumentException)
        {
            return;
        }

        var manager = new PreviewManager(_processRunner, ProxyPaths.DefaultDirectory);
        foreach (var preview in PreviewManager.List(app.ComposeFile, repository.Name))
        {
            try
            {
                if (!await manager.TeardownAsync(
                        app.ComposeFile, repository.Name, preview.PullRequestNumber, cancellationToken)
                    .ConfigureAwait(false))
                {
                    warnings.Add($"Preview for PR #{preview.PullRequestNumber} could not be torn down.");
                }
            }
            catch (Exception exception)
            {
                warnings.Add($"Preview for PR #{preview.PullRequestNumber}: {exception.Message}");
            }
        }
    }

    private async Task ClearProxyRoutesAsync(
        AppConnection app, List<string> warnings, CancellationToken cancellationToken)
    {
        try
        {
            var (hadDomains, hadPorts) = _proxy.Store.Update(config =>
            {
                var domains = config.Domains.RemoveAll(entry =>
                    string.Equals(entry.Target, app.Id, StringComparison.OrdinalIgnoreCase)) > 0;
                var ports = config.Ports.RemoveAll(entry =>
                    string.Equals(entry.Target, app.Id, StringComparison.OrdinalIgnoreCase)) > 0;
                return (domains, ports);
            });

            if (!hadPorts && !hadDomains)
            {
                return;
            }

            // Domains need a Caddy reload; dropped host ports need the proxy
            // container recreated so its -p flags match.
            await _proxy.ApplyAsync().ConfigureAwait(false);
            if (hadPorts)
            {
                await _proxy.RepublishAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            warnings.Add($"Proxy cleanup: {exception.Message}");
            _logger.LogWarning(exception, "Proxy cleanup failed for app {AppId}", app.Id);
        }
    }

    private async Task ComposeDownAsync(
        AppConnection app,
        string project,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var workingDirectory = PinqOpsStatePaths.ComposeWorkingDirectory(app.ComposeFile)
            ?? Path.GetDirectoryName(app.ComposeFile)
            ?? ".";

        if (File.Exists(app.ComposeFile))
        {
            // Blue/green leftovers first when colour env files exist.
            foreach (var color in new[] { DeployColors.Blue, DeployColors.Green })
            {
                var colorEnv = ColorEnvironment.FileFor(app.ComposeFile, color);
                if (!File.Exists(colorEnv))
                {
                    continue;
                }

                var colorProject = DeployColors.ProjectName(project, color);
                await RunComposeAsync(
                        DockerComposeCommandBuilder.DownColor(
                            app.ComposeFile, colorProject, colorEnv, removeVolumes: true),
                        workingDirectory,
                        $"compose down ({color})",
                        warnings,
                        cancellationToken)
                    .ConfigureAwait(false);
                await ForceRemoveProjectContainersAsync(colorProject, warnings).ConfigureAwait(false);
            }

            // Pin -p to the resolved compose project. Directory-derived names
            // (owner-repo) miss containers created as the repo project (repo).
            await RunComposeAsync(
                    DockerComposeCommandBuilder.Down(app.ComposeFile, project, removeVolumes: true),
                    workingDirectory,
                    "compose down",
                    warnings,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            // YAML already gone — still tear down whatever docker knows as this project.
            await RunComposeAsync(
                    DockerComposeCommandBuilder.DownProject(project, removeVolumes: true),
                    workingDirectory,
                    "compose down (project)",
                    warnings,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await ForceRemoveProjectContainersAsync(project, warnings).ConfigureAwait(false);
    }

    /// <summary>
    /// Last resort after <c>compose down</c>: force-rm any container still labelled
    /// with the compose project so DeleteDisk cannot orphan a running stack.
    /// </summary>
    private async Task ForceRemoveProjectContainersAsync(string project, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(project) || !DockerService.IsValidResourceName(project))
        {
            return;
        }

        IReadOnlyList<string> ids;
        try
        {
            ids = await _docker.ListContainerIdsByComposeProjectAsync(project).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            warnings.Add($"List containers for project {project}: {exception.Message}");
            return;
        }

        foreach (var id in ids)
        {
            try
            {
                await _docker.RemoveContainerAsync(id, removeVolumes: true).ConfigureAwait(false);
                _logger.LogWarning(
                    "Force-removed orphan container {ContainerId} for compose project {Project}",
                    id, project);
            }
            catch (Exception exception)
            {
                warnings.Add($"Remove container {id}: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// Drops the per-app external network create-compose attached the proxy to.
    /// <c>compose down -v</c> does not remove <c>external: true</c> networks.
    /// Never touches the shared <c>pinqops-apps</c> network.
    /// </summary>
    private async Task RemoveAppNetworksAsync(AppConnection app, string project, List<string> warnings)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seed in new[] { project, app.Id })
        {
            if (string.IsNullOrWhiteSpace(seed))
            {
                continue;
            }

            try
            {
                names.Add(AppNetwork.NameFor(seed));
            }
            catch (ArgumentException)
            {
                // Id/project that cannot form a docker network name — skip.
            }
        }

        foreach (var network in names)
        {
            try
            {
                await _docker.DisconnectNetworkAsync(network, ProxyService.ContainerName)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Proxy absent or already disconnected is normal.
                _logger.LogDebug(exception, "Disconnect proxy from {Network}", network);
            }

            try
            {
                await _docker.RemoveNetworkAsync(network).ConfigureAwait(false);
                _logger.LogWarning("Removed app network {Network} for {AppId}", network, app.Id);
            }
            catch (Exception exception) when (IsMissingNetwork(exception))
            {
                // Already gone — purge is idempotent; do not surface as a warning.
                _logger.LogDebug(exception, "App network {Network} already absent for {AppId}", network, app.Id);
            }
            catch (Exception exception)
            {
                warnings.Add($"Network {network}: {exception.Message}");
            }
        }
    }

    private static bool IsMissingNetwork(Exception exception)
    {
        var text = exception.Message;
        return text.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || text.Contains("No such network", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> ResolveProjectNameAsync(AppConnection app, CancellationToken cancellationToken)
    {
        if (File.Exists(app.ComposeFile))
        {
            try
            {
                var declared = ComposeProjectName.ReadFrom(
                    await File.ReadAllTextAsync(app.ComposeFile, cancellationToken).ConfigureAwait(false));
                if (declared is { Length: > 0 })
                {
                    return declared;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(exception, "Could not read compose name for {AppId}", app.Id);
            }
        }

        return SafeProjectName(app);
    }

    private async Task UninstallRunnerAsync(
        AppConnection app, string? pat, List<string> warnings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(app.RunnerDirectory) || !Directory.Exists(app.RunnerDirectory))
        {
            return;
        }

        string? removalToken = null;
        if (!string.IsNullOrWhiteSpace(pat))
        {
            try
            {
                var registeredUrl = LocalRunnerService.GetRegisteredUrl(app.RunnerDirectory) ?? app.RepoUrl;
                var repository = GitHubRepositoryParser.Parse(registeredUrl);
                using var apiClient = new GitHubApiClient();
                removalToken = await apiClient.CreateRemovalTokenAsync(repository, pat)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is GitHubApiException or ArgumentException)
            {
                warnings.Add($"Runner removal token: {exception.Message}");
            }
        }

        try
        {
            using var downloader = new HttpFileDownloader();
            var installer = new RunnerInstaller(_processRunner, downloader, message =>
                _logger.LogInformation("Runner uninstall [{AppId}]: {Message}", app.Id, message));
            await installer.UninstallAsync(app.RunnerDirectory, removalToken, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            warnings.Add($"Runner uninstall: {exception.Message}");
        }
    }

    private static void DeleteDisk(AppConnection app, List<string> warnings, List<string> removed)
    {
        var composeDir = Path.GetDirectoryName(app.ComposeFile);
        foreach (var path in new[] { composeDir, app.RunnerDirectory })
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                continue;
            }

            try
            {
                Directory.Delete(path, recursive: true);
                removed.Add(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not delete {path}: {exception.Message}");
            }
        }
    }

    private void PurgeSecrets(AppConnection app, List<string> warnings)
    {
        try
        {
            var scope = SecretScopes.Normalize(app.Id);
            foreach (var secret in _secrets.List().Where(s =>
                         string.Equals(s.Scope, scope, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _secrets.Remove(secret.Scope, secret.Name);
            }
        }
        catch (Exception exception)
        {
            warnings.Add($"Secrets: {exception.Message}");
        }
    }

    private void PurgeGrants(AppConnection app)
    {
        try
        {
            _teams.RemoveResource(ResourceKinds.App, app.Id);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Grant cleanup failed for app {AppId}", app.Id);
        }
    }

    private async Task RunComposeAsync(
        IReadOnlyList<string> args,
        string workingDirectory,
        string label,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ComposeTimeout);
            var result = await _processRunner
                .RunAsync("docker", args, workingDirectory, timeout.Token)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                warnings.Add($"{label}: {result.StandardError.Trim()}");
            }
        }
        catch (Exception exception)
        {
            warnings.Add($"{label}: {exception.Message}");
        }
    }

    private static string SafeProjectName(AppConnection app)
    {
        try
        {
            return ComposeProjectName.FromRepository(GitHubRepositoryParser.Parse(app.RepoUrl).Name);
        }
        catch (ArgumentException)
        {
            return ComposeProjectName.FromRepository(app.Id);
        }
    }
}

/// <summary>Outcome of a full app purge (infra teardown before config remove).</summary>
public sealed record AppPurgeResult(IReadOnlyList<string> Warnings, IReadOnlyList<string> RemovedPaths);
