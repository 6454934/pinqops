using PinqOps;
using PinqOps.Deploy;
using Xunit;

namespace PinqOps.Tests.Deploy;

/// <summary>
/// What a coloured deploy leaves behind.
///
/// <para>Every ordinary deploy writes a history entry and hands an outcome to the
/// notification channels. A coloured one produced neither: the cutover sequence
/// takes no history store and no observer, and the caller returned as soon as it
/// finished. So switching a project to a no-gap release silently switched off its
/// deploy history and its alerts — the two things that tell an operator a deploy
/// happened and whether it worked.</para>
/// </summary>
public class BlueGreenRecordTests
{
    private static readonly DateTimeOffset Started = new(2026, 8, 2, 3, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Finished = Started.AddSeconds(42.5);

    private static BlueGreenResult Succeeded(string color = DeployColors.Green) =>
        new(Succeeded: true, Color: color, Switched: true, Error: null);

    private static BlueGreenResult Failed(string error) =>
        new(Succeeded: false, Color: DeployColors.Green, Switched: false, Error: error);

    [Fact]
    public void ASucceededDeployIsRecordedAsOne()
    {
        var record = BlueGreenRecord.For(
            Succeeded(), DeployRecordValues.TriggerCi, "sha-abc", Started, Finished);

        Assert.Equal(DeployRecordValues.ResultSucceeded, record.Result);
        Assert.Equal(DeployRecordValues.TriggerCi, record.Trigger);
        Assert.Equal("sha-abc", record.Tag);
        Assert.Equal(Started, record.StartedAt);
        Assert.Equal(42.5, record.DurationSeconds);
        Assert.Null(record.Error);
        Assert.NotEmpty(record.Id);
    }

    /// <summary>
    /// The sequence only reaches success once the compose health check and the
    /// readiness probe have passed, so that is what the entry says.
    /// </summary>
    [Fact]
    public void ASucceededDeployRecordsThatItWasProvedHealthy() =>
        Assert.Equal(
            DeployRecordValues.HealthPassed,
            BlueGreenRecord.For(Succeeded(), DeployRecordValues.TriggerCi, "sha-abc", Started, Finished).HealthCheck);

    [Fact]
    public void AFailedDeployCarriesItsReason()
    {
        var record = BlueGreenRecord.For(
            Failed("the proxy would not accept the new routes"),
            DeployRecordValues.TriggerManual,
            tag: null,
            Started,
            Finished);

        Assert.Equal(DeployRecordValues.ResultFailed, record.Result);
        Assert.Equal("the proxy would not accept the new routes", record.Error);
        // No tag given is the same "latest" every other record uses.
        Assert.Equal("latest", record.Tag);
    }

    /// <summary>
    /// A coloured deploy can fail at the pull, the eligibility gate or the proxy
    /// reload, and the result does not say which — so a failure must not be
    /// recorded as a failed <em>health check</em>, which would put a specific and
    /// often wrong reason in the history.
    /// </summary>
    [Fact]
    public void AFailedDeployDoesNotBlameTheHealthCheck()
    {
        var record = BlueGreenRecord.For(
            Failed("could not pull the image"), DeployRecordValues.TriggerCi, "sha-abc", Started, Finished);

        Assert.NotEqual(DeployRecordValues.HealthFailed, record.HealthCheck);
        Assert.Equal(DeployRecordValues.HealthSkipped, record.HealthCheck);
    }

    [Fact]
    public void TheOutcomeSaysTheSameThingAsTheRecord()
    {
        var result = Failed("the proxy would not accept the new routes");

        var record = BlueGreenRecord.For(result, DeployRecordValues.TriggerCi, "sha-abc", Started, Finished);
        var outcome = BlueGreenRecord.OutcomeFor(result, DeployRecordValues.TriggerCi, "sha-abc");

        Assert.Equal(record.Result, outcome.Result);
        Assert.Equal(record.Trigger, outcome.Trigger);
        Assert.Equal(record.Tag, outcome.Tag ?? "latest");
        Assert.Equal(record.HealthCheck, outcome.HealthCheck);
        Assert.Equal(record.Error, outcome.Error);
    }

    [Fact]
    public void EveryRecordGetsItsOwnId() =>
        Assert.NotEqual(
            BlueGreenRecord.For(Succeeded(), DeployRecordValues.TriggerCi, "a", Started, Finished).Id,
            BlueGreenRecord.For(Succeeded(), DeployRecordValues.TriggerCi, "a", Started, Finished).Id);

    /// <summary>
    /// A successful coloured rollback recorded as <c>succeeded</c> broke the chain
    /// <see cref="DeployHistoryStore.LastSuccessfulTagBefore"/> follows: with no
    /// <c>rolled_back</c> record naming what was escaped, a second consecutive
    /// rollback rolled forward onto the release the first one had just left.
    /// </summary>
    [Fact]
    public void ASucceededRollbackIsRecordedAsRolledBackWithTheTagItEscaped()
    {
        var record = BlueGreenRecord.For(
            Succeeded(), DeployRecordValues.TriggerRollback, "sha-2", Started, Finished, previousTag: "sha-3");

        Assert.Equal(DeployRecordValues.ResultRolledBack, record.Result);
        Assert.Equal("sha-2", record.Tag);
        Assert.Equal("sha-3", record.PreviousTag);

        var outcome = BlueGreenRecord.OutcomeFor(
            Succeeded(), DeployRecordValues.TriggerRollback, "sha-2", previousTag: "sha-3");
        Assert.Equal(DeployRecordValues.ResultRolledBack, outcome.Result);
        Assert.Equal("sha-3", outcome.PreviousTag);
    }

    [Fact]
    public void AFailedRollbackIsStillRecordedAsFailed() =>
        Assert.Equal(
            DeployRecordValues.ResultFailed,
            BlueGreenRecord.For(
                Failed("pull failed"), DeployRecordValues.TriggerRollback, "sha-2", Started, Finished, "sha-3").Result);

    /// <summary>
    /// The fast rollback — a proxy switch back to the kept colour — used to leave
    /// no record at all: invisible in the history, silent in the notification
    /// channels, and fatal to the rollback chain, since the next default rollback
    /// walked straight back onto the escaped release.
    /// </summary>
    [Fact]
    public void ASwitchBackIsARolledBackRecordWithNoHealthVerdict()
    {
        var record = BlueGreenRecord.ForSwitchBack("sha-2", "sha-3", Started, Finished);

        Assert.Equal(DeployRecordValues.ResultRolledBack, record.Result);
        Assert.Equal(DeployRecordValues.TriggerRollback, record.Trigger);
        Assert.Equal("sha-2", record.Tag);
        Assert.Equal("sha-3", record.PreviousTag);
        // No pull, no restart — and so no health verdict either.
        Assert.Equal(DeployRecordValues.HealthSkipped, record.HealthCheck);
        Assert.Equal(42.5, record.DurationSeconds);
        Assert.Null(record.Error);

        var outcome = BlueGreenRecord.OutcomeForSwitchBack("sha-2", "sha-3");
        Assert.Equal(DeployRecordValues.ResultRolledBack, outcome.Result);
        Assert.Equal("sha-3", outcome.PreviousTag);
        Assert.Equal(DeployRecordValues.HealthSkipped, outcome.HealthCheck);
    }

    /// <summary>
    /// The scenario the chain exists for, end to end: deploy sha-2 then sha-3,
    /// switch back to sha-2, and the next default rollback target must be sha-1's
    /// side of history — never sha-3, the release just escaped.
    /// </summary>
    [Fact]
    public void ASwitchBackRecordKeepsTheNextRollbackMovingBackwards()
    {
        var directory = Directory.CreateTempSubdirectory("pinqops-bluegreen-record-");
        try
        {
            var composeFile = Path.Combine(directory.FullName, "docker-compose.yml");
            var history = new DeployHistoryStore(composeFile);
            history.Append(BlueGreenRecord.For(
                Succeeded(), DeployRecordValues.TriggerCi, "sha-1", Started, Finished, previousTag: null));
            history.Append(BlueGreenRecord.For(
                Succeeded(), DeployRecordValues.TriggerCi, "sha-2", Started, Finished, "sha-1"));
            history.Append(BlueGreenRecord.For(
                Succeeded(), DeployRecordValues.TriggerCi, "sha-3", Started, Finished, "sha-2"));
            history.Append(BlueGreenRecord.ForSwitchBack("sha-2", "sha-3", Started, Finished));

            Assert.Equal("sha-1", history.LastSuccessfulTagBefore("sha-2"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
