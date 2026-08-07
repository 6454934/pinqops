using System.Net;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

public class TrustedProxiesTests
{
    // Unset is the default deployment: X-Forwarded-For must be ignored, because
    // believing it unconditionally would let any caller choose its own throttle
    // bucket.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unset_IsEmpty(string? value)
    {
        Assert.True(TrustedProxies.Parse(value).IsEmpty);
    }

    [Fact]
    public void ParsesASingleAddress()
    {
        var parsed = TrustedProxies.Parse("127.0.0.1");

        Assert.Equal([IPAddress.Parse("127.0.0.1")], parsed.Addresses);
        Assert.Empty(parsed.Networks);
        Assert.False(parsed.IsEmpty);
    }

    [Fact]
    public void ParsesCidrRangesAsNetworks()
    {
        var parsed = TrustedProxies.Parse("10.0.0.0/8");

        Assert.Empty(parsed.Addresses);
        var network = Assert.Single(parsed.Networks);
        Assert.Equal(IPAddress.Parse("10.0.0.0"), network.BaseAddress);
        Assert.Equal(8, network.PrefixLength);
    }

    [Theory]
    [InlineData("127.0.0.1,10.0.0.0/8,::1")]
    [InlineData("127.0.0.1 10.0.0.0/8 ::1")]
    [InlineData(" 127.0.0.1 , 10.0.0.0/8 ; ::1 ")]
    public void AcceptsMixedSeparatorsAndWhitespace(string value)
    {
        var parsed = TrustedProxies.Parse(value);

        Assert.Equal(2, parsed.Addresses.Count);
        Assert.Single(parsed.Networks);
        Assert.Empty(parsed.Invalid);
    }

    [Fact]
    public void ParsesIpv6()
    {
        Assert.Equal([IPAddress.IPv6Loopback], TrustedProxies.Parse("::1").Addresses);
    }

    // One typo must not stop the dashboard from starting; it starts without
    // trusting that entry, which is the safe direction.
    [Fact]
    public void CollectsUnparseableEntriesInsteadOfThrowing()
    {
        var parsed = TrustedProxies.Parse("127.0.0.1,not-an-ip,10.0.0.0/999");

        Assert.Equal([IPAddress.Parse("127.0.0.1")], parsed.Addresses);
        Assert.Equal(["not-an-ip", "10.0.0.0/999"], parsed.Invalid);
    }

    // Nothing valid means nothing is trusted, even though entries were supplied.
    [Fact]
    public void OnlyInvalidEntries_StaysEmpty()
    {
        var parsed = TrustedProxies.Parse("nonsense");

        Assert.True(parsed.IsEmpty);
        Assert.Single(parsed.Invalid);
    }
}
