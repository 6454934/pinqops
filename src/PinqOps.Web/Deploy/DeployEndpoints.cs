using PinqOps;
using PinqOps.Deploy;
using PinqOps.Proxy;
using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>
/// Deploy history, state and rollback for a compose project.
/// </summary>
public static class DeployEndpoints
{
    public static void MapDeployEndpoints(this IEndpointRouteBuilder app, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logger);

        app.MapGet("/api/deploy/state", async Task<object?> (HttpContext context, UiConfigStore store, DeployService deploys) =>
        {
            await Task.CompletedTask;
            return deploys.GetState(ResolveApp(store, context).ComposeFile);
        });

        app.MapGet("/api/deploy/history", async Task<object?> (HttpContext context, UiConfigStore store, DeployService deploys) =>
        {
            await Task.CompletedTask;
            return new { items = deploys.History(ResolveApp(store, context).ComposeFile) };
        });
        app.MapPost("/api/deploy/rollback", async (HttpContext context, UiConfigStore store, DeployService deploys) =>
        {
            RollbackRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<RollbackRequest>();
            }
            catch (System.Text.Json.JsonException)
            {
                return Error(400, "Invalid request body.");
            }

            if (request?.Tag is not { Length: > 0 } tag)
            {
                return Error(400, "A tag is required.");
            }

            try
            {
                var job = deploys.TryStartRollback(ResolveApp(store, context).ComposeFile, tag);
                if (job is null)
                {
                    return Error(409, "A rollback is already in progress.");
                }

                logger.LogWarning("Rollback to {Tag} started from the dashboard", tag);
                return Results.Json(new { jobId = job.Id });
            }
            catch (ArgumentException exception)
            {
                return Error(400, exception.Message);
            }
            catch (InvalidOperationException exception)
            {
                return Error(400, exception.Message);
            }
        });

        app.MapGet("/api/deploy/job/{jobId}", (string jobId, DeployService deploys) =>
        {
            var job = deploys.Find(jobId);
            return job is null
                ? Error(404, "Unknown rollback job.")
                : Results.Json(new { tag = job.Tag, phase = job.Phase, done = job.Done, error = job.Error, log = job.Log() });
        });

        app.MapGet("/api/deploy/readiness", async Task<object?> (HttpContext context, UiConfigStore store) =>
        {
            await Task.CompletedTask;
            var settings = new DeploySettingsStore(ResolveApp(store, context).ComposeFile).Load().Readiness;
            return Describe(settings);
        });

        app.MapPost("/api/deploy/readiness", async (HttpContext context, UiConfigStore store) =>
        {
            ReadinessRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<ReadinessRequest>();
            }
            catch (System.Text.Json.JsonException)
            {
                return Error(400, "Invalid request body.");
            }

            if (request is null)
            {
                return Error(400, "Invalid request body.");
            }

            // Rejected rather than cleaned up: a path quietly rewritten to "/" would
            // leave the operator believing the probe checks /healthz when it does
            // not, and a probe checking the wrong thing is worse than none.
            if (!ReadinessSettings.TryNormalizePath(request.Path, out var path))
            {
                return Error(
                    400,
                    $"'{request.Path}' is not a request path. It must start with '/' and carry no spaces or backslashes.");
            }

            if (request.ExpectedStatusFrom > request.ExpectedStatusTo)
            {
                return Error(
                    400,
                    $"The status range {request.ExpectedStatusFrom}-{request.ExpectedStatusTo} accepts nothing. "
                    + "The lower bound has to come first.");
            }

            var composeFile = ResolveApp(store, context).ComposeFile;
            var settingsStore = new DeploySettingsStore(composeFile);
            var settings = settingsStore.Load();

            // Stored already clamped, so what the dashboard shows next is what the
            // deploy will actually do — and the response says so rather than leaving
            // the operator to compare.
            settings.Readiness = new ReadinessSettings
            {
                Enabled = request.Enabled,
                Path = path,
                ExpectedStatusFrom = request.ExpectedStatusFrom,
                ExpectedStatusTo = request.ExpectedStatusTo,
                IntervalSeconds = request.IntervalSeconds,
                TimeoutSeconds = request.TimeoutSeconds,
                RequestTimeoutSeconds = request.RequestTimeoutSeconds,
                ConsecutiveSuccesses = request.ConsecutiveSuccesses,
            }.Normalized();

            settingsStore.Save(settings);
            logger.LogWarning(
                "Readiness probe for {Compose} is now {State}", composeFile, settings.Readiness.Enabled ? "on" : "off");

            return Results.Json(Describe(settings.Readiness));
        });

        app.MapGet("/api/deploy/scale", async Task<object?> (HttpContext context, UiConfigStore store) =>
        {
            await Task.CompletedTask;
            var connection = ResolveApp(store, context);
            var settings = new DeploySettingsStore(connection.ComposeFile).Load();
            return DescribeScale(settings, ReplicaAlias(connection.ComposeFile));
        });

        app.MapPost("/api/deploy/scale", async (HttpContext context, UiConfigStore store, ProxyService proxy) =>
        {
            ScaleRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<ScaleRequest>();
            }
            catch (System.Text.Json.JsonException)
            {
                return Error(400, "Invalid request body.");
            }

            if (request is null)
            {
                return Error(400, "Invalid request body.");
            }

            if (!LoadBalancingPolicies.IsKnown(request.Policy))
            {
                return Error(400, $"'{request.Policy}' is not a balancing policy pinqops knows.");
            }

            var connection = ResolveApp(store, context);
            var replicas = DeploySettings.ClampReplicas(request.Replicas);
            var alias = ReplicaAlias(connection.ComposeFile);

            // Refused here rather than at the next deploy: two containers cannot bind
            // one host port, so the deploy would fail on "port is already allocated"
            // — which reads as somebody else holding the port, not as the app holding
            // it against itself.
            if (replicas > 1 && alias is null)
            {
                return Error(
                    400,
                    "This app publishes its own host port, and two containers cannot bind the same one. "
                    + "Hand the port to the proxy first, then run more than one copy.");
            }

            var settingsStore = new DeploySettingsStore(connection.ComposeFile);
            var settings = settingsStore.Load();
            settings.Replicas = replicas;
            settings.BalancingPolicy = request.Policy!;

            if (request.Autoscale is { } autoscale)
            {
                // Stored already clamped, so the numbers the page shows next are the
                // ones the controller will act on.
                settings.Autoscale = new AutoscaleSettings
                {
                    Enabled = autoscale.Enabled,
                    MinReplicas = autoscale.MinReplicas,
                    MaxReplicas = autoscale.MaxReplicas,
                    TargetCpuPercent = autoscale.TargetCpuPercent,
                    TargetMemoryPercent = autoscale.TargetMemoryPercent,
                    HoldSeconds = autoscale.HoldSeconds,
                    ScaleUpCooldownSeconds = autoscale.ScaleUpCooldownSeconds,
                    ScaleDownCooldownSeconds = autoscale.ScaleDownCooldownSeconds,
                }.Normalized();

                if (settings.Autoscale.Enabled && alias is null)
                {
                    return Error(
                        400,
                        "This app publishes its own host port, and two containers cannot bind the same one. "
                        + "Hand the port to the proxy first, then run more than one copy.");
                }
            }

            settingsStore.Save(settings);

            // Pointing the routes at the replica set is the half that takes effect
            // now; the containers follow on the next deploy. Both are reported, so
            // "I set it to three and nothing happened" has an answer on the page.
            var routes = await proxy.SetAppBalancingAsync(
                connection.Id,
                RoutedBalancing(settings, alias, replicas, request.Policy!));

            logger.LogWarning(
                "{App} is now set to {Replicas} copies ({Policy}), {Routes} routes updated",
                connection.Id,
                replicas,
                request.Policy,
                routes);

            return Results.Json(DescribeScale(settings, alias, routes));
        });

        app.MapGet("/api/deploy/bluegreen", async Task<object?> (HttpContext context, UiConfigStore store) =>
        {
            await Task.CompletedTask;
            var connection = ResolveApp(store, context);
            return DescribeColors(connection.ComposeFile, new DeploySettingsStore(connection.ComposeFile).Load());
        });

        app.MapPost("/api/deploy/bluegreen", async (
            HttpContext context, UiConfigStore store, ProxyService proxy, DeployService deploys) =>
        {
            BlueGreenRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<BlueGreenRequest>();
            }
            catch (System.Text.Json.JsonException)
            {
                return Error(400, "Invalid request body.");
            }

            if (request is null)
            {
                return Error(400, "Invalid request body.");
            }

            var connection = ResolveApp(store, context);
            var settingsStore = new DeploySettingsStore(connection.ComposeFile);
            var settings = settingsStore.Load();

            if (request.Enabled)
            {
                // Refused at the form rather than at the deploy: each of these is a
                // way the two colours would interfere that docker reports as
                // something else entirely, or not at all.
                if (!File.Exists(connection.ComposeFile))
                {
                    return Error(400, "This app has no compose project yet — publish it first.");
                }

                var eligibility = BlueGreenEligibility.Check(await File.ReadAllTextAsync(connection.ComposeFile));
                if (!eligibility.Eligible)
                {
                    return Error(400, string.Join(" ", eligibility.Blockers));
                }

                if (ReplicaAlias(connection.ComposeFile) is null)
                {
                    return Error(
                        400,
                        "This app publishes its own host port, so there is no way to reach one colour rather "
                        + "than the other. Hand its host port to the proxy first.");
                }

                // Recorded now, because the CLI that performs the deploy knows only a
                // compose file path and would otherwise have to guess this.
                settings.ProxyTarget = connection.Id;
            }

            var wasDeployedInColors = settings.BlueGreen;
            settings.BlueGreen = request.Enabled;
            settings.KeepPreviousColor = request.KeepPreviousColor;
            settingsStore.Save(settings);

            if (wasDeployedInColors && !request.Enabled)
            {
                try
                {
                    await StopUsingColorsAsync(connection.Id, connection.ComposeFile, settings, deploys, proxy);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Put the setting back rather than leave the app deploying the
                    // ordinary way into routes that still name a colour — that state
                    // is the silent one, where every later deploy reports success and
                    // serves nobody.
                    settingsStore.Update(stored => stored.BlueGreen = true);
                    logger.LogError(exception, "{App} could not be taken off colours", connection.Id);
                    return Error(
                        400,
                        "Turning deploys-without-a-gap off failed, so the app is still on colours: "
                        + exception.Message);
                }
            }

            logger.LogWarning(
                "{App} deploys {How}",
                connection.Id,
                request.Enabled ? $"without a gap, currently on {settings.ActiveColor}" : "the ordinary way");

            return Results.Json(DescribeColors(connection.ComposeFile, settings));
        });
    }

    private static object DescribeColors(string composeFile, DeploySettings settings)
    {
        // What is in the way, listed before it is switched on rather than after —
        // the page can then say which line to change instead of "not eligible".
        var blockers = File.Exists(composeFile)
            ? BlueGreenEligibility.Check(File.ReadAllText(composeFile)).Blockers
            : ["This app has no compose project yet — publish it first."];

        return new
        {
            enabled = settings.BlueGreen,
            activeColor = settings.ActiveColor,
            keepPreviousColor = settings.KeepPreviousColor,
            eligible = blockers.Count == 0 && ReplicaAlias(composeFile) is not null,
            blockers,
            hasAlias = ReplicaAlias(composeFile) is not null,
        };
    }

    private sealed record BlueGreenRequest(bool Enabled, bool KeepPreviousColor);

    /// <summary>
    /// The network alias every replica of this app answers on, or null when the app
    /// still publishes its own port — which is the same thing as "it cannot run more
    /// than one copy". Read from the project's <c>.env</c> rather than derived from
    /// the repository name, because that is the value compose actually puts on the
    /// containers.
    /// </summary>
    /// <summary>
    /// What this app's proxy routes should point at once the copy count is saved.
    /// Null means no replica set, i.e. fall back to the static upstream.
    ///
    /// <para>Under blue-green the answer is never the plain alias and never null.
    /// The containers of a colour answer on <c>&lt;alias&gt;-&lt;colour&gt;</c> and
    /// on nothing else — that is what keeps two versions from splitting traffic —
    /// so writing the unqualified name here points the routes at nothing, and
    /// clearing the replica set points them at a static upstream named after a
    /// container no colour creates. Either one takes the app off the air until the
    /// next deploy, for what the operator did as an edit to a number.</para>
    ///
    /// <para>The rule is <see cref="ColorReconciler"/>'s, deliberately: that is
    /// what re-derives these routes at every restart, so anything that disagrees
    /// with it is undone on the next one.</para>
    /// </summary>
    internal static LoadBalancing? RoutedBalancing(
        DeploySettings settings, string? alias, int replicas, string policy)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (alias is null || (!settings.BlueGreen && replicas <= 1))
        {
            return null;
        }

        return new LoadBalancing { Alias = RoutedAlias(settings, alias)!, Policy = policy };
    }

    /// <summary>
    /// The network name this app's routes actually resolve. The page quotes it to
    /// the operator as the name to look for, so under blue-green it has to be the
    /// colour-qualified one — the plain name resolves to nothing there.
    /// </summary>
    internal static string? RoutedAlias(DeploySettings settings, string? alias)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return alias is not null && settings.BlueGreen
            ? DeployColors.Alias(alias, settings.ActiveColor)
            : alias;
    }

    /// <summary>
    /// Brings an app back onto its ordinary project after deploys-without-a-gap are
    /// turned off, and points its proxy routes at it.
    ///
    /// <para><b>Why anything happens at all.</b> A colour's containers answer on
    /// <c>&lt;alias&gt;-&lt;colour&gt;</c> and on nothing else, and the routes were
    /// pointed there by the last cutover. Saving the setting does not move them, and
    /// <see cref="ColorReconciler"/> — which re-derives these routes at every restart
    /// — skips an app that is no longer deploying in colours, so nothing ever
    /// corrects them. Every later ordinary deploy then creates containers on the plain
    /// alias, reports success, and is served to nobody while the abandoned colour goes
    /// on answering: the release from before the toggle stays live indefinitely.</para>
    ///
    /// <para><b>Why the project comes up first.</b> Re-pointing on its own would aim
    /// the routes at a name nothing answers until the next deploy — an app taken off
    /// the air by a settings toggle, which is the failure the colour-aware replica
    /// rule exists to avoid. The ordinary <c>up</c> runs first so something is
    /// already answering the plain alias when the routes arrive. The retired colour
    /// is left running: it is what was serving a moment ago, and stopping it is the
    /// operator's call.</para>
    /// </summary>
    internal static async Task StopUsingColorsAsync(
        string appId,
        string composeFile,
        DeploySettings settings,
        DeployService deploy,
        ProxyService proxy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(deploy);
        ArgumentNullException.ThrowIfNull(proxy);

        // ApplyComposeAsync answers null when a deploy or rollback holds this
        // project's gate — nothing was brought up. Moving the routes anyway would
        // aim them at the plain alias nothing answers yet: the exact outage the
        // up-before-routes ordering exists to prevent. Throwing instead lands in
        // the caller's catch, which puts the BlueGreen setting back.
        if (await deploy.ApplyComposeAsync(composeFile, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException(
                "a deploy or rollback is in progress for this project — try again when it finishes.");
        }

        await proxy.SetAppBalancingAsync(
            appId,
            RoutedBalancing(settings, ReplicaAlias(composeFile), settings.Replicas, settings.BalancingPolicy),
            cancellationToken).ConfigureAwait(false);
    }

    private static string? ReplicaAlias(string composeFile)
    {
        var alias = EnvFileStore.GetValue(PinqOpsStatePaths.EnvFile(composeFile), Deployer.AliasVariable)?.Trim();
        return PinqOps.Proxy.CaddyfileGenerator.IsEmittableName(alias) ? alias : null;
    }

    private static object DescribeScale(DeploySettings settings, string? alias, int? routesUpdated = null)
    {
        var autoscale = settings.Autoscale.Normalized();
        // The name the routes resolve, which under blue-green is not the one in the
        // .env — and the page quotes it as the name to go and look for.
        var resolvedAlias = RoutedAlias(settings, alias);
        return new
        {
            replicas = settings.Replicas,
            policy = settings.BalancingPolicy,
            policies = LoadBalancingPolicies.All,
            // What the page needs to explain why the replica field is refused,
            // without making the operator guess which prerequisite is missing.
            canScale = alias is not null,
            alias = resolvedAlias,
            routesUpdated,
            autoscale = new
            {
                enabled = settings.Autoscale.Enabled,
                minReplicas = autoscale.MinReplicas,
                maxReplicas = autoscale.MaxReplicas,
                targetCpuPercent = autoscale.TargetCpuPercent,
                targetMemoryPercent = autoscale.TargetMemoryPercent,
                holdSeconds = autoscale.HoldSeconds,
                scaleUpCooldownSeconds = autoscale.ScaleUpCooldownSeconds,
                scaleDownCooldownSeconds = autoscale.ScaleDownCooldownSeconds,
            },
        };
    }

    private sealed record ScaleRequest(int Replicas, string? Policy, AutoscaleRequest? Autoscale);

    /// <summary>
    /// Optional on the request, so a client that only changes the copy count cannot
    /// silently reset the controller's bounds to defaults nobody chose.
    /// </summary>
    private sealed record AutoscaleRequest(
        bool Enabled,
        int MinReplicas,
        int MaxReplicas,
        int TargetCpuPercent,
        int TargetMemoryPercent,
        int HoldSeconds,
        int ScaleUpCooldownSeconds,
        int ScaleDownCooldownSeconds);

    private static object Describe(ReadinessSettings settings) => new
    {
        enabled = settings.Enabled,
        path = settings.Path,
        expectedStatusFrom = settings.ExpectedStatusFrom,
        expectedStatusTo = settings.ExpectedStatusTo,
        intervalSeconds = settings.IntervalSeconds,
        timeoutSeconds = settings.TimeoutSeconds,
        requestTimeoutSeconds = settings.RequestTimeoutSeconds,
        consecutiveSuccesses = settings.ConsecutiveSuccesses,
    };

    /// <summary>
    /// Every field required, so a form that forgets one cannot silently reset it to
    /// a default the operator never chose.
    /// </summary>
    private sealed record ReadinessRequest(
        bool Enabled,
        string? Path,
        int ExpectedStatusFrom,
        int ExpectedStatusTo,
        int IntervalSeconds,
        int TimeoutSeconds,
        int RequestTimeoutSeconds,
        int ConsecutiveSuccesses);
}
