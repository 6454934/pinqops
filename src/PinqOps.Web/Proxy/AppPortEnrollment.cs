using PinqOps.Deploy;
using PinqOps.Proxy;

namespace PinqOps.Web;

/// <summary>What an enrol or unenrol did.</summary>
public sealed record EnrollmentResult(bool Enrolled, int HostPort, string Alias, string? RolledBackBecause)
{
    public bool Failed => RolledBackBecause is not null;
}

/// <summary>
/// Moves an app between publishing its own host port and letting the proxy publish
/// it.
///
/// <para><b>Why this exists as one sequence rather than a few endpoints.</b> There
/// is no way to hand a bound listening socket from one container to another, so the
/// port must be released before it can be taken. That makes the middle of this a
/// window where nothing is listening, and every step after the rewrite has to be
/// undoable — otherwise a failure halfway leaves an app that publishes nothing and
/// a proxy that does not know about it, which is an app that is simply gone.</para>
///
/// <para><b>The blip is real and bounded.</b> It is one operator-initiated moment,
/// not a per-deploy cost, and the dashboard says so rather than implying the switch
/// is free.</para>
///
/// <para><b>There is deliberately no automatic fallback.</b> If the proxy later
/// dies, nothing here rewrites compose files in the background to rescue the app:
/// doing that while a deploy may hold the project's lock, or while the operator has
/// stopped the proxy on purpose, is how a short outage becomes a set of corrupted
/// projects. The watchdog says so loudly and the operator unenrols with one click.</para>
/// </summary>
public sealed class AppPortEnrollment
{
    private static readonly TimeSpan ComposeTimeout = TimeSpan.FromMinutes(3);

    private readonly ProxyService _proxy;
    private readonly DeployService _deploy;
    private readonly UiConfigStore _config;
    private readonly ILogger<AppPortEnrollment> _logger;

    public AppPortEnrollment(
        ProxyService proxy, DeployService deploy, UiConfigStore config, ILogger<AppPortEnrollment> logger)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(deploy);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        _proxy = proxy;
        _deploy = deploy;
        _config = config;
        _logger = logger;
    }

    /// <summary>Whether the proxy publishes this app's port.</summary>
    public bool IsEnrolled(AppConnection app) =>
        _proxy.Store.Load().Ports.Exists(entry =>
            string.Equals(entry.Target, app.Id, StringComparison.OrdinalIgnoreCase));

    public async Task<EnrollmentResult> EnrollAsync(AppConnection app, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        var repository = GitHubRepositoryParser.Parse(app.RepoUrl);
        var project = ComposeProjectName.FromRepository(repository.Name);
        var container = $"{project}-app-1";
        var envFile = PinqOpsStatePaths.EnvFile(app.ComposeFile);

        var hostPort = EndpointHelpers.TryParsePort(
            EnvFileStore.GetValue(envFile, Deployer.HostPortVariable))
            ?? throw new InvalidOperationException(
                "This app does not record a host port, so there is nothing for the proxy to take over.");

        var containerPort = EndpointHelpers.TryParsePort(
            EnvFileStore.GetValue(envFile, Deployer.ContainerPortVariable))
            ?? DockerfileInspector.DefaultPort;

        await PreflightAsync(app, hostPort);

        var original = await File.ReadAllTextAsync(app.ComposeFile, cancellationToken);
        var rewrite = ComposePortPublication.Rewrite(original, ComposePublishMode.Proxy);
        if (rewrite.Refused)
        {
            throw new InvalidOperationException(rewrite.Blockers[0]);
        }

        var backup = app.ComposeFile + ".bak";
        await File.WriteAllTextAsync(backup, original, cancellationToken);

        try
        {
            // The app has to let go of the port before the proxy can bind it —
            // docker refuses two containers on one host port, and there is no way to
            // hand a listening socket over. This is where the blip starts.
            await File.WriteAllTextAsync(app.ComposeFile, rewrite.Yaml, cancellationToken);
            EnvFileStore.SetValue(envFile, Deployer.AliasVariable, project);
            await ComposeUpAsync(app);

            await _proxy.SetAppPortAsync(app.Id, hostPort, container, containerPort, cancellationToken);
            await _proxy.RepublishAsync(cancellationToken);

            _logger.LogWarning("{App} enrolled: the proxy now publishes {Port}", app.Id, hostPort);
            return new EnrollmentResult(true, hostPort, project, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Every step above is undoable, and this is why: a failure halfway leaves
            // an app publishing nothing and a proxy that does not know about it,
            // which is an app that is simply gone.
            var rollback = await RollbackAsync(app, original, restorePort: true, cancellationToken);
            _logger.LogError(exception, "Enrolling {App} failed; rolled back", app.Id);

            return new EnrollmentResult(
                false, hostPort, project,
                rollback is null
                    ? exception.Message
                    : $"{exception.Message} The rollback also failed: {rollback}");
        }
        finally
        {
            TryDelete(backup);
        }
    }

    public async Task<EnrollmentResult> UnenrollAsync(AppConnection app, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        var original = await File.ReadAllTextAsync(app.ComposeFile, cancellationToken);
        var rewrite = ComposePortPublication.Rewrite(original, ComposePublishMode.HostPort);
        if (rewrite.Refused)
        {
            throw new InvalidOperationException(rewrite.Blockers[0]);
        }

        var envFile = PinqOpsStatePaths.EnvFile(app.ComposeFile);
        var alias = EnvFileStore.GetValue(envFile, Deployer.AliasVariable);

        // What the proxy publishes today, read before it lets go. Everything from
        // the removal to the compose step can fail, and until this was remembered
        // there was nothing to put back — leaving the app published by neither the
        // proxy nor its own container, which is an app that is simply gone.
        var published = _proxy.Store.Load().Ports.Find(entry =>
            string.Equals(entry.Target, app.Id, StringComparison.OrdinalIgnoreCase));

        // An app that publishes its own port can only run one copy — two containers
        // cannot bind the same one. Taking the port back while the project is set to
        // three would leave a deploy that fails on a port collision, from a button
        // about something else, so the count comes down with it.
        var previousReplicas = TakeBackToOneCopy(app.ComposeFile);
        var wasDeployedInColors = StopUsingColors(app.ComposeFile);

        try
        {
            await _proxy.SetAppBalancingAsync(app.Id, null, cancellationToken);

            // The proxy lets go first here, which is the mirror of enrolling: it has
            // the port, and the app cannot bind it while the proxy still holds it.
            await _proxy.RemoveAppPortAsync(app.Id, cancellationToken);
            await _proxy.RepublishAsync(cancellationToken);

            await File.WriteAllTextAsync(app.ComposeFile, rewrite.Yaml, cancellationToken);
            EnvFileStore.RemoveValue(envFile, Deployer.AliasVariable);
            await ComposeUpAsync(app);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var rollback = await PutBackOnTheProxyAsync(
                app, original, alias, published, previousReplicas, wasDeployedInColors, cancellationToken);
            _logger.LogError(exception, "Unenrolling {App} failed; put back on the proxy", app.Id);

            return new EnrollmentResult(
                true, published?.HostPort ?? 0, alias ?? string.Empty,
                rollback is null
                    ? exception.Message
                    : $"{exception.Message} The rollback also failed: {rollback}");
        }

        _logger.LogWarning("{App} unenrolled: it publishes its own port again", app.Id);
        return new EnrollmentResult(false, 0, string.Empty, null);
    }

    /// <summary>
    /// Records that this project runs a single copy again, and returns the count it
    /// had so a failed unenrol can put it back. The chosen balancing policy is left
    /// alone: it costs nothing at one copy and is what the operator picked if they
    /// ever hand the port back.
    /// </summary>
    private int? TakeBackToOneCopy(string composeFile)
    {
        var store = new DeploySettingsStore(composeFile);
        var previous = store.Load().Replicas;
        if (previous <= DeploySettings.DefaultReplicas)
        {
            return null;
        }

        _logger.LogWarning(
            "{Compose} was set to {Replicas} copies; back to one now that it publishes its own port",
            composeFile,
            previous);
        store.Update(settings => settings.Replicas = DeploySettings.DefaultReplicas);
        return previous;
    }

    /// <summary>
    /// Stops the project deploying as two colours, and says whether it was.
    ///
    /// <para>The same prerequisite as the copy count, for the same reason: the two
    /// colours are two projects over one file and only one of them can bind a host
    /// port. Left on, this sequence removes the network alias the colours are reached
    /// by and then asks compose to bring one up — which cannot be worked out without
    /// that alias, so the whole thing fails at its last step every time and the
    /// refusal advises handing the port to the proxy, which is what the operator has
    /// just asked to undo.</para>
    /// </summary>
    private bool StopUsingColors(string composeFile)
    {
        var store = new DeploySettingsStore(composeFile);
        if (!store.Load().BlueGreen)
        {
            return false;
        }

        _logger.LogWarning(
            "{Compose} deployed without a gap; back to the ordinary way now that it publishes its own port",
            composeFile);
        store.Update(settings => settings.BlueGreen = false);
        return true;
    }

    /// <summary>
    /// Undoes a half-finished unenrol: the app goes back to being published by the
    /// proxy, which is where it was working. Returns null on success, or what went
    /// wrong — an app whose rollback also failed needs a person.
    /// </summary>
    private async Task<string?> PutBackOnTheProxyAsync(
        AppConnection app,
        string originalYaml,
        string? alias,
        PortEntry? published,
        int? previousReplicas,
        bool wasDeployedInColors,
        CancellationToken cancellationToken)
    {
        try
        {
            await File.WriteAllTextAsync(app.ComposeFile, originalYaml, cancellationToken);
            if (alias is not null)
            {
                EnvFileStore.SetValue(PinqOpsStatePaths.EnvFile(app.ComposeFile), Deployer.AliasVariable, alias);
            }

            if (previousReplicas is { } replicas)
            {
                new DeploySettingsStore(app.ComposeFile).Update(settings => settings.Replicas = replicas);
            }

            if (wasDeployedInColors)
            {
                new DeploySettingsStore(app.ComposeFile).Update(settings => settings.BlueGreen = true);
            }

            if (published is not null)
            {
                await _proxy.SetAppPortAsync(
                    app.Id, published.HostPort, published.TargetContainer, published.TargetPort, cancellationToken);
                await _proxy.SetAppBalancingAsync(app.Id, published.Upstream?.Balancing, cancellationToken);
                await _proxy.RepublishAsync(cancellationToken);
            }

            await ComposeUpAsync(app);
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Putting {App} back on the proxy failed", app.Id);
            return exception.Message;
        }
    }

    private async Task PreflightAsync(AppConnection app, int hostPort)
    {
        if (!File.Exists(app.ComposeFile))
        {
            throw new InvalidOperationException("This app has no compose project yet — publish it first.");
        }

        if (!await _proxy.IsRunningAsync())
        {
            throw new InvalidOperationException(
                "The proxy has to be installed and running before it can take over a port — "
                + "it is what serves the app afterwards.");
        }

        if (hostPort is ProxyPortSet.HttpPort or ProxyPortSet.HttpsPort)
        {
            throw new InvalidOperationException(
                $"Port {hostPort} is the proxy's own. Give the app a different host port first.");
        }

        var claimed = _proxy.Store.Load().Ports.Find(entry =>
            entry.HostPort == hostPort && !string.Equals(entry.Target, app.Id, StringComparison.OrdinalIgnoreCase));
        if (claimed is not null)
        {
            throw new InvalidOperationException($"The proxy already publishes port {hostPort} for '{claimed.Target}'.");
        }
    }

    private async Task ComposeUpAsync(AppConnection app)
    {
        using var timeout = new CancellationTokenSource(ComposeTimeout);
        var applied = await _deploy.ApplyComposeAsync(app.ComposeFile, timeout.Token);
        if (applied is null)
        {
            // The per-project gate is held by a deploy or a rollback. Recreating the
            // container underneath one is how two versions end up half-running.
            throw new InvalidOperationException(
                "A deploy is in progress for this app. Wait for it to finish and try again.");
        }
    }

    /// <summary>Puts everything back. Returns null on success, or what went wrong.</summary>
    private async Task<string?> RollbackAsync(
        AppConnection app, string originalYaml, bool restorePort, CancellationToken cancellationToken)
    {
        try
        {
            if (restorePort)
            {
                await _proxy.RemoveAppPortAsync(app.Id, cancellationToken);
            }

            await File.WriteAllTextAsync(app.ComposeFile, originalYaml, cancellationToken);
            EnvFileStore.RemoveValue(PinqOpsStatePaths.EnvFile(app.ComposeFile), Deployer.AliasVariable);
            await ComposeUpAsync(app);
            await _proxy.RepublishAsync(cancellationToken);
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Reported rather than swallowed: an app whose rollback failed needs a
            // person, and the backup file beside the compose project is what they
            // will need to know about.
            _logger.LogError(exception, "Rolling {App} back failed", app.Id);
            return exception.Message;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
