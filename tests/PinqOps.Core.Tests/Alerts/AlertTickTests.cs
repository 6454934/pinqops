using PinqOps.Alerts;
using Xunit;

namespace PinqOps.Tests.Alerts;

public class AlertTickTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private static MetricSample Sample(
        double? memory = 40, params (string Name, double Cpu, bool Down)[] containers) => new()
        {
            At = T0,
            Memory = memory,
            Containers = containers
                .Select(c => new ContainerMetrics { Name = c.Name, Cpu = c.Cpu, Down = c.Down })
                .ToList(),
        };

    private static AlertRule HostRule(double threshold = 90) => new()
    {
        Id = "host1",
        Name = "Memory high",
        Metric = AlertMetrics.HostMemory,
        Threshold = threshold,
        ForSeconds = 0,
    };

    private static AlertRule WildcardRule() => new()
    {
        Id = "cpu1",
        Name = "Container CPU",
        Metric = AlertMetrics.ContainerCpu,
        Target = "*",
        Threshold = 90,
        ForSeconds = 0,
    };

    [Fact]
    public void HostRule_EvaluatesTheEmptySeries()
    {
        var result = AlertTick.Run([HostRule()], Sample(memory: 95), new Dictionary<string, AlertSeriesState>(), T0);

        var state = Assert.Single(result.States).Value;
        Assert.Equal(string.Empty, state.Series);
        Assert.Equal(AlertHealth.Alerting, state.Health);
        Assert.Single(result.Transitions);
    }

    [Fact]
    public void DisabledRules_AreNotEvaluated_AndTheirStateIsRetired()
    {
        var rule = HostRule();
        var firing = AlertTick.Run([rule], Sample(memory: 95), new Dictionary<string, AlertSeriesState>(), T0);

        rule.Enabled = false;
        var after = AlertTick.Run([rule], Sample(memory: 95), firing.States, T0.AddMinutes(1));

        Assert.Empty(after.States);
        Assert.Empty(after.Transitions);
    }

    [Fact]
    public void WildcardRule_FansOutOverEveryContainer()
    {
        var sample = Sample(containers: [("app", 95, false), ("db", 3, false)]);

        var result = AlertTick.Run([WildcardRule()], sample, new Dictionary<string, AlertSeriesState>(), T0);

        Assert.Equal(2, result.States.Count);
        var transition = Assert.Single(result.Transitions);
        Assert.Equal("app", transition.Series);
    }

    [Fact]
    public void WildcardSeries_ThatVanishes_ReachesNoDataOnce_ThenDropsOut()
    {
        var rule = WildcardRule();
        rule.NoDataAfterSeconds = 0;

        var first = AlertTick.Run(
            [rule], Sample(containers: [("app", 5, false)]), new Dictionary<string, AlertSeriesState>(), T0);
        Assert.Single(first.States);

        // The container is gone from docker's listing. It is still tracked, so it
        // is written off as no-data — once.
        var second = AlertTick.Run([rule], Sample(), first.States, T0.AddMinutes(1));
        Assert.Equal(AlertHealth.NoData, Assert.Single(second.States).Value.Health);
        Assert.Equal(AlertTransitionKind.NoData, Assert.Single(second.Transitions).Kind);

        // From here it is simply not there any more: no state, and above all no
        // second no-data notification on every subsequent tick.
        var third = AlertTick.Run([rule], Sample(), second.States, T0.AddMinutes(2));
        Assert.Empty(third.States);
        Assert.Empty(third.Transitions);
    }

    [Fact]
    public void ExplicitTarget_IsEvaluatedEvenWhenTheContainerIsMissing()
    {
        var rule = WildcardRule();
        rule.Target = "db";
        rule.NoDataAfterSeconds = 0;

        var result = AlertTick.Run([rule], Sample(), new Dictionary<string, AlertSeriesState>(), T0);

        Assert.Equal(AlertHealth.NoData, Assert.Single(result.States).Value.Health);
    }

    [Fact]
    public void SilencedRules_AreRecordedButNotDelivered()
    {
        var rule = HostRule();
        rule.SilencedUntil = T0.AddHours(1);

        var result = AlertTick.Run([rule], Sample(memory: 95), new Dictionary<string, AlertSeriesState>(), T0);

        Assert.Equal(AlertHealth.Alerting, Assert.Single(result.States).Value.Health);
        Assert.Single(result.Transitions);
        Assert.Empty(result.Notifications);
    }

    [Fact]
    public void UnsilencedRules_AreBothRecordedAndDelivered()
    {
        var result = AlertTick.Run(
            [HostRule()], Sample(memory: 95), new Dictionary<string, AlertSeriesState>(), T0);

        Assert.Single(result.Transitions);
        Assert.Same(result.Transitions[0], Assert.Single(result.Notifications));
    }

    [Fact]
    public void RulesWithoutAnId_AreSkipped()
    {
        var rule = HostRule();
        rule.Id = string.Empty;

        var result = AlertTick.Run([rule], Sample(memory: 95), new Dictionary<string, AlertSeriesState>(), T0);

        Assert.Empty(result.States);
    }
}

public class AlertStateHygieneTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StatesFromBeforeADowntime_AreReset()
    {
        // Pending since three days ago: measuring that against today's clock would
        // fire on the first tick after a restart, which the "for" window exists to
        // prevent.
        var states = new Dictionary<string, AlertSeriesState>
        {
            ["r1|"] = new()
            {
                RuleId = "r1",
                Health = AlertHealth.Pending,
                SinceUtc = Now.AddDays(-3),
                LastSeenUtc = Now.AddDays(-3),
            },
        };

        var repaired = AlertStateHygiene.ResetAfterDowntime(states, Now, TimeSpan.FromMinutes(5));

        var state = repaired["r1|"];
        Assert.Equal(AlertHealth.Normal, state.Health);
        Assert.Null(state.SinceUtc);
        Assert.Null(state.LastSeenUtc);
        Assert.Equal("r1", state.RuleId);
    }

    [Fact]
    public void RecentStates_AreLeftAlone()
    {
        var states = new Dictionary<string, AlertSeriesState>
        {
            ["r1|"] = new()
            {
                RuleId = "r1",
                Health = AlertHealth.Alerting,
                SinceUtc = Now.AddMinutes(-2),
                LastSeenUtc = Now.AddMinutes(-1),
                LastNotifiedUtc = Now.AddMinutes(-2),
            },
        };

        var repaired = AlertStateHygiene.ResetAfterDowntime(states, Now, TimeSpan.FromMinutes(5));

        Assert.Equal(AlertHealth.Alerting, repaired["r1|"].Health);
        Assert.Equal(Now.AddMinutes(-2), repaired["r1|"].LastNotifiedUtc);
    }
}
