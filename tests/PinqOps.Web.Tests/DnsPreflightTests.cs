using PinqOps.Proxy;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// The advisory check that runs before a domain is added.
///
/// <para>Two things have to hold, and neither did. It must accept every name the
/// route that calls it accepts — <c>POST /api/domains</c> normalizes with
/// <c>allowWildcard</c> taken from the DNS-01 settings and then hands the result
/// here, so a preflight that normalizes again with the default refuses exactly the
/// names DNS-01 exists to make possible. And it must not look up a name that
/// cannot have an address: a wildcard has no A record of its own, so resolving it
/// can only fail, and reporting that failure as "this domain does not point at
/// this server" is a warning about nothing.</para>
/// </summary>
public class DnsPreflightTests
{
    [Theory]
    [InlineData("app.example.com")]
    [InlineData("APP.Example.com.")]
    [InlineData(" app.example.com ")]
    public void AnOrdinaryNameIsLookedUp(string domain)
    {
        var (name, lookup) = ProxyService.Preflight(domain, allowWildcard: false);

        Assert.Equal("app.example.com", name);
        Assert.True(lookup);
    }

    /// <summary>
    /// The names the domain route hands over once a DNS provider is configured. It
    /// has already accepted them; refusing them here refuses the whole feature.
    /// </summary>
    [Fact]
    public void AWildcardTheRouteAcceptedIsAccepted()
    {
        var stored = DomainName.Normalize("*.example.com", allowWildcard: true);

        var (name, _) = ProxyService.Preflight(stored, allowWildcard: true);

        Assert.Equal(stored, name);
    }

    /// <summary>
    /// There is no address to compare against, so the lookup is skipped rather than
    /// run and reported as a mismatch. Reaching this server is still a real
    /// requirement — it just is not one this check can answer for a wildcard.
    /// </summary>
    [Fact]
    public void AWildcardIsNotLookedUp()
    {
        var (_, lookup) = ProxyService.Preflight("*.example.com", allowWildcard: true);

        Assert.False(lookup);
    }

    /// <summary>
    /// Without DNS-01 the refusal stands, and with the message it always had — the
    /// certificate genuinely cannot be issued, so taking the name would move the
    /// failure to issuance where nobody sees it.
    /// </summary>
    [Fact]
    public void AWildcardIsStillRefusedWithoutDnsChallenge()
    {
        var failure = Assert.Throws<ArgumentException>(
            () => ProxyService.Preflight("*.example.com", allowWildcard: false));

        Assert.Contains("Wildcard domains are not supported", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyNameIsRefusedEitherWay(string domain)
    {
        Assert.Throws<ArgumentException>(() => ProxyService.Preflight(domain, allowWildcard: false));
        Assert.Throws<ArgumentException>(() => ProxyService.Preflight(domain, allowWildcard: true));
    }

    /// <summary>
    /// Allowing wildcards does not loosen anything else: a name with a star in the
    /// middle is not a wildcard any authority will issue.
    /// </summary>
    [Theory]
    [InlineData("a.*.example.com")]
    [InlineData("*.*.example.com")]
    [InlineData("*example.com")]
    public void AllowingWildcardsDoesNotAllowNonsense(string domain) =>
        Assert.Throws<ArgumentException>(() => ProxyService.Preflight(domain, allowWildcard: true));
}
