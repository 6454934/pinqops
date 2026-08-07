namespace PinqOps.Deploy;

/// <summary>
/// Turns a coloured deploy's result into the history record and the outcome every
/// other deploy produces.
///
/// <para><b>Why this exists separately.</b> <see cref="BlueGreenDeployer"/> takes
/// neither a history store nor an observer, deliberately: it is the cutover
/// sequence and nothing else. But a deploy that leaves no trace is a deploy the
/// operator cannot see happened — turning a project's colours on used to switch
/// off its deploy history and its notifications at the same time, silently, which
/// is the opposite of what a no-gap release is for.</para>
/// </summary>
public static class BlueGreenRecord
{
    /// <summary>The history entry for a finished coloured deploy.</summary>
    public static DeployRecord For(
        BlueGreenResult result,
        string trigger,
        string? tag,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt,
        string? previousTag = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(trigger);

        return new DeployRecord
        {
            Id = DeployHistoryStore.NewRecordId(),
            Tag = tag ?? "latest",
            StartedAt = startedAt,
            DurationSeconds = Math.Round((finishedAt - startedAt).TotalSeconds, 1),
            Result = ResultFor(result, trigger),
            Trigger = trigger,
            PreviousTag = previousTag,
            HealthCheck = HealthFor(result),
            Error = result.Error,
        };
    }

    /// <summary>The same result as an outcome, for the notification channels.</summary>
    public static DeployOutcome OutcomeFor(
        BlueGreenResult result, string trigger, string? tag, string? previousTag = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(trigger);

        return new DeployOutcome
        {
            Result = ResultFor(result, trigger),
            Trigger = trigger,
            Tag = tag,
            PreviousTag = previousTag,
            HealthCheck = HealthFor(result),
            Error = result.Error,
        };
    }

    /// <summary>
    /// The history entry for a rollback that was a proxy switch back to the kept
    /// colour — no pull, no restart, and so no health verdict either.
    /// </summary>
    public static DeployRecord ForSwitchBack(
        string tag, string? previousTag, DateTimeOffset startedAt, DateTimeOffset finishedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        return new DeployRecord
        {
            Id = DeployHistoryStore.NewRecordId(),
            Tag = tag,
            StartedAt = startedAt,
            DurationSeconds = Math.Round((finishedAt - startedAt).TotalSeconds, 1),
            Result = DeployRecordValues.ResultRolledBack,
            Trigger = DeployRecordValues.TriggerRollback,
            PreviousTag = previousTag,
            HealthCheck = DeployRecordValues.HealthSkipped,
            Error = null,
        };
    }

    /// <summary>The switch-back as an outcome, for the notification channels.</summary>
    public static DeployOutcome OutcomeForSwitchBack(string tag, string? previousTag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        return new DeployOutcome
        {
            Result = DeployRecordValues.ResultRolledBack,
            Trigger = DeployRecordValues.TriggerRollback,
            Tag = tag,
            PreviousTag = previousTag,
            HealthCheck = DeployRecordValues.HealthSkipped,
            Error = null,
        };
    }

    /// <summary>
    /// A successful coloured rollback is recorded as <c>rolled_back</c>, exactly as
    /// <see cref="Deployer"/> records an ordinary one. Recording it as
    /// <c>succeeded</c> broke the rollback chain
    /// <see cref="DeployHistoryStore.LastSuccessfulTagBefore"/> follows: with no
    /// <c>rolled_back</c> record naming what was escaped, a second consecutive
    /// rollback rolled <em>forward</em> onto the release the first one had just
    /// left.
    /// </summary>
    private static string ResultFor(BlueGreenResult result, string trigger) =>
        !result.Succeeded ? DeployRecordValues.ResultFailed
        : trigger == DeployRecordValues.TriggerRollback ? DeployRecordValues.ResultRolledBack
        : DeployRecordValues.ResultSucceeded;

    /// <summary>
    /// A coloured deploy only reaches success after the compose health check and
    /// the readiness probe have both passed, so success is a passed health check
    /// and says so.
    ///
    /// <para>A failure is <em>not</em> recorded as a failed health check. The
    /// sequence can also fail at the pull, the eligibility gate or the proxy
    /// reload, and the result does not say which — calling all of those a failed
    /// health check would put a specific, wrong reason in the history. "Skipped"
    /// says no health verdict was reached; the error says what actually
    /// happened.</para>
    /// </summary>
    private static string HealthFor(BlueGreenResult result) =>
        result.Succeeded ? DeployRecordValues.HealthPassed : DeployRecordValues.HealthSkipped;
}
