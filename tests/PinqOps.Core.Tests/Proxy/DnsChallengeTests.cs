using PinqOps.Proxy;
using Xunit;

namespace PinqOps.Tests.Proxy;

public class DnsChallengeTests
{
    private static DnsChallenge Configured() => new()
    {
        Enabled = true,
        Provider = DnsProviders.Cloudflare,
        SecretName = "CLOUDFLARE_TOKEN",
    };

    private static DomainConfig Config(DnsChallenge? dns, params string[] domains) => new()
    {
        Dns = dns,
        Domains =
        [
            .. domains.Select(domain => new DomainEntry
            {
                Domain = domain,
                TargetContainer = "app-1",
                TargetPort = 3000,
                Enabled = true,
                Security = new SecurityHeaders { Enabled = false },
            }),
        ],
    };

    // ---- when it is usable --------------------------------------------------

    [Fact]
    public void ItIsNotUsableUntilEverythingIsThere()
    {
        Assert.False(new DnsChallenge().IsUsable());
        Assert.False(new DnsChallenge { Enabled = true }.IsUsable());
        Assert.False(new DnsChallenge { Enabled = true, Provider = "cloudflare" }.IsUsable());
        Assert.False(new DnsChallenge { Enabled = false, Provider = "cloudflare", SecretName = "x" }.IsUsable());
        Assert.False(new DnsChallenge { Enabled = true, Provider = "made-up", SecretName = "x" }.IsUsable());
        Assert.True(Configured().IsUsable());
    }

    // ---- wildcards ----------------------------------------------------------

    /// <summary>
    /// The refusal that was there before is not a limitation to work around: HTTP-01
    /// proves control by serving a file at the name, and there is no host at
    /// <c>*.example.com</c> to serve one from. Without DNS-01 the message and the
    /// behaviour are exactly what they always were.
    /// </summary>
    [Fact]
    public void WithoutADnsProviderAWildcardIsStillRefused()
    {
        var exception = Assert.Throws<ArgumentException>(() => DomainName.Normalize("*.example.com"));

        Assert.Contains("HTTP-01", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithADnsProviderAWildcardIsAccepted()
    {
        Assert.Equal("*.example.com", DomainName.Normalize("*.example.com", allowWildcard: true));
    }

    /// <summary>Only a single leading label. The rest are names no certificate
    /// authority will issue, so taking them would just defer the failure.</summary>
    [Theory]
    [InlineData("*.*.example.com")]
    [InlineData("a.*.example.com")]
    [InlineData("*example.com")]
    [InlineData("*.")]
    [InlineData("*")]
    public void AMalformedWildcardIsRefusedEvenWithDns(string domain)
    {
        Assert.Throws<ArgumentException>(() => DomainName.Normalize(domain, allowWildcard: true));
    }

    [Theory]
    [InlineData("*.example.com", true)]
    [InlineData("app.example.com", false)]
    [InlineData("", false)]
    public void WildcardDetection(string domain, bool expected)
    {
        Assert.Equal(expected, DomainName.IsWildcard(domain));
    }

    [Fact]
    public void AWildcardEntryIsSkippedWhileNoProviderIsConfigured()
    {
        var render = CaddyfileGenerator.GenerateWithDiagnostics(Config(null, "*.example.com"));

        Assert.DoesNotContain("*.example.com", render.Caddyfile, StringComparison.Ordinal);
        Assert.Single(render.Skipped);
    }

    // ---- what is emitted ----------------------------------------------------

    [Fact]
    public void AWildcardGetsADnsChallengeBlock()
    {
        var caddyfile = CaddyfileGenerator.Generate(Config(Configured(), "*.example.com"));

        Assert.Contains("*.example.com {", caddyfile, StringComparison.Ordinal);
        Assert.Contains("tls {", caddyfile, StringComparison.Ordinal);
        Assert.Contains("dns cloudflare {env.PINQOPS_DNS_API_TOKEN}", caddyfile, StringComparison.Ordinal);
    }

    /// <summary>
    /// An ordinary domain keeps proving itself over HTTP-01, which needs no
    /// credential and no zone access. Moving every domain onto the DNS challenge
    /// would make one wrong token break certificates that were working.
    /// </summary>
    [Fact]
    public void AnOrdinaryDomainIsNotMovedOntoTheDnsChallenge()
    {
        var caddyfile = CaddyfileGenerator.Generate(Config(Configured(), "app.example.com"));

        Assert.Contains("app.example.com {", caddyfile, StringComparison.Ordinal);
        Assert.DoesNotContain("tls {", caddyfile, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheWildcardInAMixedConfigGetsIt()
    {
        var caddyfile = CaddyfileGenerator.Generate(Config(Configured(), "app.example.com", "*.example.com"));

        Assert.Single(
            caddyfile.Split("tls {", StringSplitOptions.None).Skip(1));
    }

    /// <summary>
    /// The token is referenced through the container's environment and never
    /// written here: the Caddyfile is regenerated constantly and sits beside a
    /// config two processes write, which is not where a credential that can edit an
    /// entire DNS zone belongs.
    /// </summary>
    [Fact]
    public void TheTokenItselfNeverReachesTheCaddyfile()
    {
        var dns = Configured();
        var caddyfile = CaddyfileGenerator.Generate(Config(dns, "*.example.com"));

        // Only the secret's name could possibly be here, and not even that.
        Assert.DoesNotContain(dns.SecretName, caddyfile, StringComparison.Ordinal);
        Assert.Contains("{env.", caddyfile, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryDeclaredProviderIsKnown()
    {
        Assert.All(DnsProviders.All, provider => Assert.True(DnsProviders.IsKnown(provider)));
        Assert.False(DnsProviders.IsKnown("gandi"));
        Assert.False(DnsProviders.IsKnown(null));
    }

    /// <summary>A provider name is never taken from the caller into the file — it
    /// has to be one of the modules compiled into the image.</summary>
    [Fact]
    public void AnUnknownProviderMakesTheChallengeUnusableRatherThanBeingEmitted()
    {
        var dns = new DnsChallenge { Enabled = true, Provider = "evil }\nother {", SecretName = "x" };

        var render = CaddyfileGenerator.GenerateWithDiagnostics(Config(dns, "*.example.com"));

        Assert.DoesNotContain("evil", render.Caddyfile, StringComparison.Ordinal);
        Assert.Single(render.Skipped);
    }
}
