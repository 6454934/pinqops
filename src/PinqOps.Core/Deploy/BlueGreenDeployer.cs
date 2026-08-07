using PinqOps.Proxy;

namespace PinqOps.Deploy;

/// <summary>What one coloured deploy did.</summary>
/// <param name="Color">The colour that is serving traffic now.</param>
/// <param name="Switched">Whether the proxy was actually moved to a new colour.</param>
public sealed record BlueGreenResult(bool Succeeded, string Color, bool Switched, string? Error);

/// <summary>
/// Deploys a project as two colours, so a release has no gap: the new version is
/// started alongside the old one, proved healthy, and only then given the traffic.
///
/// <para><b>Separate from <see cref="Deployer"/> on purpose.</b> That sequence is
/// ~130 lines of hard-won cancellation and restore-the-env handling for a
/// single-project deploy, and folding a second project, a colour and a proxy cutover
/// into it would put every one of those paths at risk to add a feature most projects
/// will not turn on.</para>
///
/// <para><b>One rule makes every crash recoverable:</b> the active colour is written
/// to disk only after the proxy has accepted the new configuration. So the recorded
/// colour always describes what the proxy is actually pointing at, and whatever a
/// restart finds, it can tell which half of the cutover happened. Writing it first
/// would leave a record of a switch that never took effect — and the next proxy
/// restart would then quietly finish a deploy that had been abandoned.</para>
/// </summary>
public sealed class BlueGreenDeployer
{
    private readonly IProcessRunner _processRunner;
    private readonly ProxyGateway _proxy;
    private readonly ComposeHealthChecker _healthChecker;
    private readonly ReadinessProbe _readinessProbe;
    private readonly Action<string>? _log;

    public BlueGreenDeployer(
        IProcessRunner processRunner,
        ProxyGateway proxy,
        Action<string>? log = null,
        ComposeHealthChecker? healthChecker = null,
        ReadinessProbe? readinessProbe = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(proxy);
        _processRunner = processRunner;
        _proxy = proxy;
        _log = log;
        _healthChecker = healthChecker ?? new ComposeHealthChecker(processRunner, log);
        _readinessProbe = readinessProbe ?? new ReadinessProbe(processRunner, log: log);
    }

    /// <summary>
    /// Brings up the other colour, proves it, and switches the proxy to it.
    ///
    /// <para>Nothing here touches the colour that is currently serving until the new
    /// one has taken the traffic. A failure at any step before the switch leaves the
    /// live version exactly as it was — the cost is a stopped container nobody is
    /// using, which the next deploy replaces.</para>
    /// </summary>
    public async Task<BlueGreenResult> DeployAsync(
        BlueGreenOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The same cross-process gate the ordinary deployer takes: a CLI deploy
        // and a dashboard rollback interleaved over one project is two operations
        // both reporting success about a state neither produced.
        using var gate = await DeployGate
            .AcquireAsync(options.ComposeFilePath, DeployGate.DefaultWait, _log, cancellationToken)
            .ConfigureAwait(false);

        var settingsStore = new DeploySettingsStore(options.ComposeFilePath);
        var settings = settingsStore.Load();
        var live = DeployColors.Normalize(settings.ActiveColor);
        var target = DeployColors.Other(live);

        var yaml = await File.ReadAllTextAsync(options.ComposeFilePath, cancellationToken).ConfigureAwait(false);
        var eligibility = BlueGreenEligibility.Check(yaml);
        if (!eligibility.Eligible)
        {
            // Refused before anything is started: every blocker here describes a way
            // the two colours would interfere that docker would not report.
            return Failed(live, string.Join(" ", eligibility.Blockers));
        }

        var workingDirectory = PinqOpsStatePaths.ComposeWorkingDirectory(options.ComposeFilePath);
        var envFile = PinqOpsStatePaths.EnvFile(options.ComposeFilePath);

        // Everything from here to the switch can fail, and the project's shared
        // .env is what every ordinary compose action reads. Abandon puts it back;
        // every early exit below goes through it rather than through Failed.
        var pinned = PinnedVersion.Apply(envFile, options.Tag, options.Image);

        BlueGreenResult Abandon(string error)
        {
            pinned.Restore();
            return Failed(live, error);
        }

        var colorEnv = ColorEnvironment.Write(options.ComposeFilePath, target, options.Alias);
        var project = DeployColors.ProjectName(options.Project, target);
        var replicas = DeploySettings.ClampReplicas(settings.Replicas);
        _log?.Invoke($"starting {target} as {project} ({replicas} {(replicas == 1 ? "copy" : "copies")})");

        var failure = await RunAsync(
            DockerComposeCommandBuilder.PullColor(options.ComposeFilePath, project, colorEnv),
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return Abandon($"image pull failed: {failure}");
        }

        failure = await RunAsync(
            DockerComposeCommandBuilder.UpColor(options.ComposeFilePath, project, colorEnv, replicas),
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return Abandon($"starting {target} failed: {failure}");
        }

        var unhealthy = await ProveAsync(options, project, colorEnv, settings, cancellationToken).ConfigureAwait(false);
        if (unhealthy is not null)
        {
            // The live colour never heard about any of this. Leaving the failed one
            // up is deliberate: its logs are the only evidence of why it failed, and
            // the next deploy replaces it anyway.
            _log?.Invoke($"{target} did not come up healthy; {live} is still serving");
            return Abandon(unhealthy);
        }

        var switched = await SwitchAsync(options, target, cancellationToken).ConfigureAwait(false);
        if (switched is not null)
        {
            return Abandon(switched);
        }

        // Only now. Before the reload succeeded, this file would have described a
        // switch that never happened.
        //
        // The colour and nothing else: `settings` was read before the pull, and
        // writing it whole would put back every setting the operator has changed
        // since — the copy count, autoscaling, the colour settings themselves.
        settings = settingsStore.Update(stored => stored.ActiveColor = target);
        _log?.Invoke($"{target} is serving traffic");

        await RetireAsync(options, live, settings, cancellationToken).ConfigureAwait(false);
        return new BlueGreenResult(true, target, Switched: true, null);
    }

    /// <summary>
    /// Switches back to the colour that was live before the last deploy, when it is
    /// still running the version being asked for.
    ///
    /// <para>This is what <see cref="DeploySettings.KeepPreviousColor"/> buys: no
    /// pull, no restart, no health check — the old containers never stopped, and
    /// have already proved they run on this server. It is a proxy reload, so it
    /// finishes in well under a second.</para>
    ///
    /// <para>Returns false when the kept colour is not running the requested version
    /// — which is not a failure, it is the answer that the ordinary redeploy is what
    /// this rollback needs. Deciding that here rather than in the caller keeps the
    /// one question ("is the fast path available?") in the one place that can see
    /// what the other colour is running.</para>
    /// </summary>
    public async Task<bool> TrySwitchBackAsync(
        BlueGreenOptions options, string tag, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        // Under the gate too: the switch rewrites the shared .env and the stored
        // colour, exactly the state a concurrent deploy is in the middle of.
        using var gate = await DeployGate
            .AcquireAsync(options.ComposeFilePath, DeployGate.DefaultWait, _log, cancellationToken)
            .ConfigureAwait(false);

        var settingsStore = new DeploySettingsStore(options.ComposeFilePath);
        var settings = settingsStore.Load();
        if (!settings.BlueGreen || !settings.KeepPreviousColor)
        {
            return false;
        }

        var live = DeployColors.Normalize(settings.ActiveColor);
        var kept = DeployColors.Other(live);
        var keptEnv = ColorEnvironment.FileFor(options.ComposeFilePath, kept);
        if (!File.Exists(keptEnv) || EnvFileStore.GetValue(keptEnv, Deployer.TagVariable) != tag)
        {
            return false;
        }

        // Running, not merely recorded: a container that died overnight would make
        // this switch traffic to nothing, and the ordinary redeploy is right there.
        if (!await IsRunningAsync(options, kept, keptEnv, cancellationToken).ConfigureAwait(false))
        {
            _log?.Invoke($"{kept} is not running any more, so this rollback has to redeploy");
            return false;
        }

        _log?.Invoke($"{kept} is still running {tag}; switching back to it");
        if (await SwitchAsync(options, kept, cancellationToken).ConfigureAwait(false) is { } failure)
        {
            _log?.Invoke(failure);
            return false;
        }

        // The shared .env is the input to every ordinary compose action on this
        // project, not a record of the deploy. Left naming the release the traffic
        // was just taken off, the next variable change or autoscale tick would copy
        // it into the live colour's env and recreate the containers on it — the
        // operator changes a setting and the bad release comes back.
        PinnedVersion.Apply(
            PinqOpsStatePaths.EnvFile(options.ComposeFilePath),
            tag,
            EnvFileStore.GetValue(keptEnv, Deployer.ImageVariable));

        settingsStore.Update(stored => stored.ActiveColor = kept);
        _log?.Invoke($"rolled back to {tag} on {kept}");
        return true;
    }

    /// <summary>Whether one colour's project has a running container.</summary>
    private async Task<bool> IsRunningAsync(
        BlueGreenOptions options, string color, string colorEnv, CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
            "docker",
            DockerComposeCommandBuilder.PsColor(
                options.ComposeFilePath, DeployColors.ProjectName(options.Project, color), colorEnv),
            PinqOpsStatePaths.ComposeWorkingDirectory(options.ComposeFilePath),
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return false;
        }

        foreach (var service in JsonLines.Parse(result.StandardOutput))
        {
            if (service.TryGetProperty("State", out var state)
                && state.ValueKind == System.Text.Json.JsonValueKind.String
                && string.Equals(state.GetString(), "running", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Points every route for this app at the new colour and reloads the proxy.
    /// Returns null on success, or why the switch did not happen.
    /// </summary>
    private async Task<string?> SwitchAsync(
        BlueGreenOptions options, string target, CancellationToken cancellationToken)
    {
        var alias = DeployColors.Alias(options.Alias, target);
        var pointed = 0;
        var replaced = new List<LoadBalancing?>();

        var applied = await _proxy.Update(
            config =>
            {
                pointed = 0;
                replaced.Clear();
                foreach (var upstream in RoutesFor(config, options.Target))
                {
                    replaced.Add(upstream.Balancing);
                    // Always the dynamic form, whatever the replica count. One copy
                    // resolves to one container, and it keeps the switch a single
                    // change of one value rather than a rewrite of the container
                    // names as well.
                    upstream.Balancing = new LoadBalancing
                    {
                        Alias = alias,
                        Policy = options.Policy,
                    };
                    pointed++;
                }
            },
            cancellationToken).ConfigureAwait(false);

        if (applied.Failed)
        {
            // The Caddyfile on disk was not replaced, so the proxy keeps serving the
            // live colour. The stored config is another matter: it was written before
            // Caddy was asked, and it is what every later regeneration reads — so
            // leaving it naming this colour would have the next domain change or
            // preview quietly install a switch that was reported as failed.
            await UndoRoutesAsync(options, target, replaced, cancellationToken).ConfigureAwait(false);
            return $"the proxy would not accept the new routes, so {options.Target} was not switched: {applied.Error}";
        }

        if (pointed == 0)
        {
            return $"the proxy has no route for '{options.Target}', so there is nothing to switch traffic to. "
                + "Hand the app's host port to the proxy, or give it a domain, first.";
        }

        _log?.Invoke($"proxy now forwards {options.Target} to {alias} ({pointed} routes)");
        return null;
    }

    /// <summary>
    /// Puts the routes back the way this switch found them, after the proxy refused
    /// to serve them.
    ///
    /// <para>Only the routes this switch touched, rather than the whole stored file:
    /// the reload happens outside the store's lock on purpose, so another edit may
    /// have landed in between and restoring a snapshot would throw it away.</para>
    /// </summary>
    private async Task UndoRoutesAsync(
        BlueGreenOptions options,
        string target,
        IReadOnlyList<LoadBalancing?> replaced,
        CancellationToken cancellationToken)
    {
        if (replaced.Count == 0)
        {
            return;
        }

        var undone = await _proxy.Update(
            config =>
            {
                var routes = RoutesFor(config, options.Target);
                for (var index = 0; index < routes.Count && index < replaced.Count; index++)
                {
                    routes[index].Balancing = replaced[index];
                }
            },
            cancellationToken).ConfigureAwait(false);

        if (undone.Failed)
        {
            // The record itself is written before the proxy is asked, so it is
            // already back; only the reload of the restored routes failed, and the
            // proxy is serving them regardless because they are what it had.
            _log?.Invoke($"the proxy would not reload the routes being put back after the refused switch to "
                + $"{target}: {undone.Error}");
        }
    }

    /// <summary>
    /// Gives the retiring colour time to finish what it is holding, then takes it
    /// down unless it is being kept for an instant rollback.
    /// </summary>
    private async Task RetireAsync(
        BlueGreenOptions options, string retiring, DeploySettings settings, CancellationToken cancellationToken)
    {
        // In-flight requests were routed to the old colour a moment ago and are
        // still being answered by it. Cutting it now is the one way this design can
        // drop a request that the old design never would.
        if (options.DrainSeconds > 0)
        {
            _log?.Invoke($"letting {retiring} finish in-flight requests for {options.DrainSeconds}s");
            await Task.Delay(TimeSpan.FromSeconds(options.DrainSeconds), cancellationToken).ConfigureAwait(false);
        }

        if (settings.KeepPreviousColor)
        {
            _log?.Invoke($"{retiring} is kept running with no traffic, so a rollback is a proxy reload");
            return;
        }

        var colorEnv = ColorEnvironment.FileFor(options.ComposeFilePath, retiring);
        var project = DeployColors.ProjectName(options.Project, retiring);
        var failure = await RunAsync(
            DockerComposeCommandBuilder.DownColor(options.ComposeFilePath, project, colorEnv),
            PinqOpsStatePaths.ComposeWorkingDirectory(options.ComposeFilePath),
            cancellationToken).ConfigureAwait(false);

        // Reported, never fatal: traffic is already on the new colour, and a deploy
        // that succeeded must not be called failed because a stopped container would
        // not stop.
        _log?.Invoke(failure is null ? $"{retiring} stopped" : $"could not stop {retiring}: {failure}");
    }

    /// <summary>
    /// The two gates the new colour has to pass before it is given any traffic:
    /// docker settling the containers, then — when it is on — the application
    /// answering a request.
    /// </summary>
    private async Task<string?> ProveAsync(
        BlueGreenOptions options,
        string project,
        string colorEnv,
        DeploySettings settings,
        CancellationToken cancellationToken)
    {
        if (options.HealthCheckTimeout > TimeSpan.Zero)
        {
            var unhealthy = await _healthChecker
                .WaitForHealthyAsync(
                    options.ComposeFilePath,
                    options.HealthCheckTimeout,
                    cancellationToken,
                    project,
                    colorEnv)
                .ConfigureAwait(false);
            if (unhealthy is not null)
            {
                return unhealthy;
            }
        }

        if (!settings.Readiness.Enabled)
        {
            return null;
        }

        var containers = await AppContainersAsync(options.ComposeFilePath, project, colorEnv, cancellationToken)
            .ConfigureAwait(false);
        if (containers.Count == 0)
        {
            return "readiness probe: compose reports no application container to ask.";
        }

        // Every copy, not just the first. The proxy round-robins across all of
        // them the moment the switch lands — a three-copy deploy where one copy
        // serves and two failed to bind passed a first-container probe and then
        // failed two thirds of the traffic on a deploy reported green.
        foreach (var container in containers)
        {
            var notReady = await _readinessProbe
                .WaitForReadyAsync(settings.Readiness, container, options.ContainerPort, cancellationToken)
                .ConfigureAwait(false);
            if (notReady is not null)
            {
                return containers.Count > 1 ? $"{notReady} ({container})" : notReady;
            }
        }

        return null;
    }

    /// <summary>Every container of one colour's project, in compose's order.</summary>
    private async Task<IReadOnlyList<string>> AppContainersAsync(
        string composeFilePath, string project, string colorEnv, CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
            "docker",
            DockerComposeCommandBuilder.PsColor(composeFilePath, project, colorEnv),
            PinqOpsStatePaths.ComposeWorkingDirectory(composeFilePath),
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return [];
        }

        var containers = new List<string>();
        foreach (var service in JsonLines.Parse(result.StandardOutput))
        {
            if (service.TryGetProperty("Name", out var name)
                && name.ValueKind == System.Text.Json.JsonValueKind.String
                && name.GetString() is { Length: > 0 } container)
            {
                containers.Add(container);
            }
        }

        return containers;
    }

    /// <summary>The upstream options of every route for an app, created where absent.</summary>
    private static List<UpstreamOptions> RoutesFor(DomainConfig config, string target)
    {
        var routes = new List<UpstreamOptions>();

        foreach (var entry in config.Ports)
        {
            if (string.Equals(entry.Target, target, StringComparison.OrdinalIgnoreCase))
            {
                routes.Add(entry.Upstream ??= new UpstreamOptions());
            }
        }

        foreach (var entry in config.Domains)
        {
            if (string.Equals(entry.Target, target, StringComparison.OrdinalIgnoreCase))
            {
                routes.Add(entry.Upstream ??= new UpstreamOptions());
            }
        }

        return routes;
    }

    private async Task<string?> RunAsync(
        IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken cancellationToken)
    {
        var result = await _processRunner
            .RunAsync("docker", arguments, workingDirectory, cancellationToken)
            .ConfigureAwait(false);

        if (result.Succeeded)
        {
            return null;
        }

        var detail = result.StandardError.Trim();
        return detail.Length > 0 ? detail : $"exit {result.ExitCode}";
    }

    private BlueGreenResult Failed(string liveColor, string error)
    {
        _log?.Invoke(error);
        return new BlueGreenResult(false, liveColor, Switched: false, error);
    }
}

/// <summary>What one coloured deploy needs to know.</summary>
public sealed record BlueGreenOptions
{
    public required string ComposeFilePath { get; init; }

    /// <summary>The app id the proxy's routes name.</summary>
    public required string Target { get; init; }

    /// <summary>The compose project name the colours are derived from.</summary>
    public required string Project { get; init; }

    /// <summary>The unqualified network alias; each colour gets its own from this.</summary>
    public required string Alias { get; init; }

    /// <summary>The port the containers listen on inside the network.</summary>
    public int ContainerPort { get; init; } = DockerfileInspector.DefaultPort;

    public string? Tag { get; init; }

    public string? Image { get; init; }

    public string Policy { get; init; } = LoadBalancingPolicies.RoundRobin;

    public TimeSpan HealthCheckTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long the retiring colour is left up after the switch. Requests routed to
    /// it a moment ago are still being answered by it.
    /// </summary>
    public int DrainSeconds { get; init; } = 15;
}
