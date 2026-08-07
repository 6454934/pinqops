using PinqOps.Alerts;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// The per-rule share of an alert batch is anti-starvation, not a ceiling.
///
/// <para>An eight-service stack going down produces eight firings for one
/// wildcard rule in one tick. The batch cap is twenty and nothing else is
/// competing, so there is room for all of them — but the per-rule share dropped
/// the last three outright while fifteen slots sat empty. The evaluator has
/// already stamped <c>LastNotifiedUtc</c> for every one of them, so with "notify
/// once" there is no second chance: those containers are never paged at all, and
/// when they come back the resolve reads the stamp as "announced" and sends an
/// all-clear for an alert nobody was told about. The sort is stable and container
/// names sort ordinally, so it is always the same alphabetically-late
/// containers.</para>
/// </summary>
public class AlertBatchCapTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private static AlertRule DownRule(string id) => new()
    {
        Id = id,
        Name = $"Container down ({id})",
        Metric = AlertMetrics.ContainerDown,
        Target = "*",
        Threshold = 0,
        ForSeconds = 0,
        Severity = AlertSeverity.Critical,
        ReNotifySeconds = 0,
    };

    private static List<AlertTransition> Firings(AlertRule rule, int count) =>
    [
        .. Enumerable.Range(1, count).Select(index => new AlertTransition
        {
            Rule = rule,
            Series = $"{rule.Id}-service-{index}",
            Kind = AlertTransitionKind.Firing,
            At = Now,
            Value = 1,
        }),
    ];

    private static int CountFor(IEnumerable<AlertTransition> transitions, string ruleId) =>
        transitions.Count(transition => string.Equals(transition.Rule.Id, ruleId, StringComparison.Ordinal));

    /// <summary>
    /// One rule, eight containers, nineteen free slots. Every one of them has to go
    /// out — this is the whole bug.
    /// </summary>
    [Fact]
    public void ALoneRuleUsesTheWholeBatchRatherThanItsShare()
    {
        var rule = DownRule("stack");

        var batch = AlertDispatcher.Prioritize(Firings(rule, 8));

        Assert.Equal(8, batch.Count);
    }

    /// <summary>
    /// And starvation protection is unchanged: every rule gets its share before any
    /// rule gets more than its share.
    /// </summary>
    [Fact]
    public void EveryRuleGetsItsShareBeforeAnyRuleGetsMore()
    {
        var noisy = DownRule("noisy");
        var quiet = DownRule("quiet");

        var batch = AlertDispatcher.Prioritize([.. Firings(noisy, 8), .. Firings(quiet, 8)]);

        Assert.Equal(16, batch.Count);
        var share = batch.Take(AlertDispatcher.MaxPerRule * 2).ToList();
        Assert.Equal(AlertDispatcher.MaxPerRule, CountFor(share, "noisy"));
        Assert.Equal(AlertDispatcher.MaxPerRule, CountFor(share, "quiet"));
    }

    /// <summary>
    /// The batch cap is still the real ceiling — the point of the second pass is to
    /// use the free slots, not to remove the limit.
    /// </summary>
    [Fact]
    public void TheBatchCapIsStillTheCeiling()
    {
        var first = DownRule("first");
        var second = DownRule("second");
        var third = DownRule("third");

        var batch = AlertDispatcher.Prioritize(
            [.. Firings(first, 10), .. Firings(second, 10), .. Firings(third, 10)]);

        Assert.Equal(AlertDispatcher.MaxPerBatch, batch.Count);
        Assert.Equal(AlertDispatcher.MaxPerRule, CountFor(batch.Take(AlertDispatcher.MaxPerRule * 3), "first"));
        Assert.Equal(AlertDispatcher.MaxPerRule, CountFor(batch.Take(AlertDispatcher.MaxPerRule * 3), "second"));
        Assert.Equal(AlertDispatcher.MaxPerRule, CountFor(batch.Take(AlertDispatcher.MaxPerRule * 3), "third"));
    }

    /// <summary>
    /// Severity still decides who goes first, so a critical rule is not pushed
    /// behind an info one by the leftovers pass.
    /// </summary>
    [Fact]
    public void SeverityStillDecidesTheOrder()
    {
        var critical = DownRule("critical");
        var info = DownRule("info");
        info.Severity = AlertSeverity.Info;

        var batch = AlertDispatcher.Prioritize([.. Firings(info, 8), .. Firings(critical, 8)]);

        Assert.Equal(AlertSeverity.Critical, batch[0].Rule.Severity);
        Assert.Equal(
            AlertDispatcher.MaxPerRule,
            CountFor(batch.Take(AlertDispatcher.MaxPerRule), "critical"));
    }
}
