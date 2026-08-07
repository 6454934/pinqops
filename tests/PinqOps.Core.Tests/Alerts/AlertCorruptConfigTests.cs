using PinqOps.Alerts;
using Xunit;

namespace PinqOps.Tests.Alerts;

/// <summary>
/// A config file can be hand-edited, half-written, or produced by an older or
/// newer pinqops. None of that may take the evaluator down: the worker re-reads
/// these files every minute, so a null that throws is not one bad request, it is
/// a monitoring system that has silently stopped watching.
/// </summary>
public class AlertCorruptConfigTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pinqops-alert-corrupt-").FullName;
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private AlertRuleStore RuleStore(string json)
    {
        var path = Path.Combine(_dir, "alerts.json");
        File.WriteAllText(path, json);
        return new AlertRuleStore(path);
    }

    [Fact]
    public void ExplicitNulls_DoNotSurviveIntoTheModel()
    {
        // A property initializer only runs when the member is absent. An explicit
        // null assigns straight over it.
        var rule = Assert.Single(RuleStore(
            """{"rules":[{"id":null,"name":null,"metric":null,"target":null,"comparator":null,"severity":null,"channels":null}]}""")
            .Load().Rules);

        Assert.Equal(string.Empty, rule.Id);
        Assert.Equal(string.Empty, rule.Name);
        Assert.Equal(string.Empty, rule.Metric);
        Assert.Equal(string.Empty, rule.Target);
        Assert.Equal(string.Empty, rule.Comparator);
        Assert.Equal(string.Empty, rule.Severity);
        Assert.Empty(rule.Channels);
    }

    [Fact]
    public void ANullMetric_DoesNotThrowOutOfIsContainerRule()
    {
        // This is the one that actually bit: the worker asks every loaded rule
        // whether it is a container rule before anything validates it.
        var rule = Assert.Single(RuleStore("""{"rules":[{"id":"a","metric":null}]}""").Load().Rules);

        Assert.False(rule.IsContainerRule);
    }

    [Fact]
    public void ANullRuleList_LoadsAsEmpty()
    {
        Assert.Empty(RuleStore("""{"rules":null}""").Load().Rules);
    }

    [Fact]
    public void NullEntriesInTheRuleList_AreDropped()
    {
        Assert.Single(RuleStore("""{"rules":[null,{"id":"a","metric":"host.cpu"}]}""").Load().Rules);
    }

    [Fact]
    public void AWholeTickSurvivesAMangledRuleFile()
    {
        var rules = RuleStore(
            """
            {"rules":[
              {"id":"a","name":null,"metric":null,"channels":null,"enabled":true},
              {"id":"b","name":"ok","metric":"host.mem","threshold":1,"forSeconds":0,"enabled":true}]}
            """)
            .Load().Rules;

        var sample = new MetricSample { At = Now, Memory = 50 };
        var result = AlertTick.Run(rules, sample, new Dictionary<string, AlertSeriesState>(), Now);

        // The unusable rule is inert; the usable one still fires.
        var transition = Assert.Single(result.Transitions);
        Assert.Equal("b", transition.Rule.Id);
    }

    [Fact]
    public void ValidationRejectsAMangledRule_WithoutThrowingNullReference()
    {
        var rule = Assert.Single(RuleStore("""{"rules":[{"id":"a","metric":null,"channels":null}]}""").Load().Rules);

        Assert.Throws<ArgumentException>(() => AlertRuleValidator.Validate(rule));
    }

    [Fact]
    public void ChannelConfig_TolerateExplicitNulls()
    {
        var path = Path.Combine(_dir, "alert-channels.json");
        File.WriteAllText(path, """{"webhook":null,"slack":{"enabled":true,"webhookUrl":null},"telegram":null}""");

        var config = new AlertChannelStore(path).Load();

        Assert.Equal(string.Empty, config.Webhook.Url);
        Assert.Equal(string.Empty, config.Slack.WebhookUrl);
        Assert.Equal(string.Empty, config.Telegram.BotToken);
    }

    [Fact]
    public void StateFile_DropsNullEntries_RatherThanTheWholeMap()
    {
        var path = Path.Combine(_dir, "alert-state.json");
        File.WriteAllText(path, """{"a|":null,"b|":{"ruleId":"b","series":null,"health":"alerting"}}""");

        var states = new AlertStateStore(path).Load();

        var state = Assert.Single(states).Value;
        Assert.Equal("b", state.RuleId);
        Assert.Equal(string.Empty, state.Series);
        Assert.Equal(AlertHealth.Alerting, state.Health);
    }

    [Fact]
    public void StateFile_WithNullSeries_SurvivesHygiene()
    {
        var states = new Dictionary<string, AlertSeriesState>
        {
            ["a|"] = new() { RuleId = "a", Series = null! },
        };

        var repaired = AlertStateHygiene.ResetAfterDowntime(states, Now, TimeSpan.FromMinutes(2));

        Assert.Equal(string.Empty, Assert.Single(repaired).Value.Series);
    }

    [Fact]
    public void AnUnknownHealthValue_LoadsAsNormal_RatherThanThrowing()
    {
        var path = Path.Combine(_dir, "alert-state.json");
        File.WriteAllText(path, """{"a|":{"ruleId":"a","health":"on-fire"}}""");

        // An unparseable enum makes the whole document unreadable; the contract is
        // that we lose the state, not that the worker refuses to start.
        Assert.Empty(new AlertStateStore(path).Load());
    }
}
