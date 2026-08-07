using PinqOps.Alerts;
using Xunit;

namespace PinqOps.Tests.Alerts;

public class AlertRuleValidatorTests
{
    private static AlertRule Valid() => new()
    {
        Id = "abcd1234",
        Name = "Disk almost full",
        Metric = AlertMetrics.HostDisk,
        Comparator = AlertComparators.GreaterThan,
        Threshold = 90,
        ForSeconds = 300,
        Severity = AlertSeverity.Critical,
    };

    [Fact]
    public void AWellFormedRule_Passes()
    {
        AlertRuleValidator.Validate(Valid());
    }

    [Fact]
    public void NameIsRequired()
    {
        var rule = Valid();
        rule.Name = "   ";

        Assert.Throws<ArgumentException>(() => AlertRuleValidator.Validate(rule));
    }

    [Fact]
    public void NameIsLengthCapped()
    {
        var rule = Valid();
        rule.Name = new string('x', AlertRuleValidator.MaxNameLength + 1);

        Assert.Throws<ArgumentException>(() => AlertRuleValidator.Validate(rule));
    }

    [Fact]
    public void UnknownMetricIsRejected()
    {
        var rule = Valid();
        rule.Metric = "host.entropy";

        var error = Assert.Throws<ArgumentException>(() => AlertRuleValidator.Validate(rule));
        Assert.Contains("host.entropy", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("eq")]
    [InlineData("")]
    [InlineData("GT")]
    public void UnknownComparatorIsRejected(string comparator)
    {
        var rule = Valid();
        rule.Comparator = comparator;

        Assert.Throws<ArgumentException>(() => AlertRuleValidator.Validate(rule));
    }

    [Fact]
    public void UnknownSeverityIsRejected()
    {
        var rule = Valid();
        rule.Severity = "page-the-ceo";

        Assert.Throws<ArgumentException>(() => AlertRuleValidator.Validate(rule));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteThresholdIsRejected(double threshold)
    {
        var rule = Valid();
        rule.Threshold = threshold;

        Assert.Throws<ArgumentException>(() => AlertRuleValidator.Validate(rule));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(AlertRuleValidator.MaxForSeconds + 1)]
    public void OutOfRangeForDurationIsRejected(int forSeconds)
    {
        var rule = Valid();
        rule.ForSeconds = forSeconds;

        Assert.Throws<ArgumentException>(() => AlertRuleValidator.Validate(rule));
    }

    [Fact]
    public void UnknownChannelIsRejected()
    {
        var rule = Valid();
        rule.Channels = [AlertChannelNames.Slack, "carrier-pigeon"];

        Assert.Throws<ArgumentException>(() => AlertRuleValidator.Validate(rule));
    }

    [Fact]
    public void ContainerMetricNeedsATarget()
    {
        var rule = Valid();
        rule.Metric = AlertMetrics.ContainerCpu;
        rule.Target = string.Empty;

        Assert.Throws<ArgumentException>(() => AlertRuleValidator.Validate(rule));
    }

    [Fact]
    public void HostMetricRefusesATarget()
    {
        // Silently ignoring it would leave someone convinced a host rule was
        // scoped to one container.
        var rule = Valid();
        rule.Target = "app";

        Assert.Throws<ArgumentException>(() => AlertRuleValidator.Validate(rule));
    }

    [Fact]
    public void WildcardIsAValidContainerTarget()
    {
        var rule = Valid();
        rule.Metric = AlertMetrics.ContainerDown;
        rule.Target = "*";

        AlertRuleValidator.Validate(rule);
    }

    [Theory]
    [InlineData(AlertComparators.GreaterThan, 90, "> 90%")]
    [InlineData(AlertComparators.GreaterOrEqual, 90.5, ">= 90.5%")]
    [InlineData(AlertComparators.LessThan, 5, "< 5%")]
    public void DescribeCondition_ReadsLikeTheRule(string comparator, double threshold, string expected)
    {
        var rule = Valid();
        rule.Comparator = comparator;
        rule.Threshold = threshold;

        Assert.Equal(expected, AlertRuleValidator.DescribeCondition(rule));
    }

    [Fact]
    public void DescribeCondition_DropsThePercentSignForRatios()
    {
        var rule = Valid();
        rule.Metric = AlertMetrics.HostLoad1;
        rule.Threshold = 1.5;

        Assert.Equal("> 1.5", AlertRuleValidator.DescribeCondition(rule));
    }
}
