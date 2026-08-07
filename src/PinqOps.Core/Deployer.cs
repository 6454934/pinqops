using System.Globalization;
using System.Text.Json;
using PinqOps.Deploy;

namespace PinqOps;

/// <summary>
/// Runs the fixed deploy sequence against a single, predefined compose project:
/// pin the requested tag in the project's <c>.env</c>, pull, recreate the
/// containers, verify they come up healthy, then record the outcome and keep a
/// bounded set of images for rollback. The process runner is injected so the
/// sequence is unit-testable without Docker.
/// </summary>
public sealed class Deployer
{
    private const string DockerExecutable = "docker";
    public const string TagVariable = "PINQOPS_TAG";
    public const string ImageVariable = "PINQOPS_IMAGE";

    /// <summary>Host port the compose project publishes on (user-editable).</summary>
    public const string HostPortVariable = "PINQOPS_HOST_PORT";

    /// <summary>Port inside the container the app listens on (user-editable).</summary>
    public const string ContainerPortVariable = "PINQOPS_CONTAINER_PORT";

    /// <summary>
    /// The network alias the app answers to inside docker. The proxy forwards to
    /// this rather than to a container name, which is what will let more than one
    /// container serve one app.
    ///
    /// <para>Deploy-managed, and that is load-bearing: two projects sharing an alias
    /// share traffic. A hand-edited value that survived would be a preview or a
    /// half-finished deploy silently joining production's pool.</para>
    /// </summary>
    public const string AliasVariable = "PINQOPS_ALIAS";

    /// <summary>
    /// Whether a compose <c>.env</c> key is owned by deploy/rollback. Each is
    /// re-pinned on every deploy, so editing one by hand silently disappears —
    /// callers surface them as read-only rather than accept the edit.
    /// </summary>
    public static bool IsDeployManagedVariable(string key) =>
        key == TagVariable || key == ImageVariable || key == AliasVariable;

    private readonly IProcessRunner _processRunner;
    private readonly Action<string>? _log;
    private readonly DeployHistoryStore? _history;
    private readonly IDeployObserver? _observer;
    private readonly ComposeHealthChecker _healthChecker;
    private readonly ReadinessProbe _readinessProbe;
    private readonly ImageRetentionPruner _retentionPruner;

    public Deployer(
        IProcessRunner processRunner,
        Action<string>? log = null,
        DeployHistoryStore? history = null,
        IDeployObserver? observer = null,
        ComposeHealthChecker? healthChecker = null,
        ReadinessProbe? readinessProbe = null)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _log = log;
        _history = history;
        _observer = observer;
        _healthChecker = healthChecker ?? new ComposeHealthChecker(processRunner, log);
        _readinessProbe = readinessProbe ?? new ReadinessProbe(processRunner, log: log);
        _retentionPruner = new ImageRetentionPruner(processRunner, log);
    }

    /// <summary>
    /// Runs the deploy sequence. Returns true only when pull, up and the health
    /// check (when enabled) all succeed. There is no automatic rollback: a
    /// failed deploy is recorded and reported, and rolling back is an explicit
    /// user action.
    /// </summary>
    public async Task<bool> DeployAsync(DeployOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Across processes, not just this one: the runner CLI and the dashboard
        // both drive this sequence over the same .env, and interleaving them
        // recreates the containers on whichever tag was pinned last while history
        // records the other operation as successful. Taken before the deploy
        // timeout starts, so queueing behind a slow deploy does not eat this
        // one's budget.
        using var gate = await Deploy.DeployGate
            .AcquireAsync(options.ComposeFilePath, Deploy.DeployGate.DefaultWait, _log, cancellationToken)
            .ConfigureAwait(false);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(options.Timeout);
        var token = timeoutSource.Token;

        var startedAt = DateTimeOffset.UtcNow;
        var envFile = PinqOpsStatePaths.EnvFile(options.ComposeFilePath);
        var composeDirectory = PinqOpsStatePaths.ComposeWorkingDirectory(options.ComposeFilePath);
        var previousTag = EnvFileStore.GetValue(envFile, TagVariable);
        var previousImage = EnvFileStore.GetValue(envFile, ImageVariable);
        // Mutable so the cancellation handlers below can record the health state
        // the sequence had actually reached when docker was killed.
        var health = new HealthProgress();

        // Every step below runs under the timeout token, and ProcessRunner rethrows
        // the cancellation after killing the child. Without catching it here the
        // one path that matters most — docker was killed mid-flight, so the project
        // may be half-deployed — was the only path that wrote no history record and
        // sent no notification: the operator saw "A task was canceled." and nothing
        // else.
        try
        {
            return await RunSequenceAsync(
                    options, startedAt, envFile, composeDirectory, previousTag, previousImage, health, token,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested
                                                 && !cancellationToken.IsCancellationRequested)
        {
            var timedOut =
                $"deploy timed out after {options.Timeout.TotalSeconds:0}s — docker was killed mid-flight, so the "
                + "compose project may be partly updated; check the containers";
            _log?.Invoke(timedOut);
            await FinishAsync(options, startedAt, DeployRecordValues.ResultFailed, previousTag, health.State, timedOut, cancellationToken)
                .ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            // The caller cancelled. Record it before propagating, using a fresh
            // token so the write and the notification are not cancelled too.
            const string cancelled = "deploy was cancelled — docker was killed mid-flight, so the compose project "
                + "may be partly updated; check the containers";
            _log?.Invoke(cancelled);
            await FinishAsync(options, startedAt, DeployRecordValues.ResultFailed, previousTag, health.State, cancelled, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The pull/up/health/prune sequence. Split out of
    /// <see cref="DeployAsync"/> so a cancellation anywhere inside it lands in one
    /// place that still records and notifies the outcome.
    /// </summary>
    private async Task<bool> RunSequenceAsync(
        DeployOptions options,
        DateTimeOffset startedAt,
        string envFile,
        string? composeDirectory,
        string? previousTag,
        string? previousImage,
        HealthProgress health,
        CancellationToken token,
        CancellationToken cancellationToken)
    {
        // Before anything else: this deployer is colour-blind. It runs `compose up`
        // with no -p and no colour environment file, so against a project deployed
        // as two colours it starts a THIRD compose project — a second copy of the
        // app that nothing routes to, holding the same external volumes — and then
        // reports the deploy as done.
        //
        // Refused here rather than at each caller. Both rollback paths, the CLI's
        // and the dashboard's, fall back to this deployer when the fast colour
        // switch declines, and neither noticed; there is no caller for which the
        // colour-blind path is the right one on a coloured project.
        if (new Deploy.DeploySettingsStore(options.ComposeFilePath).Load().BlueGreen)
        {
            const string Coloured =
                "This project is deployed as two colours, and this path cannot deploy one — it would start a "
                + "third copy that nothing routes to. Deploy or roll back through the coloured path instead.";
            await FinishAsync(options, startedAt, DeployRecordValues.ResultFailed, previousTag, health.State, Coloured, cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        if (options.ExpectedImage is not null)
        {
            // BEFORE touching the .env: does this project even belong to us? The
            // project name is the owning repository's, and it is the only durable
            // marker — pinning the image first would make every later check pass
            // while quietly hijacking another application's project.
            var wrongProject = FindProjectOwnerMismatch(options.ExpectedImage, options.ComposeFilePath);
            if (wrongProject is not null)
            {
                await FinishAsync(options, startedAt, DeployRecordValues.ResultFailed, previousTag, health.State, wrongProject, cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }

            // Pin the image so the compose resolves to what this deploy is for; a
            // repository rename then flows straight through the workflow's
            // --image with no stale compose file to fix by hand.
            EnvFileStore.SetValue(envFile, ImageVariable, options.ExpectedImage);
            _log?.Invoke($"pinned {ImageVariable}={options.ExpectedImage}");

            var mismatch = await FindImageMismatchAsync(options.ExpectedImage, options.ComposeFilePath, token)
                .ConfigureAwait(false);
            if (mismatch is not null)
            {
                // Nothing was applied; leave the .env describing what is running.
                RestoreEnvValue(envFile, ImageVariable, previousImage);
                await FinishAsync(options, startedAt, DeployRecordValues.ResultFailed, previousTag, health.State, mismatch, cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }
        }

        if (options.Tag is not null)
        {
            EnvFileStore.SetValue(envFile, TagVariable, options.Tag);
            _log?.Invoke($"pinned {TagVariable}={options.Tag}");
        }

        var pullNeeded = true;
        if (options.Trigger == DeployRecordValues.TriggerRollback)
        {
            pullNeeded = !await ImagesPresentLocallyAsync(options.ComposeFilePath, token).ConfigureAwait(false);
            if (!pullNeeded)
            {
                _log?.Invoke("rollback target image found locally; skipping pull");
            }
        }

        var pullFailure = pullNeeded
            ? await RunStepAsync(DockerComposeCommandBuilder.Pull(options.ComposeFilePath), composeDirectory, token).ConfigureAwait(false)
            : null;
        if (pullFailure is not null)
        {
            var error = options.Trigger == DeployRecordValues.TriggerRollback
                ? $"image pull failed ({pullFailure}) — the rollback target is no longer local and pulling from the "
                  + "registry requires docker login (e.g. a token with read:packages)"
                : $"image pull failed: {pullFailure}";

            // Nothing was applied; restore the previously pinned tag so the
            // .env keeps describing what is actually running. RestoreEnvValue
            // removes the key when there was no prior tag (a first deploy), so a
            // failed pull never leaves a tag pinned to an image that never ran —
            // mirroring the image-restore path above.
            if (options.Tag is not null && previousTag != options.Tag)
            {
                RestoreEnvValue(envFile, TagVariable, previousTag);
            }

            if (options.ExpectedImage is not null && previousImage != options.ExpectedImage)
            {
                RestoreEnvValue(envFile, ImageVariable, previousImage);
            }

            await FinishAsync(options, startedAt, DeployRecordValues.ResultFailed, previousTag, health.State, error, cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        var replicas = DeploySettings.ClampReplicas(
            new DeploySettingsStore(options.ComposeFilePath).Load().Replicas);
        if (replicas > 1)
        {
            var cannotScale = WhyItCannotScale(options.ComposeFilePath);
            if (cannotScale is not null)
            {
                await FinishAsync(options, startedAt, DeployRecordValues.ResultFailed, previousTag, health.State, cannotScale, cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }

            _log?.Invoke($"running {replicas} copies of {DockerComposeCommandBuilder.AppService}");
        }

        var upFailure = await RunStepAsync(
                DockerComposeCommandBuilder.Up(options.ComposeFilePath, replicas), composeDirectory, token)
            .ConfigureAwait(false);
        if (upFailure is not null)
        {
            await FinishAsync(options, startedAt, DeployRecordValues.ResultFailed, previousTag, health.State, $"compose up failed: {upFailure}", cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        if (options.HealthCheckTimeout > TimeSpan.Zero)
        {
            // Not passed until it passes: if the deploy timeout kills the check
            // mid-flight, the record must not claim the health state was "skipped".
            health.State = DeployRecordValues.HealthFailed;
            var failure = await _healthChecker
                .WaitForHealthyAsync(options.ComposeFilePath, options.HealthCheckTimeout, token)
                .ConfigureAwait(false);
            if (failure is not null)
            {
                _log?.Invoke($"health check failed: {failure}");
                health.State = DeployRecordValues.HealthFailed;
                await FinishAsync(options, startedAt, DeployRecordValues.ResultFailed, previousTag, DeployRecordValues.HealthFailed, failure, cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }

            health.State = DeployRecordValues.HealthPassed;
        }

        // Second, and only after docker says the containers settled: asking an
        // application for a page is pointless while its container is still starting,
        // and the compose check is what knows when that is over.
        var notReady = await ProbeReadinessAsync(options, startedAt, token).ConfigureAwait(false);
        if (notReady is not null)
        {
            _log?.Invoke(notReady);
            health.State = DeployRecordValues.HealthFailed;
            await FinishAsync(options, startedAt, DeployRecordValues.ResultFailed, previousTag, DeployRecordValues.HealthFailed, notReady, cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        await WarnOnUnservedPortAsync(options.ComposeFilePath, token).ConfigureAwait(false);

        if (options.PruneImages)
        {
            // Cleanup is a nicety; its failure must not fail the deploy.
            await _retentionPruner.PruneAsync(options.ComposeFilePath, options.KeepImages, token).ConfigureAwait(false);
        }

        var result = options.Trigger == DeployRecordValues.TriggerRollback
            ? DeployRecordValues.ResultRolledBack
            : DeployRecordValues.ResultSucceeded;
        await FinishAsync(options, startedAt, result, previousTag, health.State, error: null, cancellationToken)
            .ConfigureAwait(false);
        _log?.Invoke(options.Trigger == DeployRecordValues.TriggerRollback ? "rollback succeeded" : "deploy succeeded");
        return true;
    }

    /// <summary>
    /// Carries the health-check outcome out of the deploy sequence, so the
    /// cancellation handlers in <see cref="DeployAsync"/> can record the state the
    /// sequence had reached rather than assuming it never got that far.
    /// </summary>
    private sealed class HealthProgress
    {
        public string State { get; set; } = DeployRecordValues.HealthSkipped;
    }

    private async Task FinishAsync(
        DeployOptions options,
        DateTimeOffset startedAt,
        string result,
        string? previousTag,
        string healthState,
        string? error,
        CancellationToken cancellationToken)
    {
        try
        {
            _history?.Append(new DeployRecord
            {
                Id = DeployHistoryStore.NewRecordId(),
                Tag = options.Tag ?? "latest",
                StartedAt = startedAt,
                DurationSeconds = Math.Round((DateTimeOffset.UtcNow - startedAt).TotalSeconds, 1),
                Result = result,
                Trigger = options.Trigger,
                PreviousTag = previousTag,
                HealthCheck = healthState,
                Error = error,
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"could not write deploy history: {exception.Message}");
        }

        if (_observer is not null)
        {
            try
            {
                await _observer.OnDeployCompletedAsync(
                    new DeployOutcome
                    {
                        Result = result,
                        Trigger = options.Trigger,
                        Tag = options.Tag ?? "latest",
                        PreviousTag = previousTag,
                        HealthCheck = healthState,
                        Error = error,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _log?.Invoke($"deploy observer failed: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// Verifies the compose project actually references <paramref name="expectedImage"/>
    /// — it will unless the image line was hand-edited to hardcode a name instead of
    /// using the pinned variable. Returns an actionable error message when it does not,
    /// or null when it matches or the reference set could not be read (the pull would
    /// surface that anyway).
    /// </summary>
    private async Task<string?> FindImageMismatchAsync(string expectedImage, string composeFilePath, CancellationToken cancellationToken)
    {
        var imagesResult = await _processRunner
            .RunAsync(DockerExecutable, DockerComposeCommandBuilder.ConfigImages(composeFilePath), PinqOpsStatePaths.ComposeWorkingDirectory(composeFilePath), cancellationToken)
            .ConfigureAwait(false);
        if (!imagesResult.Succeeded)
        {
            // Can't read the reference set (e.g. a compose file pinqops did not
            // generate); don't invent a failure — let pull report any real error.
            _log?.Invoke($"could not read compose images to verify the target ({imagesResult.StandardError.TrimEnd()}); skipping the check");
            return null;
        }

        var repositories = imagesResult.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ImageReference.RepositoryOf)
            .ToArray();

        if (repositories.Any(repository => string.Equals(repository, expectedImage, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var referenced = repositories.Length > 0 ? string.Join(", ", repositories) : "(none)";
        _log?.Invoke(
            $"image mismatch: this deploy is for {expectedImage} but {composeFilePath} references {referenced}");
        return $"compose file targets the wrong image. Expected {expectedImage}, but {composeFilePath} "
            + $"references {referenced} — its image: line hardcodes a name instead of using the pinned variable. "
            + $"Set it to ${{{ImageVariable}:-{expectedImage}}}:${{{TagVariable}:-latest}} so the image follows the "
            + $"repository, then redeploy.";
    }

    private static void RestoreEnvValue(string envFile, string key, string? previousValue)
    {
        if (previousValue is null)
        {
            EnvFileStore.RemoveValue(envFile, key);
        }
        else
        {
            EnvFileStore.SetValue(envFile, key, previousValue);
        }
    }

    /// <summary>
    /// Returns an error when the compose project belongs to a different
    /// application than the one being deployed, or null when it matches (or the
    /// file declares no project name, e.g. a hand-written one).
    /// </summary>
    /// <remarks>
    /// pinqops manages one application per compose file. Pointing a second
    /// repository at the same path is silently destructive: its deploy pins its
    /// own image and tag over the first application's, so the wrong image runs
    /// under the wrong project — or the pull dies on a tag that only exists in
    /// the other package. The project name is the repository's, so comparing it
    /// against the image being deployed catches this before anything is written.
    /// </remarks>
    private static string? FindProjectOwnerMismatch(string expectedImage, string composeFilePath)
    {
        // ghcr.io/<owner>/<repo> — the last segment is the repository, which is
        // what the project name is derived from.
        var repository = expectedImage[(expectedImage.LastIndexOf('/') + 1)..];
        return repository.Length == 0 ? null : ProjectOwnerMismatch(repository, composeFilePath);
    }

    /// <summary>
    /// The same check, given the repository name directly. Shared with the preview
    /// path, which knows its repository without having to read it out of an image —
    /// and which had no such check at all, so it would read any compose file it was
    /// pointed at and copy that application's secrets into a container built from
    /// somebody else's pull request.
    /// </summary>
    internal static string? ProjectOwnerMismatch(string repositoryName, string composeFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryName);
        ArgumentException.ThrowIfNullOrWhiteSpace(composeFilePath);

        if (!File.Exists(composeFilePath))
        {
            return null;
        }

        string? declaredProject;
        try
        {
            declaredProject = ComposeProjectName.ReadFrom(File.ReadAllText(composeFilePath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (declaredProject is null)
        {
            return null;
        }

        var expectedProject = ComposeProjectName.FromRepository(repositoryName);
        if (string.Equals(declaredProject, expectedProject, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"{composeFilePath} is the compose project of '{declaredProject}', but this deploy is for "
            + $"'{expectedProject}'. pinqops manages one application per compose file — give this one its own "
            + $"path (e.g. /opt/{expectedProject}/docker-compose.yml) and set the repository variable "
            + $"APP_COMPOSE_PATH to it, or the two will overwrite each other.";
    }

    /// <summary>
    /// Warns when the project publishes a container port the image does not
    /// expose. The container still runs and the health check still passes, so
    /// without this the deploy is reported green while nothing answers on the
    /// published port — the classic "it deployed but the site is dead".
    /// </summary>
    /// <summary>
    /// Why this project cannot run more than one copy, or null when it can.
    ///
    /// <para>Checked before <c>up</c> rather than left to docker, because docker's
    /// answer is "Bind for 0.0.0.0:8080 failed: port is already allocated" — which
    /// reads as somebody else taking the port, not as the app taking it from itself.
    /// The operator would go looking for the wrong thing.</para>
    /// </summary>
    private static string? WhyItCannotScale(string composeFilePath)
    {
        string yaml;
        try
        {
            yaml = File.ReadAllText(composeFilePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Unreadable here means unreadable to compose in a moment too; let the
            // deploy fail where the real error is.
            return null;
        }

        return ComposePortPublication.ProxyPublishesThePort(yaml)
            ? null
            : "this project publishes its own host port, and two containers cannot bind the same one. "
                + "Hand the port to the proxy on the Domains page, then deploy again.";
    }

    /// <summary>
    /// The optional HTTP gate, after the compose health check. Returns null when it
    /// is off or the application answered; otherwise why the deploy should fail.
    /// </summary>
    /// <remarks>
    /// Every failure path here fails the deploy, including the ones that are this
    /// code's own fault. A probe that cannot be run has not passed — a gate that
    /// quietly stops gating is worse than no gate, because it reports the same green
    /// either way.
    /// </remarks>
    private async Task<string?> ProbeReadinessAsync(
        DeployOptions options, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        try
        {
            var settings = new DeploySettingsStore(options.ComposeFilePath).Load().Readiness;
            if (!settings.Enabled)
            {
                return null;
            }

            var containers = await AppContainerNamesAsync(options.ComposeFilePath, cancellationToken).ConfigureAwait(false);
            if (containers.Count == 0)
            {
                return "readiness probe: compose reports no application container to ask.";
            }

            var containerPort = ParsePort(
                EnvFileStore.GetValue(PinqOpsStatePaths.EnvFile(options.ComposeFilePath), ContainerPortVariable))
                ?? DockerfileInspector.DefaultPort;

            // Every copy, not just the first: a scaled app is served round-robin
            // across all of them, so one ready copy proves nothing about the rest.
            // The budget is recomputed per copy — it is what is left of the
            // deploy's own timeout either way.
            foreach (var container in containers)
            {
                var notReady = await _readinessProbe
                    .WaitForReadyAsync(
                        WithinTheDeployBudget(settings, options, startedAt), container, containerPort, cancellationToken)
                    .ConfigureAwait(false);
                if (notReady is not null)
                {
                    return containers.Count > 1 ? $"{notReady} ({container})" : notReady;
                }
            }

            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return $"readiness probe could not be run: {exception.Message}";
        }
    }

    /// <summary>
    /// The probe's timeout, capped at what is left of the deploy's.
    ///
    /// <para>A probe budget larger than the deploy's can never be spent: the deploy
    /// is killed mid-probe and reports "docker was killed mid-flight, so the compose
    /// project may be partly updated" — about a deploy where nothing is wrong except
    /// that an application took longer than the operator allowed. Capping it turns
    /// that into the message the operator can act on.</para>
    /// </summary>
    private ReadinessSettings WithinTheDeployBudget(
        ReadinessSettings settings, DeployOptions options, DateTimeOffset startedAt)
    {
        // Enough left over for the history write and the notification that follow.
        var margin = TimeSpan.FromSeconds(5);
        var remaining = startedAt + options.Timeout - DateTimeOffset.UtcNow - margin;
        var available = (int)Math.Floor(remaining.TotalSeconds);
        if (available >= settings.TimeoutSeconds)
        {
            return settings;
        }

        _log?.Invoke(
            $"readiness probe: {settings.TimeoutSeconds}s does not fit in what is left of the deploy timeout; "
            + $"allowing {Math.Max(available, 1)}s");

        // Normalized() floors this at 1s, so a deploy already past its budget still
        // makes one honest attempt rather than reporting a probe it never ran.
        return new ReadinessSettings
        {
            Enabled = settings.Enabled,
            Path = settings.Path,
            ExpectedStatusFrom = settings.ExpectedStatusFrom,
            ExpectedStatusTo = settings.ExpectedStatusTo,
            IntervalSeconds = settings.IntervalSeconds,
            TimeoutSeconds = Math.Max(available, 1),
            RequestTimeoutSeconds = settings.RequestTimeoutSeconds,
            ConsecutiveSuccesses = settings.ConsecutiveSuccesses,
        };
    }

    /// <summary>
    /// The containers of the service the template calls <c>app</c> — all of them,
    /// because a scaled service runs several — from <c>compose ps</c>. Empty when
    /// compose reports none.
    /// </summary>
    private async Task<IReadOnlyList<string>> AppContainerNamesAsync(
        string composeFilePath, CancellationToken cancellationToken)
    {
        var workingDirectory = PinqOpsStatePaths.ComposeWorkingDirectory(composeFilePath);
        var psOutput = await RunCapturedAsync(DockerComposeCommandBuilder.Ps(composeFilePath), workingDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (psOutput is null)
        {
            return [];
        }

        var appContainers = new List<string>();
        string? firstName = null;
        foreach (var service in JsonLines.Parse(psOutput))
        {
            if (!service.TryGetProperty("Name", out var name)
                || name.ValueKind != JsonValueKind.String
                || name.GetString() is not { Length: > 0 } containerName)
            {
                continue;
            }

            if (IsAppService(service))
            {
                appContainers.Add(containerName);
            }

            // A hand-edited project may not have a service called `app`; the first
            // one it does have is the only reasonable stand-in, and it is the same
            // choice ComposeAppStatus makes for the wizard's card.
            firstName ??= containerName;
        }

        if (appContainers.Count > 0)
        {
            return appContainers;
        }

        return firstName is null ? [] : [firstName];
    }

    /// <remarks>
    /// Advisory only. <c>EXPOSE</c> is documentation: an app may legitimately
    /// listen on a port its image never declared, so this must never fail a
    /// deploy — it only says what looks wrong.
    /// </remarks>
    private async Task WarnOnUnservedPortAsync(string composeFilePath, CancellationToken cancellationToken)
    {
        try
        {
            // What the project asks the app to listen on. An app whose host port
            // the proxy publishes has no `Publishers` entry at all, so reading the
            // answer off the published ports made this diagnostic fall silent
            // exactly where it is hardest to recover from: a proxy route aimed at
            // a port nothing listens on reads as a proxy fault, not an app fault.
            var declaredContainerPort = ParsePort(
                EnvFileStore.GetValue(PinqOpsStatePaths.EnvFile(composeFilePath), ContainerPortVariable));

            var workingDirectory = PinqOpsStatePaths.ComposeWorkingDirectory(composeFilePath);
            var images = await RunCapturedAsync(DockerComposeCommandBuilder.ConfigImages(composeFilePath), workingDirectory, cancellationToken)
                .ConfigureAwait(false);
            var image = images?
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (image is null)
            {
                return;
            }

            var exposedJson = await RunCapturedAsync(DockerComposeCommandBuilder.InspectImageExposedPorts(image), workingDirectory, cancellationToken)
                .ConfigureAwait(false);
            var exposed = ParseExposedPorts(exposedJson);
            if (exposed.Count == 0)
            {
                // Nothing declared — no basis for an opinion.
                return;
            }

            var psOutput = await RunCapturedAsync(DockerComposeCommandBuilder.Ps(composeFilePath), workingDirectory, cancellationToken)
                .ConfigureAwait(false);
            if (psOutput is null)
            {
                return;
            }

            var exposedByImage = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal) { [image] = exposed };

            foreach (var service in JsonLines.Parse(psOutput))
            {
                var containerPorts = PublishedContainerPorts(service);
                if (containerPorts.Count == 0)
                {
                    // Nothing published. Either the proxy holds this app's port,
                    // or this service was never meant to be reachable. Only the
                    // first has an answer worth judging, and only the app service
                    // is what PINQOPS_CONTAINER_PORT describes.
                    if (declaredContainerPort is not { } enrolledPort || !IsAppService(service))
                    {
                        continue;
                    }

                    containerPorts = [enrolledPort];
                }

                // Judge each service against its own image's EXPOSE list — a
                // second service (a database, say) publishes ports the app image
                // never declared, and comparing those against the wrong image
                // produced a false warning on every deploy. An entry without an
                // Image field falls back to the first configured image.
                var serviceExposed = exposed;
                if (service.TryGetProperty("Image", out var imageElement)
                    && imageElement.GetString() is { Length: > 0 } serviceImage
                    && !exposedByImage.TryGetValue(serviceImage, out serviceExposed))
                {
                    var serviceExposedJson = await RunCapturedAsync(DockerComposeCommandBuilder.InspectImageExposedPorts(serviceImage), workingDirectory, cancellationToken)
                        .ConfigureAwait(false);
                    serviceExposed = ParseExposedPorts(serviceExposedJson);
                    exposedByImage[serviceImage] = serviceExposed;
                }

                if (serviceExposed.Count == 0)
                {
                    continue;
                }

                foreach (var target in containerPorts)
                {
                    if (serviceExposed.Contains(target))
                    {
                        continue;
                    }

                    _log?.Invoke(
                        $"warning: traffic is aimed at container port {target}, but the image only exposes "
                        + $"{string.Join(", ", serviceExposed)}. If nothing is listening on {target} the app will be "
                        + $"unreachable — set {ContainerPortVariable} in the project's .env to the port your app "
                        + $"listens on, then re-apply.");
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A diagnostic must never break a deploy that already succeeded.
            _log?.Invoke($"could not check the published port: {exception.Message}");
        }
    }

    /// <summary>
    /// The container ports a <c>compose ps</c> entry publishes to the host. Empty
    /// when the service publishes nothing.
    /// </summary>
    private static List<int> PublishedContainerPorts(JsonElement service)
    {
        var ports = new List<int>();
        if (!service.TryGetProperty("Publishers", out var publishers)
            || publishers.ValueKind != JsonValueKind.Array)
        {
            return ports;
        }

        foreach (var publisher in publishers.EnumerateArray())
        {
            if (publisher.TryGetProperty("TargetPort", out var targetPort)
                && targetPort.TryGetInt32(out var target)
                && target != 0)
            {
                ports.Add(target);
            }
        }

        return ports;
    }

    /// <summary>
    /// Whether this <c>compose ps</c> entry is the service the template names
    /// <c>app</c> — the only one <see cref="ContainerPortVariable"/> describes.
    /// </summary>
    private static bool IsAppService(JsonElement service) =>
        service.TryGetProperty("Service", out var name)
        && name.ValueKind == JsonValueKind.String
        && string.Equals(name.GetString(), "app", StringComparison.OrdinalIgnoreCase);

    /// <summary>A port number from an <c>.env</c> value, or null if it is absent or not one.</summary>
    private static int? ParsePort(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
        && HostPort.IsValid(port)
            ? port
            : null;

    /// <summary>Port numbers from a docker <c>ExposedPorts</c> map (<c>{"8083/tcp":{}}</c>).</summary>
    private static HashSet<int> ParseExposedPorts(string? exposedPortsJson)
    {
        var ports = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(exposedPortsJson))
        {
            return ports;
        }

        try
        {
            using var document = JsonDocument.Parse(exposedPortsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ports;
            }

            foreach (var entry in document.RootElement.EnumerateObject())
            {
                var slash = entry.Name.IndexOf('/');
                var portText = slash >= 0 ? entry.Name[..slash] : entry.Name;
                if (int.TryParse(portText, out var port))
                {
                    ports.Add(port);
                }
            }
        }
        catch (JsonException)
        {
            // "null" or an unexpected shape — treat as "nothing declared".
        }

        return ports;
    }

    /// <summary>Standard output of a docker command, or null when it failed.</summary>
    private async Task<string?> RunCapturedAsync(IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken cancellationToken)
    {
        var result = await _processRunner
            .RunAsync(DockerExecutable, arguments, workingDirectory, cancellationToken)
            .ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput : null;
    }

    /// <summary>True when every image the compose project references exists locally.</summary>
    private async Task<bool> ImagesPresentLocallyAsync(string composeFilePath, CancellationToken cancellationToken)
    {
        var workingDirectory = PinqOpsStatePaths.ComposeWorkingDirectory(composeFilePath);
        var imagesResult = await _processRunner
            .RunAsync(DockerExecutable, DockerComposeCommandBuilder.ConfigImages(composeFilePath), workingDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (!imagesResult.Succeeded)
        {
            return false;
        }

        var references = imagesResult.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (references.Length == 0)
        {
            return false;
        }

        foreach (var reference in references)
        {
            var inspect = await _processRunner
                .RunAsync(DockerExecutable, DockerComposeCommandBuilder.InspectImage(reference), workingDirectory, cancellationToken)
                .ConfigureAwait(false);
            if (!inspect.Succeeded)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Runs a docker step. Returns null on success, otherwise docker's own
    /// reason — which the caller puts in the deploy record and the notification,
    /// so "port is already allocated" reaches Slack instead of a bare
    /// "compose up failed".
    /// </summary>
    private async Task<string?> RunStepAsync(IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken cancellationToken)
    {
        _log?.Invoke($"$ {DockerExecutable} {string.Join(' ', arguments)}");

        var result = await _processRunner
            .RunAsync(DockerExecutable, arguments, workingDirectory, cancellationToken)
            .ConfigureAwait(false);

        if (result.StandardOutput.Length > 0)
        {
            _log?.Invoke(result.StandardOutput.TrimEnd());
        }

        if (result.Succeeded)
        {
            return null;
        }

        _log?.Invoke($"command failed (exit {result.ExitCode}): {result.StandardError.TrimEnd()}");
        return Condense(result.StandardError) ?? $"exit {result.ExitCode}";
    }

    /// <summary>
    /// The most specific line of a docker error, capped so it stays readable in
    /// a chat notification. Docker prints the actual cause last.
    /// </summary>
    private static string? Condense(string standardError)
    {
        const int MaxLength = 300;

        var lastLine = standardError
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        if (lastLine is null)
        {
            return null;
        }

        return lastLine.Length <= MaxLength ? lastLine : lastLine[..MaxLength] + "…";
    }
}
