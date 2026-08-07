using System.Globalization;
using PinqOps.Alerts;
using Xunit;

namespace PinqOps.Tests.Alerts;

public class MetricParsingTests
{
    [Theory]
    [InlineData("1.23%", 1.23)]
    [InlineData("0.00%", 0)]
    [InlineData("100.00%", 100)]
    [InlineData("12%", 12)]
    [InlineData(" 3.5 % ", 3.5)]
    public void Percent_ReadsDockersFormat(string text, double expected)
    {
        Assert.Equal(expected, MetricParsing.Percent(text));
    }

    [Theory]
    [InlineData("--")]
    [InlineData("N/A")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("abc")]
    public void Percent_TreatsPlaceholdersAsNoReading(string? text)
    {
        // Not zero: "no reading" must reach the evaluator as no-data, or a missing
        // metric would quietly resolve a live alert.
        Assert.Null(MetricParsing.Percent(text));
    }

    [Fact]
    public void Percent_UsesInvariantCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // Under tr-TR the ambient parse reads "1.23" as 123 — a hundredfold
            // overstatement that would fire every CPU rule on the host.
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

            Assert.Equal(1.23, MetricParsing.Percent("1.23%"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("0B", 0)]
    [InlineData("512B", 512)]
    [InlineData("1KiB", 1024)]
    [InlineData("1.5GiB", 1610612736)]
    [InlineData("1.5 GiB", 1610612736)]
    [InlineData("2MB", 2097152)]
    [InlineData("1TiB", 1099511627776)]
    public void Bytes_UnderstandsBothUnitFamilies(string text, double expected)
    {
        Assert.Equal(expected, MetricParsing.Bytes(text));
    }

    [Theory]
    [InlineData("--")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("GiB")]
    [InlineData("5 parsecs")]
    public void Bytes_RejectsWhatItCannotRead(string? text)
    {
        Assert.Null(MetricParsing.Bytes(text));
    }

    [Fact]
    public void Bytes_IsCultureInvariant()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.Equal(1610612736, MetricParsing.Bytes("1.5GiB"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void MemUsage_SplitsUsedFromLimit()
    {
        var (used, limit) = MetricParsing.MemUsage("1.5GiB / 4GiB");

        Assert.Equal(1610612736, used);
        Assert.Equal(4294967296, limit);
    }

    [Fact]
    public void MemoryPercent_PrefersDockersOwnFigure()
    {
        Assert.Equal(37.5, MetricParsing.MemoryPercent("37.5%", "1.5GiB / 4GiB"));
    }

    [Fact]
    public void MemoryPercent_FallsBackWhenTheContainerHasNoLimit()
    {
        // docker prints "--" for MemPerc when no cgroup memory limit is set.
        Assert.Equal(37.5, MetricParsing.MemoryPercent("--", "1.5GiB / 4GiB"));
    }

    [Fact]
    public void MemoryPercent_IsNullWhenNeitherFormIsUsable()
    {
        Assert.Null(MetricParsing.MemoryPercent("--", "--"));
    }

    [Theory]
    [InlineData("running", "Up 5 minutes", true)]
    [InlineData("exited", "Exited (137) 4 minutes ago", false)]
    [InlineData("restarting", "Restarting (1) 3 seconds ago", false)]
    [InlineData("", "Up 2 hours", true)]
    [InlineData("", "Exited (0) 1 hour ago", false)]
    public void IsRunning_ReadsStateFirst_ThenStatus(string state, string status, bool running)
    {
        Assert.Equal(running, MetricParsing.IsRunning(state, status));
    }

    [Theory]
    [InlineData("Up 5 minutes (unhealthy)", true)]
    [InlineData("Up 5 minutes (healthy)", false)]
    [InlineData("Up 2 seconds (health: starting)", false)]
    [InlineData("Up 5 minutes", false)]
    public void IsUnhealthy_IgnoresAHealthCheckThatIsStillStarting(string status, bool unhealthy)
    {
        // A container that has only just come up would otherwise page somebody on
        // every deploy.
        Assert.Equal(unhealthy, MetricParsing.IsUnhealthy(status));
    }

    [Theory]
    [InlineData("restarting", "Restarting (1) 3 seconds ago", true)]
    [InlineData("", "Restarting (1) 3 seconds ago", true)]
    [InlineData("running", "Up 5 minutes", false)]
    public void IsRestarting_SpotsARestartLoop(string state, string status, bool restarting)
    {
        Assert.Equal(restarting, MetricParsing.IsRestarting(state, status));
    }

    [Theory]
    [InlineData("app", "app")]
    [InlineData("app,alias", "app")]
    [InlineData(" app , alias ", "app")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void FirstName_MatchesHowDockerStatsKeysContainers(string? names, string expected)
    {
        Assert.Equal(expected, MetricParsing.FirstName(names));
    }
}

public class CpuTimesTests
{
    // The real thing, including the double space after "cpu".
    private const string ProcStat = "cpu  10132153 290696 3084719 46828483 16683 0 25195 0 0 0";

    [Fact]
    public void Parse_ReadsTheAggregateLine()
    {
        var times = CpuTimes.Parse(ProcStat);

        Assert.NotNull(times);
        Assert.Equal(46828483 + 16683, times!.Value.Idle);
        Assert.Equal(10132153d + 290696 + 3084719 + 46828483 + 16683 + 0 + 25195, times.Value.Total);
    }

    [Fact]
    public void Parse_HandlesAKernelWithFewerFields()
    {
        var times = CpuTimes.Parse("cpu 100 20 30 400 5");

        Assert.NotNull(times);
        Assert.Equal(405, times!.Value.Idle);
        Assert.Equal(555, times.Value.Total);
    }

    [Theory]
    [InlineData("cpu0 100 20 30 400 5")]
    [InlineData("intr 1 2 3")]
    [InlineData("cpu 1 2")]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_RejectsAnythingElse(string? line)
    {
        Assert.Null(CpuTimes.Parse(line));
    }

    [Fact]
    public void Parse_IsCultureInvariant()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

            Assert.NotNull(CpuTimes.Parse(ProcStat));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void PercentBusy_IsTheNonIdleShareOfTheDelta()
    {
        var before = new CpuTimes(Idle: 100, Total: 200);
        var after = new CpuTimes(Idle: 150, Total: 300);

        Assert.Equal(50, CpuTimes.PercentBusy(before, after));
    }

    [Fact]
    public void PercentBusy_WithNoPreviousReading_IsUnknown()
    {
        // The first tick after start: null, never a fabricated 0% that would read
        // as "the machine is idle".
        Assert.Null(CpuTimes.PercentBusy(null, new CpuTimes(1, 2)));
    }

    [Theory]
    [InlineData(200, 200)]
    [InlineData(200, 100)]
    public void PercentBusy_WithNoAdvance_OrACounterReset_IsUnknown(double beforeTotal, double afterTotal)
    {
        Assert.Null(CpuTimes.PercentBusy(new CpuTimes(0, beforeTotal), new CpuTimes(0, afterTotal)));
    }

    [Fact]
    public void PercentBusy_IsClampedToASensibleRange()
    {
        // Idle going backwards relative to total would otherwise exceed 100%.
        var percent = CpuTimes.PercentBusy(new CpuTimes(100, 100), new CpuTimes(90, 200));

        Assert.InRange(percent!.Value, 0, 100);
    }
}
