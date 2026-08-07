using PinqOps.Deploy;
using Xunit;

namespace PinqOps.Tests.Deploy;

/// <summary>
/// Picking the address to probe on. The map is parsed as JSON because a Go template
/// cannot name a network with a hyphen in it, and everything in here is about the
/// shapes docker really produces.
/// </summary>
public class ContainerNetworkAddressTests
{
    private static string Networks(params (string Name, string Ip)[] entries) =>
        "{" + string.Join(",", entries.Select(entry =>
            $"\"{entry.Name}\":{{\"IPAddress\":\"{entry.Ip}\",\"Gateway\":\"172.20.0.1\"}}")) + "}";

    [Fact]
    public void TheAddressOnTheOnlyNetworkIsTheAnswer()
    {
        var address = ContainerNetworkAddress.Best(Networks(("pinqops-app-acme", "172.20.0.3")));

        Assert.Equal(new ContainerAddress("pinqops-app-acme", "172.20.0.3"), address);
    }

    [Fact]
    public void TheAppsOwnNetworkWinsOverTheSharedOne()
    {
        var address = ContainerNetworkAddress.Best(
            Networks(("pinqops-apps", "172.19.0.5"), ("pinqops-app-acme", "172.20.0.3")));

        Assert.Equal("pinqops-app-acme", address?.Network);
    }

    [Fact]
    public void TheSharedNetworkWinsOverOneNobodyHereCreated()
    {
        var address = ContainerNetworkAddress.Best(Networks(("bridge", "172.17.0.2"), ("pinqops-apps", "172.19.0.5")));

        Assert.Equal("pinqops-apps", address?.Network);
    }

    /// <summary>
    /// Two networks pinqops did not create must not resolve to whichever the map
    /// happened to list first: a probe that passes or fails on JSON ordering is
    /// worse than no probe.
    /// </summary>
    [Fact]
    public void TheChoiceDoesNotDependOnMapOrdering()
    {
        var oneWay = ContainerNetworkAddress.Best(Networks(("zeta", "172.30.0.2"), ("alpha", "172.31.0.2")));
        var theOther = ContainerNetworkAddress.Best(Networks(("alpha", "172.31.0.2"), ("zeta", "172.30.0.2")));

        Assert.Equal(oneWay, theOther);
        Assert.Equal("alpha", oneWay?.Network);
    }

    [Fact]
    public void ANetworkTheContainerHasLeftHasNoAddress()
    {
        // Docker reports an empty string for exactly this.
        Assert.Null(ContainerNetworkAddress.Best(Networks(("pinqops-apps", ""))));
    }

    [Fact]
    public void ANetworkWithAnAddressIsPreferredOverOneWithout()
    {
        var address = ContainerNetworkAddress.Best(Networks(("pinqops-app-acme", ""), ("pinqops-apps", "172.19.0.5")));

        Assert.Equal("pinqops-apps", address?.Network);
    }

    [Fact]
    public void SomethingThatIsNotAnAddressIsNotUsedAsOne()
    {
        // The value ends up in a URL.
        Assert.Null(ContainerNetworkAddress.Best("""{"pinqops-apps":{"IPAddress":"evil.example.com"}}"""));
    }

    [Fact]
    public void LoopbackIsNotAnAddressForAnotherContainer() =>
        Assert.Null(ContainerNetworkAddress.Best("""{"pinqops-apps":{"IPAddress":"127.0.0.1"}}"""));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    // What `docker inspect` prints for a container with no networks at all.
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void NothingUsableIsNull(string output) => Assert.Null(ContainerNetworkAddress.Best(output));

    [Fact]
    public void AnIpv6AddressIsUsable()
    {
        var address = ContainerNetworkAddress.Best("""{"pinqops-apps":{"IPAddress":"fd00::2"}}""");

        Assert.Equal("fd00::2", address?.IpAddress);
    }
}
