using System.Net;
using Xunit;

namespace PinqOps.Web.Tests;

public class DnsCheckCdnTests
{
    [Fact]
    public void ACloudflareAnycastAddressMatchesWhenEdgeTrustsItsRange()
    {
        // 104.16.0.0/13 is among Cloudflare's published ranges.
        Assert.True(ProxyService.IpInTrustedRanges("104.16.1.2", ["104.16.0.0/13", "172.64.0.0/13"]));
    }

    [Fact]
    public void AnUnrelatedAddressDoesNotMatch()
    {
        Assert.False(ProxyService.IpInTrustedRanges("203.0.113.7", ["104.16.0.0/13"]));
    }

    [Fact]
    public void GarbageIsNotAMatch()
    {
        Assert.False(ProxyService.IpInTrustedRanges("not-an-ip", ["104.16.0.0/13"]));
        Assert.False(ProxyService.IpInTrustedRanges("104.16.1.2", ["not-a-cidr"]));
    }
}
