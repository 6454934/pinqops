using System.Collections.Concurrent;
using System.Text;

namespace PinqOps.Web;

/// <summary>
/// Dashboard-side deploy state and rollback jobs. Reads the same
/// <c>.pinqops</c> state the CLI writes (history, pinned tag) and runs
/// rollbacks as background jobs — one at a time <em>per compose project</em>,
/// mirroring the app-install job pattern so the UI can poll for progress.
///
/// The gate and the job registry are keyed by the compose file, not held as a
/// single pair of fields. One server hosts as many apps as you like, and a
/// process-wide gate made every one of them contend with the others: rolling
/// back app A refused app B's rollback with "a rollback is already in progress",
/// blocked B's "apply .env" with a message naming <em>this</em> project, and —
/// because there was only ever one job — answered A's own progress poll with
/// "unknown rollback job" the moment B started one, so a rollback that had in
/// fact succeeded reported a failure.
/// </summary>
public sealed class DeployService
{
    private readonly IProcessRunner _processRunner;

    // Only one deploy/rollback may touch a given compose project at a time.
    // Keyed by the project's full path so two apps never queue behind each other;
    // the map is bounded by the number of connected apps.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    // The current (or most recent) rollback job per compose project.
    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);

    private readonly object _jobLock = new();

    private readonly ProxyService _proxy;

    public DeployService(IProcessRunner processRunner, ProxyService proxy)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(proxy);
        _processRunner = processRunner;
        _proxy = proxy;
    }

    public sealed class Job
    {
        private readonly StringBuilder _log = new();
        private readonly object _logLock = new();

        public required string Id { get; init; }
        public required string Tag { get; init; }

        // "running" → "done" | "error"
        public volatile string Phase = "running";
        public volatile string? Error;

        public bool Done => Phase is "done" or "error";

        public void Add(string line)
        {
            lock (_logLock)
            {
                _log.AppendLine(line);
            }
        }

        public string Log()
        {
            lock (_logLock)
            {
                return _log.ToString();
            }
        }
    }

    /// <summary>
    /// The key a compose project's gate and job are stored under. Full path, so
    /// the same project addressed relatively and absolutely is one project;
    /// ordinal, because paths are case-sensitive on the servers this runs on.
    /// </summary>
    private static string ProjectKey(string composeFilePath) => Path.GetFullPath(composeFilePath);

    private SemaphoreSlim GateFor(string composeFilePath) =>
        _gates.GetOrAdd(ProjectKey(composeFilePath), _ => new SemaphoreSlim(1, 1));

    public object GetState(string composeFilePath)
    {
        var currentTag = EnvFileStore.GetValue(PinqOpsStatePaths.EnvFile(composeFilePath), Deployer.TagVariable);
        var lastSuccessful = new DeployHistoryStore(composeFilePath).LastSuccessful();
        return new
        {
            currentTag,
            currentDeployedAt = lastSuccessful?.StartedAt,
            composeUsesTagVariable = ComposeUsesTagVariable(composeFilePath),
            rollbackInProgress = CurrentJob(composeFilePath) is { Done: false },
        };
    }

    public IReadOnlyList<DeployRecord> History(string composeFilePath) =>
        new DeployHistoryStore(composeFilePath).Load();

    /// <summary>
    /// A job by id, whichever project it belongs to — the poller only ever holds
    /// the id it was handed, and it has to keep resolving while another app's
    /// rollback runs alongside.
    /// </summary>
    public Job? Find(string jobId) =>
        _jobs.Values.FirstOrDefault(job => string.Equals(job.Id, jobId, StringComparison.Ordinal));

    /// <summary>
    /// Starts a rollback to <paramref name="tag"/> in the background. The tag
    /// must appear in deploy history (any past deploy attempt of it counts), so
    /// arbitrary strings can never reach docker. Returns null when a rollback is
    /// already running <em>for this compose project</em> — another app rolling
    /// back at the same time is not a conflict and must not be treated as one.
    /// </summary>
    public Job? TryStartRollback(string composeFilePath, string tag)
    {
        if (!ComposeUsesTagVariable(composeFilePath))
        {
            throw new InvalidOperationException(
                $"{composeFilePath} does not reference ${{{Deployer.TagVariable}}}. "
                + $"Change the image line to e.g. 'image: ghcr.io/<owner>/<repo>:${{{Deployer.TagVariable}:-latest}}' first.");
        }

        var history = new DeployHistoryStore(composeFilePath);
        if (!history.Load().Any(record => record.Tag == tag))
        {
            throw new ArgumentException($"Tag '{tag}' is not in the deploy history.");
        }

        lock (_jobLock)
        {
            if (CurrentJob(composeFilePath) is { Done: false })
            {
                return null;
            }

            var job = new Job
            {
                Id = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)),
                Tag = tag,
            };
            _jobs[ProjectKey(composeFilePath)] = job;

            _ = Task.Run(() => RunRollbackAsync(job, composeFilePath, tag));
            return job;
        }
    }

    /// <summary>
    /// Recreates the compose project's containers so edited <c>.env</c> values take
    /// effect — under the same gate as a rollback.
    ///
    /// This is the identical <c>docker compose up -d</c> against the identical
    /// project, so running it outside the gate broke the "one deploy/rollback at a
    /// time" invariant the gate exists for: a rollback that had pinned its tag and
    /// was mid-pull could have a second `up -d` start underneath it, recreating the
    /// containers on a half-pulled image while history recorded the rollback as
    /// successful.
    /// </summary>
    /// <returns>Null when a deploy or rollback currently holds the gate.</returns>
    public async Task<object?> ApplyComposeAsync(string composeFilePath, CancellationToken cancellationToken = default)
    {
        var gate = GateFor(composeFilePath);
        if (!await gate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        // The in-process gate above cannot see the runner CLI. The cross-process
        // one can; a busy answer becomes the same null the in-process refusal
        // produces, so every caller's "a deploy is in progress" handling covers
        // both. The wait is short on purpose — this is an interactive apply, not
        // a queued deploy.
        PinqOps.Deploy.DeployGate fileGate;
        try
        {
            fileGate = await PinqOps.Deploy.DeployGate
                .AcquireAsync(composeFilePath, TimeSpan.FromSeconds(5), log: null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            gate.Release();
            return null;
        }
        catch
        {
            gate.Release();
            throw;
        }

        try
        {
            using var _ = fileGate;
            var settings = new PinqOps.Deploy.DeploySettingsStore(composeFilePath).Load();

            // The same replica count the deploy uses. Without it, applying an
            // environment change would quietly take a three-copy app back to one —
            // a scale-down nobody asked for, from a form about environment
            // variables.
            var replicas = PinqOps.Deploy.DeploySettings.ClampReplicas(settings.Replicas);

            var result = await _processRunner.RunAsync(
                "docker",
                UpFor(composeFilePath, settings, replicas),
                PinqOpsStatePaths.ComposeWorkingDirectory(composeFilePath),
                cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException($"compose up failed: {result.StandardError.Trim()}");
            }

            return new { ok = true, output = result.StandardOutput.Trim() };
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The <c>up</c> that recreates this project's containers, aimed at whichever
    /// project is actually serving.
    ///
    /// <para>Colour-awareness is not a nicety here. A project deployed as two
    /// colours runs as <c>&lt;name&gt;-blue</c> or <c>&lt;name&gt;-green</c>; an
    /// <c>up</c> with no <c>-p</c> would take the name from the compose file and
    /// start a <em>third</em> project — a second copy of the app that nothing routes
    /// to, holding the same external volumes, from a form about environment
    /// variables.</para>
    /// </summary>
    private static IReadOnlyList<string> UpFor(
        string composeFilePath, PinqOps.Deploy.DeploySettings settings, int replicas)
    {
        if (!settings.BlueGreen)
        {
            return DockerComposeCommandBuilder.Up(composeFilePath, replicas);
        }

        if (!PinqOps.Deploy.BlueGreenPlan.TryCreate(composeFilePath, settings, out var plan, out var problem))
        {
            throw new InvalidOperationException(
                problem ?? "This project is set up for deploys without a gap but pinqops cannot work out how.");
        }

        // The colour's project is started with its own environment file, so the
        // edited .env has to be copied into it or the change reaches nothing.
        var color = PinqOps.Deploy.DeployColors.Normalize(settings.ActiveColor);
        var colorEnv = PinqOps.Deploy.ColorEnvironment.Write(composeFilePath, color, plan!.Alias);

        return DockerComposeCommandBuilder.UpColor(
            composeFilePath,
            PinqOps.Deploy.DeployColors.ProjectName(plan.Project, color),
            colorEnv,
            replicas);
    }

    private async Task RunRollbackAsync(Job job, string composeFilePath, string tag)
    {
        var gate = GateFor(composeFilePath);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var notifications = new PinqOps.Notifications.NotificationDispatcher(composeFilePath, job.Add);

            // What the .env pins right now is what this rollback is escaping from —
            // read before anything below rewrites it, so the rolled_back record can
            // name it and keep the rollback chain followable.
            var previousTag = EnvFileStore.GetValue(
                PinqOpsStatePaths.EnvFile(composeFilePath), Deployer.TagVariable);
            var switchStartedAt = DateTimeOffset.UtcNow;

            // The version being rolled back to may still be running with no traffic,
            // in which case this is a proxy reload rather than a pull and a restart:
            // under a second, against containers that have already proved they run
            // on this server.
            if (await TrySwitchBackAsync(composeFilePath, tag, job.Add).ConfigureAwait(false))
            {
                // Recorded like every other rollback: without the rolled_back record
                // naming what was escaped, the next default rollback walked straight
                // back onto it — and the switch never showed up in the dashboard
                // history or the notification channels.
                try
                {
                    new DeployHistoryStore(composeFilePath).Append(
                        PinqOps.Deploy.BlueGreenRecord.ForSwitchBack(
                            tag, previousTag, switchStartedAt, DateTimeOffset.UtcNow));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    job.Add($"warning: could not record the rollback: {exception.Message}");
                }

                await notifications
                    .OnDeployCompletedAsync(
                        PinqOps.Deploy.BlueGreenRecord.OutcomeForSwitchBack(tag, previousTag),
                        CancellationToken.None)
                    .ConfigureAwait(false);

                job.Phase = "done";
                return;
            }

            // No shortcut, so this is a full redeploy of the earlier tag — and for a
            // coloured project that redeploy has to be coloured too. The ordinary
            // deployer below runs `compose up` with no -p, which against a coloured
            // project starts a third copy that nothing routes to; it now refuses
            // rather than doing that, so this branch has to exist for the rollback
            // to work at all.
            if (await RedeployColorAsync(composeFilePath, tag, previousTag, job).ConfigureAwait(false) is { } colored)
            {
                await notifications
                    .OnDeployCompletedAsync(
                        PinqOps.Deploy.BlueGreenRecord.OutcomeFor(
                            colored, DeployRecordValues.TriggerRollback, tag, previousTag),
                        CancellationToken.None)
                    .ConfigureAwait(false);

                job.Phase = colored.Succeeded ? "done" : "error";
                if (!colored.Succeeded)
                {
                    job.Error = colored.Error ?? "Rollback failed — see the log.";
                }

                return;
            }

            var deployer = new Deployer(
                _processRunner,
                job.Add,
                history: new DeployHistoryStore(composeFilePath),
                observer: notifications);
            var options = DeployOptions.Create(
                composeFilePath,
                tag: tag,
                trigger: DeployRecordValues.TriggerRollback);

            var succeeded = await deployer.DeployAsync(options);
            if (succeeded)
            {
                job.Phase = "done";
            }
            else
            {
                job.Error = "Rollback failed — see the log.";
                job.Phase = "error";
            }
        }
        catch (Exception exception)
        {
            job.Error = exception.Message;
            job.Phase = "error";
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Rolls back by pointing the proxy at the colour still running the wanted
    /// version. False means there is no such colour and the ordinary redeploy is
    /// what this rollback needs — which is not a failure and is not reported as one.
    /// </summary>
    private async Task<bool> TrySwitchBackAsync(string composeFilePath, string tag, Action<string> log)
    {
        var settings = new PinqOps.Deploy.DeploySettingsStore(composeFilePath).Load();
        if (!PinqOps.Deploy.BlueGreenPlan.TryCreate(composeFilePath, settings, out var plan, out var problem))
        {
            if (problem is not null)
            {
                log(problem);
            }

            return false;
        }

        return await new PinqOps.Deploy.BlueGreenDeployer(_processRunner, _proxy.Gateway, log)
            .TrySwitchBackAsync(plan!, tag)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Redeploys a coloured project at <paramref name="tag"/>, writing the history
    /// entry the coloured path does not write for itself. Null means the project is
    /// not coloured and the ordinary deployer is what this needs.
    /// </summary>
    private async Task<PinqOps.Deploy.BlueGreenResult?> RedeployColorAsync(
        string composeFilePath, string tag, string? previousTag, Job job)
    {
        var settings = new PinqOps.Deploy.DeploySettingsStore(composeFilePath).Load();
        if (!settings.BlueGreen)
        {
            return null;
        }

        if (!PinqOps.Deploy.BlueGreenPlan.TryCreate(composeFilePath, settings, out var plan, out var problem))
        {
            // Coloured but not deployable that way: say so rather than falling
            // through to a path that would start a third copy.
            var reason = problem ?? "This project is set up for deploys without a gap but pinqops cannot work out how.";
            job.Add(reason);
            return new PinqOps.Deploy.BlueGreenResult(
                Succeeded: false, Color: string.Empty, Switched: false, Error: reason);
        }

        var startedAt = DateTimeOffset.UtcNow;
        var result = await new PinqOps.Deploy.BlueGreenDeployer(_processRunner, _proxy.Gateway, job.Add)
            .DeployAsync(plan! with { Tag = tag })
            .ConfigureAwait(false);

        try
        {
            new DeployHistoryStore(composeFilePath).Append(
                PinqOps.Deploy.BlueGreenRecord.For(
                    result, DeployRecordValues.TriggerRollback, tag, startedAt, DateTimeOffset.UtcNow, previousTag));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            job.Add($"warning: could not record the rollback: {exception.Message}");
        }

        return result;
    }

    private Job? CurrentJob(string composeFilePath) =>
        _jobs.GetValueOrDefault(ProjectKey(composeFilePath));

    internal static bool ComposeUsesTagVariable(string composeFilePath) =>
        File.Exists(composeFilePath)
        && File.ReadAllText(composeFilePath).Contains($"${{{Deployer.TagVariable}", StringComparison.Ordinal);
}
