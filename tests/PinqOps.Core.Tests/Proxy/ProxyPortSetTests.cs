using PinqOps.Proxy;
using Xunit;

namespace PinqOps.Tests.Proxy;

public class ProxyPortSetTests
{
    private static PortEntry Port(int hostPort, bool enabled = true) => new()
    {
        HostPort = hostPort,
        TargetContainer = "app-1",
        TargetPort = 3000,
        Enabled = enabled,
    };

    [Fact]
    public void AConfigWithNoPortEntriesPublishesOnlyHttpAndHttps()
    {
        Assert.Equal([80, 443], ProxyPortSet.HostPorts(new DomainConfig()));
    }

    [Fact]
    public void PortEntriesAreAddedInOrder()
    {
        var config = new DomainConfig { Ports = [Port(9000), Port(8080)] };

        Assert.Equal([80, 443, 8080, 9000], ProxyPortSet.HostPorts(config));
    }

    [Fact]
    public void DisabledInvalidAndReservedEntriesAreLeftOut()
    {
        var config = new DomainConfig
        {
            Ports = [Port(8080, enabled: false), Port(0), Port(70000), Port(80), Port(443), Port(9000)],
        };

        Assert.Equal([80, 443, 9000], ProxyPortSet.HostPorts(config));
    }

    /// <summary>Two entries naming the same port must publish it once — docker
    /// refuses a duplicate <c>-p</c> for the same host port.</summary>
    [Fact]
    public void ADuplicatePortIsPublishedOnce()
    {
        var config = new DomainConfig { Ports = [Port(8080), Port(8080)] };

        Assert.Equal([80, 443, 8080], ProxyPortSet.HostPorts(config));
    }

    /// <summary>HTTPS is published on UDP too, which is what carries HTTP/3 — the
    /// historical flag set, kept exactly.</summary>
    [Fact]
    public void ThePublishArgumentsMatchWhatTheInstallerUsedToHardcode()
    {
        Assert.Equal(
            ["-p", "80:80", "-p", "443:443", "-p", "443:443/udp"],
            ProxyPortSet.PublishArguments(new DomainConfig()));
    }

    [Fact]
    public void AnExtraPortBecomesOneTcpFlag()
    {
        var arguments = ProxyPortSet.PublishArguments(new DomainConfig { Ports = [Port(8080)] });

        Assert.Equal(
            ["-p", "80:80", "-p", "443:443", "-p", "443:443/udp", "-p", "8080:8080"],
            arguments);
    }

    /// <summary>
    /// The drift check. A Caddyfile with a <c>:8080</c> block in front of a
    /// container with no <c>-p 8080</c> is a route that exists on paper and refuses
    /// every connection, so the two have to be comparable.
    /// </summary>
    [Fact]
    public void MatchesComparesTheSetsRegardlessOfOrder()
    {
        var config = new DomainConfig { Ports = [Port(8080)] };

        Assert.True(ProxyPortSet.Matches(config, [8080, 443, 80]));
        Assert.False(ProxyPortSet.Matches(config, [80, 443]));
        Assert.False(ProxyPortSet.Matches(config, [80, 443, 8080, 9000]));
    }
}
