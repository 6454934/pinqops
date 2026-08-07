using PinqOps.Alerts;
using PinqOps.Deploy;
using PinqOps.Scheduling;

namespace PinqOps.Web;

/// <summary>
/// Follows each app's load and changes how many copies of it run.
///
/// <para><b>It reads the samples the alert worker already takes.</b> Those are one
/// <c>docker stats</c> a minute for the whole host — sampling again here would double
/// the cost of the most expensive thing the dashboard does, to learn the same
/// numbers a few seconds apart.</para>
///
/// <para><b>Every change goes through the deploy gate.</b> Scaling is a
/// <c>compose up</c> against a project, so doing it outside the gate would let it
/// start while a deploy is mid-pull and recreate the containers on a half-pulled
/// image. Refused rather than queued: the load will still be there next minute.</para>
///
/// <para><b>Every change is recorded.</b> A count that moved on its own, with no
/// trace of why, is the kind of thing an operator finds at three in the morning and
/// cannot explain — so the audit trail carries the reading that caused it.</para>
/// </summary>
public sealed class AutoscaleSource : ScheduledWorkSource
{
    private const string JobPrefix = "autoscale:";

    private readonly UiConfigStore _config;
    private readonly AlertScheduler _metrics;
    private readonly DeployService _deploys;
    private readonly AuditLog _audit;
    private readonly ILogger<AutoscaleSource> _logger;

    /// <summary>
    /// The window and cooldown state per compose project, held in memory. A restart
    /// forgets an in-progress window, which is the safe direction: the breach has to
    /// hold again before anything happens.
    /// </summary>
    private readonly Dictionary<string, AutoscaleState> _state = new(StringComparer.Ordinal);

    public AutoscaleSource(
        UiConfigStore config,
        AlertScheduler metrics,
        DeployService deploys,
        AuditLog audit,
        ILogger<AutoscaleSource> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(deploys);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(logger);
        _config = config;
        _metrics = metrics;
        _deploys = deploys;
        _audit = audit;
        _logger = logger;
    }

    public string Name => "autoscale";

    public IReadOnlyList<ScheduledJob> Due(DateTimeOffset now)
    {
        var sample = _metrics.Latest;
        if (sample is null || !sample.DockerReachable)
        {
            // No sample is not "quiet". Deciding anything from a docker that could
            // not be reached is how an app gets taken apart during an outage.
            return [];
        }

        var jobs = new List<ScheduledJob>();
        foreach (var app in _config.Current.Apps)
        {
            var store = new DeploySettingsStore(app.ComposeFile);
            var settings = store.Load();
            if (!settings.Autoscale.Enabled)
            {
                continue;
            }

            if (ProjectFor(app) is not { } project)
            {
                // Nothing to match containers by, so there is nothing to read and
                // nothing to decide from.
                continue;
            }

            var current = DeploySettings.ClampReplicas(settings.Replicas);
            var readings = Readings(sample, MetricsProjectFor(project, settings));
            _state.TryGetValue(app.ComposeFile, out var state);

            var decision = Autoscale.Decide(
                settings.Autoscale, current, readings.Cpu, readings.Memory, state, now);
            _state[app.ComposeFile] = decision.State;

            if (decision.Changed)
            {
                jobs.Add(new ScheduledJob(
                    JobPrefix + app.Id,
                    token => ScaleAsync(app, store, settings, decision, token)));
            }
        }

        return jobs;
    }

    /// <summary>
    /// The compose project this app's containers are named after: the file's own
    /// <c>name:</c> when it has one — that is what compose actually uses, including
    /// for a hand-edited project — and the repository-derived name before the file
    /// exists. Null when neither can be read, which also means there are no
    /// containers to find.
    /// </summary>
    internal static string? ProjectFor(AppConnection app)
    {
        ArgumentNullException.ThrowIfNull(app);

        try
        {
            if (File.Exists(app.ComposeFile)
                && ComposeProjectName.ReadFrom(SecureFile.ReadAllText(app.ComposeFile)) is { } declared)
            {
                return declared;
            }

            return ComposeProjectName.FromRepository(GitHubRepositoryParser.Parse(app.RepoUrl).Name);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The project whose containers carry this app's traffic. For a coloured
    /// project that is the active colour's project, not the plain one: with the
    /// previous colour kept running for instant rollback, the plain prefix matched
    /// both colours, and averaging the idle colour in halved every reading — an
    /// overloaded app read as comfortable, and a comfortable one as quiet enough
    /// to scale down.
    /// </summary>
    internal static string MetricsProjectFor(string project, DeploySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.BlueGreen
            ? DeployColors.ProjectName(project, DeployColors.Normalize(settings.ActiveColor))
            : project;
    }

    /// <summary>
    /// The average CPU and memory across this app's containers.
    ///
    /// <para>An average rather than a maximum, because the question the target
    /// answers is "are the copies as a group keeping up" — one copy briefly pegged
    /// while the others idle is a load balancer detail, not a reason to start
    /// another container.</para>
    /// </summary>
    internal static (double? Cpu, double? Memory) Readings(MetricSample sample, string project)
    {
        var cpu = new List<double>();
        var memory = new List<double>();

        foreach (var container in sample.Containers)
        {
            // Compose names every copy `<project>-app-<n>`, so the prefix is the
            // compose project — NOT the app id, which is `<owner>-<repo>` while the
            // project is the repository alone. Matching on the id meant nothing ever
            // matched, and an autoscaler with no readings decides nothing: it read as
            // "the load never crossed the target" for as long as it was switched on.
            // For a coloured project the caller passes the active colour's project,
            // so the kept idle colour's containers do not water the average down.
            if (!container.Name.StartsWith(project + "-", StringComparison.OrdinalIgnoreCase) || container.Down)
            {
                continue;
            }

            if (container.Cpu is { } containerCpu)
            {
                cpu.Add(containerCpu);
            }

            if (container.Memory is { } containerMemory)
            {
                memory.Add(containerMemory);
            }
        }

        return (
            cpu.Count > 0 ? cpu.Average() : null,
            memory.Count > 0 ? memory.Average() : null);
    }

    private async Task ScaleAsync(
        AppConnection app,
        DeploySettingsStore store,
        DeploySettings settings,
        AutoscaleDecision decision,
        CancellationToken cancellationToken)
    {
        var from = settings.Replicas;

        // Update, not Save: `settings` is the snapshot Due() loaded, and a save of
        // the whole thing would put back every field somebody changed since — a
        // blue-green cutover writes ActiveColor between this job being scheduled
        // and running, and clobbering it re-points the reconciler at the retired
        // colour. Changing one field changes one field.
        store.Update(stored => stored.Replicas = decision.Replicas);

        var applied = await _deploys.ApplyComposeAsync(app.ComposeFile, cancellationToken).ConfigureAwait(false);
        if (applied is null)
        {
            // A deploy holds the project. The count is saved, so the deploy that is
            // running will use it — and the next tick will apply it if not.
            _logger.LogInformation(
                "Scaling {App} to {Replicas} is waiting for a deploy to finish", app.Id, decision.Replicas);
            return;
        }

        _logger.LogWarning(
            "{App} scaled from {From} to {To}: {Reason}", app.Id, from, decision.Replicas, decision.Reason);

        // "pinqops" rather than a user: nobody asked for this one, and attributing
        // it to whoever last logged in would be a lie in the one record that exists
        // to answer "who did this".
        _audit.Append(new AuditEntry(
            DateTimeOffset.UtcNow,
            User: "pinqops",
            Action: "autoscale",
            Target: app.Id,
            Result: $"{from} -> {decision.Replicas} copies: {decision.Reason}",
            Status: 200));
    }
}
