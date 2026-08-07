using System.Globalization;
using PinqOps.Alerts;
using Xunit;

namespace PinqOps.Tests.Alerts;

public class AlertMessageTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private static AlertTransition Transition(
        AlertRule rule, AlertTransitionKind kind, double? value, string series = "", TimeSpan? firingFor = null) =>
        new() { Rule = rule, Series = series, Kind = kind, At = Now, Value = value, FiringFor = firingFor };

    private static AlertRule DiskRule() => new()
    {
        Id = "abcd1234",
        Name = "Disk almost full",
        Metric = AlertMetrics.HostDisk,
        Threshold = 90,
        Severity = AlertSeverity.Critical,
    };

    private static AlertRule DownRule() => new()
    {
        Id = "ef567890",
        Name = "App down",
        Metric = AlertMetrics.ContainerDown,
        Target = "*",
        Threshold = 0,
        Severity = AlertSeverity.Critical,
    };

    [Fact]
    public void FiringText_NamesTheRule_TheReading_AndTheThreshold()
    {
        var text = AlertMessage.Text(Transition(DiskRule(), AlertTransitionKind.Firing, 93.4), "web-01");

        Assert.Equal(
            "pinqops @ web-01: CRITICAL ALERT — Disk almost full: disk usage is 93.4% (threshold > 90%).",
            text);
    }

    [Fact]
    public void ResolvedText_SaysHowLongItWasFiring()
    {
        var text = AlertMessage.Text(
            Transition(DiskRule(), AlertTransitionKind.Resolved, 71, firingFor: TimeSpan.FromMinutes(125)),
            "web-01");

        Assert.Equal("pinqops @ web-01: RESOLVED — Disk almost full: disk usage is 71%, after 2h 5m.", text);
    }

    [Fact]
    public void ReminderText_SaysItIsStillFiring()
    {
        var text = AlertMessage.Text(
            Transition(DiskRule(), AlertTransitionKind.Reminder, 95, firingFor: TimeSpan.FromMinutes(30)),
            "web-01");

        Assert.Contains("STILL FIRING", text, StringComparison.Ordinal);
        Assert.Contains("for 30m", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoDataText_NamesWhatWentQuiet()
    {
        var text = AlertMessage.Text(Transition(DownRule(), AlertTransitionKind.NoData, null, "acme-app-1"), "web-01");

        Assert.Equal("pinqops @ web-01: NO DATA — App down: acme-app-1 has stopped reporting.", text);
    }

    [Fact]
    public void StateMetrics_ReadAsEnglish_NotAsAZeroOrOne()
    {
        var text = AlertMessage.Text(Transition(DownRule(), AlertTransitionKind.Firing, 1, "acme-app-1"), "web-01");

        Assert.Equal("pinqops @ web-01: CRITICAL ALERT — App down: acme-app-1 is not running.", text);
        Assert.DoesNotContain("threshold", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ContainerCpu_NamesTheContainerAndTheMetric()
    {
        var rule = new AlertRule
        {
            Id = "c1",
            Name = "Busy container",
            Metric = AlertMetrics.ContainerCpu,
            Target = "*",
            Threshold = 90,
            Severity = AlertSeverity.Warning,
        };

        var text = AlertMessage.Text(Transition(rule, AlertTransitionKind.Firing, 97.25, "acme-app-1"), "web-01");

        Assert.Equal(
            "pinqops @ web-01: ALERT — Busy container: acme-app-1 CPU is 97.25% (threshold > 90%).",
            text);
    }

    [Fact]
    public void Payload_IsFlatAndSelfDescribing()
    {
        var payload = AlertMessage.Payload(
            Transition(DiskRule(), AlertTransitionKind.Firing, 93.4), "web-01");

        Assert.Equal(AlertMessage.FiringEvent, payload.Event);
        Assert.Equal("abcd1234", payload.RuleId);
        Assert.Equal("Disk almost full", payload.Rule);
        Assert.Equal(AlertMetrics.HostDisk, payload.Metric);
        Assert.Equal(string.Empty, payload.Series);
        Assert.Equal("critical", payload.Severity);
        Assert.Equal("> 90%", payload.Condition);
        Assert.Equal(93.4, payload.Value);
        Assert.False(payload.Repeat);
        Assert.Equal("web-01", payload.Host);
        Assert.Equal(Now, payload.Timestamp);
    }

    [Fact]
    public void ReminderPayload_IsMarkedAsARepeat()
    {
        var payload = AlertMessage.Payload(
            Transition(DiskRule(), AlertTransitionKind.Reminder, 95, firingFor: TimeSpan.FromMinutes(30)),
            "web-01");

        Assert.Equal(AlertMessage.FiringEvent, payload.Event);
        Assert.True(payload.Repeat);
        Assert.Equal(1800, payload.FiringForSeconds);
    }

    [Fact]
    public void ResolvedPayload_UsesTheResolvedEvent()
    {
        var payload = AlertMessage.Payload(Transition(DiskRule(), AlertTransitionKind.Resolved, 20), "web-01");

        Assert.Equal(AlertMessage.ResolvedEvent, payload.Event);
    }

    [Fact]
    public void NumbersAreFormattedInvariantly()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // A comma-decimal culture must not turn "93.4%" into "93,4%": the text
            // is machine-read by webhook consumers as often as it is read by people.
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            var text = AlertMessage.Text(Transition(DiskRule(), AlertTransitionKind.Firing, 93.4), "web-01");

            Assert.Contains("93.4%", text, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(45, "45s")]
    [InlineData(90, "1m")]
    [InlineData(3600, "1h")]
    [InlineData(7500, "2h 5m")]
    [InlineData(93600, "1d 2h")]
    [InlineData(172800, "2d")]
    public void Duration_IsCompact(int seconds, string expected)
    {
        Assert.Equal(expected, AlertMessage.Duration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Duration_ClampsNegatives()
    {
        Assert.Equal("0s", AlertMessage.Duration(TimeSpan.FromSeconds(-5)));
    }
}
