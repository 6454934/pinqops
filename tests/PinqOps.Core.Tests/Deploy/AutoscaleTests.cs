using PinqOps.Deploy;
using Xunit;

namespace PinqOps.Tests.Deploy;

/// <summary>
/// The decision, without a clock or a server under load. Everything that makes a
/// controller behave — the window a breach has to hold for, the cooldown after a
/// change, the gap between scaling up and scaling down — is in here, because a
/// controller that reacts to one sample is a controller that oscillates.
/// </summary>
public class AutoscaleTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private static AutoscaleSettings Settings(
        int min = 1, int max = 3, int holdSeconds = 180, int upCooldown = 300, int downCooldown = 600) => new()
    {
        Enabled = true,
        MinReplicas = min,
        MaxReplicas = max,
        TargetCpuPercent = 70,
        TargetMemoryPercent = 80,
        HoldSeconds = holdSeconds,
        ScaleUpCooldownSeconds = upCooldown,
        ScaleDownCooldownSeconds = downCooldown,
    };

    /// <summary>Feeds one reading repeatedly, a minute apart, and returns every decision.</summary>
    private static List<AutoscaleDecision> Run(
        AutoscaleSettings settings, int replicas, double? cpu, int minutes, AutoscaleState state = default)
    {
        var decisions = new List<AutoscaleDecision>();
        for (var minute = 0; minute < minutes; minute++)
        {
            var decision = Autoscale.Decide(settings, replicas, cpu, null, state, Noon.AddMinutes(minute));
            state = decision.State;
            replicas = decision.Replicas;
            decisions.Add(decision);
        }

        return decisions;
    }

    [Fact]
    public void ADisabledControllerNeverChangesAnything()
    {
        var decision = Autoscale.Decide(
            new AutoscaleSettings { Enabled = false }, 1, cpuPercent: 99, memoryPercent: 99, default, Noon);

        Assert.False(decision.Changed);
        Assert.Equal(1, decision.Replicas);
    }

    [Fact]
    public void OneBusyMinuteIsNotAReasonToStartAContainer()
    {
        var decision = Autoscale.Decide(Settings(), 1, cpuPercent: 95, memoryPercent: null, default, Noon);

        Assert.False(decision.Changed);
        Assert.Equal(Noon, decision.State.OverSince);
    }

    [Fact]
    public void LoadThatHoldsPastTheWindowAddsACopy()
    {
        var decisions = Run(Settings(), replicas: 1, cpu: 95, minutes: 4);

        var change = Assert.Single(decisions, decision => decision.Changed);
        Assert.Equal(2, change.Replicas);
        Assert.Contains("above the target", change.Reason);
    }

    /// <summary>
    /// One step at a time. Jumping to a computed target reads well and behaves badly
    /// on one server: the reading that justified it was taken before any of the new
    /// containers existed.
    /// </summary>
    [Fact]
    public void ItAddsOneCopyAtATimeRatherThanJumpingToATarget()
    {
        var decisions = Run(Settings(max: 5), replicas: 1, cpu: 400, minutes: 4);

        Assert.Equal(2, decisions.Last().Replicas);
    }

    [Fact]
    public void AfterScalingUpItWaitsOutTheCooldownBeforeScalingAgain()
    {
        // Load never lets up; the only thing holding the second increase back is the
        // cooldown. The first comes at minute 1 (the window), the second no earlier
        // than minute 6 (five minutes after it).
        var decisions = Run(Settings(max: 5, holdSeconds: 60, upCooldown: 300), replicas: 1, cpu: 95, minutes: 8);

        var minutesChanged = decisions
            .Select((decision, minute) => (decision, minute))
            .Where(entry => entry.decision.Changed)
            .ToList();

        Assert.Equal([2, 3], minutesChanged.Select(entry => entry.decision.Replicas));
        Assert.Equal(1, minutesChanged[0].minute);
        Assert.True(
            minutesChanged[1].minute - minutesChanged[0].minute >= 5,
            "the second increase waits out the five-minute cooldown");
    }

    [Fact]
    public void ItNeverGoesPastTheMaximum()
    {
        var decisions = Run(Settings(max: 2, holdSeconds: 60, upCooldown: 60), replicas: 1, cpu: 95, minutes: 20);

        Assert.Equal(2, decisions.Last().Replicas);
    }

    [Fact]
    public void QuietLoadThatHoldsRemovesACopy()
    {
        var decisions = Run(
            Settings(max: 5, holdSeconds: 60, downCooldown: 60), replicas: 3, cpu: 5, minutes: 4);

        var change = decisions.First(decision => decision.Changed);
        Assert.Equal(2, change.Replicas);
        Assert.Contains("well under the target", change.Reason);
    }

    [Fact]
    public void ItNeverGoesBelowTheMinimum()
    {
        var decisions = Run(Settings(min: 2, max: 5, holdSeconds: 60, downCooldown: 60), replicas: 3, cpu: 1, minutes: 30);

        Assert.Equal(2, decisions.Last().Replicas);
    }

    /// <summary>
    /// Scaling down the moment a reading dips under the target would put the app
    /// straight back over it with one fewer copy, and then back again. The gap
    /// between "over the target" and "well under it" is what stops that.
    /// </summary>
    [Fact]
    public void AReadingJustUnderTheTargetIsNotQuietEnoughToScaleDown()
    {
        var decisions = Run(Settings(max: 5, holdSeconds: 60, downCooldown: 60), replicas: 3, cpu: 60, minutes: 10);

        Assert.DoesNotContain(decisions, decision => decision.Changed);
    }

    /// <summary>
    /// A controller that scaled down because it could not see would take an app
    /// apart during a docker outage.
    /// </summary>
    [Fact]
    public void NoReadingAtAllHoldsTheCountRatherThanReadingAsQuiet()
    {
        var decisions = Run(Settings(max: 5, holdSeconds: 60, downCooldown: 60), replicas: 3, cpu: null, minutes: 30);

        Assert.DoesNotContain(decisions, decision => decision.Changed);
        Assert.Equal(3, decisions.Last().Replicas);
    }

    [Fact]
    public void AWindowThatWasInterruptedStartsAgain()
    {
        // Busy, busy, quiet, busy: the first two minutes must not count toward the
        // window the fourth one starts.
        var state = default(AutoscaleState);
        foreach (var (cpu, minute) in new[] { (95d, 0), (95d, 1), (5d, 2), (95d, 3) })
        {
            state = Autoscale.Decide(Settings(holdSeconds: 180), 1, cpu, null, state, Noon.AddMinutes(minute)).State;
        }

        Assert.Equal(Noon.AddMinutes(3), state.OverSince);
    }

    [Fact]
    public void MemoryAloneIsEnoughToScaleUp()
    {
        var state = default(AutoscaleState);
        AutoscaleDecision decision = default;
        for (var minute = 0; minute < 4; minute++)
        {
            decision = Autoscale.Decide(Settings(), 1, cpuPercent: 5, memoryPercent: 95, state, Noon.AddMinutes(minute));
            state = decision.State;
        }

        Assert.True(decision.Changed);
        Assert.Contains("memory 95%", decision.Reason);
    }

    /// <summary>
    /// A count outside the bounds is not something to converge on gradually — it is
    /// a bound being broken right now, so no window and no cooldown applies.
    /// </summary>
    [Fact]
    public void ACountOutsideTheBoundsIsCorrectedImmediately()
    {
        var tooMany = Autoscale.Decide(Settings(min: 1, max: 3), 8, cpuPercent: 5, memoryPercent: null, default, Noon);
        Assert.Equal(3, tooMany.Replicas);
        Assert.Contains("outside the 1-3 range", tooMany.Reason);

        var tooFew = Autoscale.Decide(Settings(min: 2, max: 3), 1, cpuPercent: 5, memoryPercent: null, default, Noon);
        Assert.Equal(2, tooFew.Replicas);
    }

    [Fact]
    public void AnInvertedRangeIsWidenedRatherThanLeavingNoValidCount()
    {
        // Otherwise the controller fights itself once a minute, forever.
        var settings = new AutoscaleSettings { MinReplicas = 5, MaxReplicas = 2 }.Normalized();

        Assert.Equal(5, settings.MinReplicas);
        Assert.Equal(5, settings.MaxReplicas);
    }

    [Fact]
    public void EveryBoundIsHeldToSomethingAServerCanRun()
    {
        var settings = new AutoscaleSettings
        {
            MinReplicas = 0,
            MaxReplicas = 5_000,
            TargetCpuPercent = 0,
            HoldSeconds = 1,
            ScaleUpCooldownSeconds = 0,
        }.Normalized();

        Assert.Equal(1, settings.MinReplicas);
        Assert.Equal(DeploySettings.MaximumReplicas, settings.MaxReplicas);
        Assert.Equal(1, settings.TargetCpuPercent);
        Assert.Equal(60, settings.HoldSeconds);
        Assert.Equal(60, settings.ScaleUpCooldownSeconds);
    }

    [Fact]
    public void ItIsOffUntilSomebodyTurnsItOn() => Assert.False(new AutoscaleSettings().Enabled);
}
