using PinqOps.Alerts;
using Xunit;

namespace PinqOps.Tests.Alerts;

/// <summary>
/// A firing episode outliving a docker outage, for a rule that watches every
/// container.
///
/// <para><see cref="AlertEvaluator"/> is careful about this: being written off as
/// no-data is deliberately not a resolve, so <c>FiredAtUtc</c> and
/// <c>LastNotifiedUtc</c> survive it and the operator still gets the "resolved"
/// they are owed once readings come back. But which series a wildcard rule
/// evaluates is decided one level up, in <see cref="AlertTick"/>, and that only
/// re-added a tracked series while its health was not yet NoData. The tick after
/// the write-off therefore never evaluated it, and the returned map — which is
/// authoritative — dropped the whole state, open episode and all. Docker coming
/// back then built a fresh state with no episode in it, so the "resolved" was
/// never sent and a receiver that opens an incident on <c>alert_firing</c> keeps
/// it open for ever. A rule naming one container by name never lost it, so the
/// two rule shapes disagreed about the same outage.</para>
/// </summary>
public class AlertEpisodeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private const string Container = "api-1";

    /// <summary>
    /// Wider than the rule's default <see cref="AlertRule.NoDataAfterSeconds"/>, so
    /// the series really is written off rather than still inside its grace window.
    /// </summary>
    private static readonly TimeSpan PastTheNoDataGrace = TimeSpan.FromSeconds(360);

    /// <summary>The docker daemon did not answer: no containers, and it says so.</summary>
    private static MetricSample Unreachable(DateTimeOffset at) => new()
    {
        At = at,
        Containers = [],
        DockerReachable = false,
    };

    private static MetricSample Reporting(DateTimeOffset at, bool unhealthy) => new()
    {
        At = at,
        Containers = [new ContainerMetrics { Name = Container, Unhealthy = unhealthy }],
    };

    private static AlertRule WildcardUnhealthyRule() => new()
    {
        Id = "unhealthy1",
        Name = "Container unhealthy",
        Metric = AlertMetrics.ContainerUnhealthy,
        Target = "*",
        Threshold = 0,
        ForSeconds = 0,
    };

    [Fact]
    public void AnAnnouncedEpisodeSurvivesADockerOutageAndStillResolves()
    {
        var rule = WildcardUnhealthyRule();
        var darkAt = T0 + PastTheNoDataGrace;
        var stillDarkAt = darkAt.AddMinutes(1);
        var recoveredAt = darkAt.AddMinutes(2);

        var fired = AlertTick.Run(
            [rule], Reporting(T0, unhealthy: true), new Dictionary<string, AlertSeriesState>(), T0);
        Assert.Equal(AlertTransitionKind.Firing, Assert.Single(fired.Transitions).Kind);

        // Six minutes of silence from the daemon: past the grace window, so the
        // series is written off — which is reported as no-data, not as an all-clear.
        var writtenOff = AlertTick.Run([rule], Unreachable(darkAt), fired.States, darkAt);
        Assert.Equal(AlertTransitionKind.NoData, Assert.Single(writtenOff.Transitions).Kind);

        // Still nothing from docker. Nothing new to say, but the open episode has to
        // survive this tick or there is nothing left to resolve.
        var stillDark = AlertTick.Run([rule], Unreachable(stillDarkAt), writtenOff.States, stillDarkAt);
        Assert.Empty(stillDark.Transitions);

        var recovered = AlertTick.Run(
            [rule], Reporting(recoveredAt, unhealthy: false), stillDark.States, recoveredAt);

        Assert.Equal(AlertTransitionKind.Resolved, Assert.Single(recovered.Transitions).Kind);
    }

    /// <summary>
    /// The same thing one tick earlier, so a failure says which half broke: the
    /// state carrying the episode has to still be in the authoritative map.
    /// </summary>
    [Fact]
    public void AWrittenOffSeriesWithAnOpenEpisodeIsStillTracked()
    {
        var rule = WildcardUnhealthyRule();
        var darkAt = T0 + PastTheNoDataGrace;
        var stillDarkAt = darkAt.AddMinutes(1);

        var fired = AlertTick.Run(
            [rule], Reporting(T0, unhealthy: true), new Dictionary<string, AlertSeriesState>(), T0);
        var writtenOff = AlertTick.Run([rule], Unreachable(darkAt), fired.States, darkAt);

        var stillDark = AlertTick.Run([rule], Unreachable(stillDarkAt), writtenOff.States, stillDarkAt);

        var state = Assert.Single(stillDark.States).Value;
        Assert.Equal(Container, state.Series);
        Assert.NotNull(state.FiredAtUtc);
        Assert.NotNull(state.LastNotifiedUtc);
    }

    /// <summary>
    /// The mirror case, which must keep working exactly as it does today: a series
    /// with no episode open is written off once and then drops out of the map, so a
    /// container that is gone for good stops being rediscovered and re-announced on
    /// every tick.
    /// </summary>
    [Fact]
    public void AWrittenOffSeriesWithNoEpisodeStillDropsOut()
    {
        var rule = WildcardUnhealthyRule();
        var darkAt = T0 + PastTheNoDataGrace;
        var stillDarkAt = darkAt.AddMinutes(1);

        var healthy = AlertTick.Run(
            [rule], Reporting(T0, unhealthy: false), new Dictionary<string, AlertSeriesState>(), T0);
        Assert.Empty(healthy.Transitions);

        var writtenOff = AlertTick.Run([rule], Unreachable(darkAt), healthy.States, darkAt);
        Assert.Equal(AlertHealth.NoData, Assert.Single(writtenOff.States).Value.Health);

        var stillDark = AlertTick.Run([rule], Unreachable(stillDarkAt), writtenOff.States, stillDarkAt);

        Assert.Empty(stillDark.States);
        Assert.Empty(stillDark.Transitions);
    }

    /// <summary>
    /// And once the episode is closed the series stops being held open by it, so
    /// the resolve does not leave a state behind that lives for ever.
    /// </summary>
    [Fact]
    public void AResolvedSeriesDropsOutOnceItGoesQuietAgain()
    {
        var rule = WildcardUnhealthyRule();
        var recoveredAt = T0 + PastTheNoDataGrace;
        var darkAt = recoveredAt + PastTheNoDataGrace;
        var stillDarkAt = darkAt.AddMinutes(1);

        var fired = AlertTick.Run(
            [rule], Reporting(T0, unhealthy: true), new Dictionary<string, AlertSeriesState>(), T0);
        var recovered = AlertTick.Run(
            [rule], Reporting(recoveredAt, unhealthy: false), fired.States, recoveredAt);
        Assert.Equal(AlertTransitionKind.Resolved, Assert.Single(recovered.Transitions).Kind);

        var writtenOff = AlertTick.Run([rule], Unreachable(darkAt), recovered.States, darkAt);
        var stillDark = AlertTick.Run([rule], Unreachable(stillDarkAt), writtenOff.States, stillDarkAt);

        Assert.Empty(stillDark.States);
    }
}
