using PinqOps.Proxy;
using Xunit;

namespace PinqOps.Tests.Proxy;

/// <summary>
/// The four-case truth table behind "did we already say this". The bug it exists to
/// prevent is not a failure to notice an outage — it is saying the same thing every
/// minute for an hour, or saying nothing because a flag was never cleared.
/// </summary>
public class ProxyWatchdogTests
{
    [Fact]
    public void AProxyThatHasJustGoneDownIsReportedOnce()
    {
        var first = ProxyWatchdog.Observe(proxyRunning: false, reportedDown: false);

        Assert.Equal(ProxyWatchdogNotice.Down, first.Notice);
        Assert.True(first.ReportedDown);
    }

    [Fact]
    public void AProxyThatIsStillDownSaysNothingFurther()
    {
        var again = ProxyWatchdog.Observe(proxyRunning: false, reportedDown: true);

        Assert.Equal(ProxyWatchdogNotice.None, again.Notice);
        Assert.True(again.ReportedDown);
    }

    [Fact]
    public void ARecoveryIsOnlyWorthSayingToSomeoneWhoWasToldAboutTheOutage()
    {
        Assert.Equal(
            ProxyWatchdogNotice.Recovered,
            ProxyWatchdog.Observe(proxyRunning: true, reportedDown: true).Notice);

        Assert.Equal(
            ProxyWatchdogNotice.None,
            ProxyWatchdog.Observe(proxyRunning: true, reportedDown: false).Notice);
    }

    [Fact]
    public void AHealthyProxyCarriesNoOutstandingReport()
    {
        Assert.False(ProxyWatchdog.Observe(proxyRunning: true, reportedDown: true).ReportedDown);
        Assert.False(ProxyWatchdog.Observe(proxyRunning: true, reportedDown: false).ReportedDown);
    }

    /// <summary>
    /// A full down-and-up cycle, driven the way the worker drives it: the previous
    /// decision's state is the next observation's input. Asserting the cases one by
    /// one would not catch a decision that reports correctly but carries the wrong
    /// state forward.
    /// </summary>
    [Fact]
    public void OneOutageProducesExactlyOneNoticeAndOneRecovery()
    {
        var notices = new List<ProxyWatchdogNotice>();
        var reportedDown = false;

        foreach (var running in new[] { true, false, false, false, true, true })
        {
            var decision = ProxyWatchdog.Observe(running, reportedDown);
            reportedDown = decision.ReportedDown;
            if (decision.Notice != ProxyWatchdogNotice.None)
            {
                notices.Add(decision.Notice);
            }
        }

        Assert.Equal([ProxyWatchdogNotice.Down, ProxyWatchdogNotice.Recovered], notices);
    }

    private static PortEntry Port(string target, int hostPort = 8080, bool enabled = true) =>
        new() { Target = target, HostPort = hostPort, TargetContainer = target + "-app-1", TargetPort = 80, Enabled = enabled };

    [Fact]
    public void EnrolledTargetsAreTheAppsWhosePortTheProxyPublishes()
    {
        var config = new DomainConfig { Ports = [Port("beta", 8081), Port("alpha", 8080)] };

        Assert.Equal(["alpha", "beta"], ProxyWatchdog.EnrolledTargets(config));
    }

    [Fact]
    public void ADisabledEntryIsNotEnrolled()
    {
        // A disabled entry is left out of the proxy container's -p flags, so the app
        // is binding its own port again and a stopped proxy costs it nothing.
        var config = new DomainConfig { Ports = [Port("alpha", enabled: false)] };

        Assert.Empty(ProxyWatchdog.EnrolledTargets(config));
    }

    [Fact]
    public void AnEntryWithNoTargetOrAnImpossiblePortIsNotEnrolled()
    {
        var config = new DomainConfig
        {
            Ports = [Port(string.Empty), Port("alpha", hostPort: 0), Port("beta", hostPort: 70000)],
        };

        Assert.Empty(ProxyWatchdog.EnrolledTargets(config));
    }

    [Fact]
    public void AnAppIsNamedOnceEvenWithTwoPorts()
    {
        var config = new DomainConfig { Ports = [Port("alpha", 8080), Port("alpha", 8081)] };

        Assert.Equal(["alpha"], ProxyWatchdog.EnrolledTargets(config));
    }
}
