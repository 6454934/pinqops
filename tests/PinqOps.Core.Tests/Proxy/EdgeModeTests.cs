using PinqOps.Proxy;
using Xunit;

namespace PinqOps.Tests.Proxy;

public class EdgeModeTests
{
    private static readonly string[] CloudflareSample = ["173.245.48.0/20", "103.21.244.0/22", "2400:cb00::/32"];

    private static CaddyfileRender Render(EdgeMode? edge) =>
        CaddyfileGenerator.GenerateWithDiagnostics(new DomainConfig
        {
            Edge = edge,
            Domains =
            [
                new DomainEntry
                {
                    Domain = "app.example.com",
                    TargetContainer = "app-1",
                    TargetPort = 3000,
                    Enabled = true,
                    Security = new SecurityHeaders { Enabled = false },
                },
            ],
        });

    private static EdgeMode Enabled(int cacheSeconds = 0) => new()
    {
        Enabled = true,
        TrustedRanges = [.. CloudflareSample],
        StaticCacheSeconds = cacheSeconds,
    };

    // ---- off by default -----------------------------------------------------

    [Fact]
    public void WithNoEdgeModeNothingIsEmitted()
    {
        var caddyfile = Render(null).Caddyfile;

        Assert.DoesNotContain("trusted_proxies", caddyfile, StringComparison.Ordinal);
        Assert.DoesNotContain("Cache-Control", caddyfile, StringComparison.Ordinal);
    }

    [Fact]
    public void ADisabledEdgeModeEmitsNothing()
    {
        Assert.DoesNotContain(
            "trusted_proxies",
            Render(new EdgeMode { Enabled = false, TrustedRanges = [.. CloudflareSample] }).Caddyfile,
            StringComparison.Ordinal);
    }

    // ---- trusted proxies ----------------------------------------------------

    /// <summary>
    /// The correctness-critical part. Behind a proxy every request arrives from the
    /// proxy's address, so without this a rate limit buckets the whole internet
    /// together and any country header could have been written by anyone.
    /// </summary>
    [Fact]
    public void TheTrustedNetworksReachTheGlobalBlock()
    {
        var caddyfile = Render(Enabled()).Caddyfile;

        Assert.Contains("servers {", caddyfile, StringComparison.Ordinal);
        Assert.Contains(
            "trusted_proxies static 173.245.48.0/20 103.21.244.0/22 2400:cb00::/32",
            caddyfile,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A malformed entry makes Caddy refuse the whole file, which would take every
    /// domain down — so it is dropped and reported rather than emitted.
    /// </summary>
    [Theory]
    [InlineData("not-a-network")]
    // A bare address with no prefix length is not a network, however valid it looks.
    [InlineData("192.0.2.1")]
    [InlineData("192.0.2.0/999")]
    [InlineData("}\nevil {")]
    public void SomethingThatIsNotANetworkIsDroppedAndReported(string range)
    {
        var edge = Enabled();
        edge.TrustedRanges.Add(range);

        var render = Render(edge);

        Assert.DoesNotContain(range, render.Caddyfile, StringComparison.Ordinal);
        Assert.Single(render.Skipped);
        // The valid ones still make it — one bad line does not cost the trust list.
        Assert.Contains("173.245.48.0/20", render.Caddyfile, StringComparison.Ordinal);
    }

    [Fact]
    public void ADuplicateRangeIsTrustedOnce()
    {
        var edge = Enabled();
        edge.TrustedRanges.Add("173.245.48.0/20");

        var caddyfile = Render(edge).Caddyfile;

        Assert.Equal(2, caddyfile.Split("173.245.48.0/20", StringSplitOptions.None).Length - 1 + 1);
    }

    /// <summary>A list far longer than any CDN publishes is a fetch that went wrong,
    /// and putting it in the file would trust networks nobody chose.</summary>
    [Fact]
    public void AnAbsurdlyLongListIsCutAndReported()
    {
        var edge = new EdgeMode { Enabled = true };
        for (var index = 0; index < EdgeModeRenderer.MaximumRanges + 10; index++)
        {
            edge.TrustedRanges.Add($"10.{index / 256}.{index % 256}.0/24");
        }

        var render = Render(edge);

        Assert.Contains(render.Skipped, skip => skip.Reason.Contains("only the first", StringComparison.Ordinal));
    }

    // ---- static cache -------------------------------------------------------

    /// <summary>
    /// No lifetime by default, which leaves the app's own headers alone — pinqops
    /// does not know which of an app's paths are genuinely immutable.
    /// </summary>
    [Fact]
    public void NoCacheLifetimeMeansNoCacheHeader()
    {
        Assert.DoesNotContain("Cache-Control", Render(Enabled()).Caddyfile, StringComparison.Ordinal);
    }

    [Fact]
    public void AStaticCacheLifetimeIsAppliedToAssetsOnly()
    {
        var caddyfile = Render(Enabled(cacheSeconds: 86400)).Caddyfile;

        Assert.Contains("@pinqops_static path *.css *.js", caddyfile, StringComparison.Ordinal);
        Assert.Contains(
            "header @pinqops_static Cache-Control \"public, max-age=86400\"", caddyfile, StringComparison.Ordinal);
    }

    /// <summary>
    /// A fixed list rather than a pattern the operator writes: a matcher that
    /// accidentally covers an HTML page caches a logged-in view at the edge.
    /// </summary>
    [Fact]
    public void TheStaticMatcherNeverCoversPages()
    {
        var caddyfile = Render(Enabled(cacheSeconds: 86400)).Caddyfile;

        Assert.DoesNotContain("*.html", caddyfile, StringComparison.Ordinal);
        Assert.DoesNotContain("*.json", caddyfile, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsurdCacheLifetimeIsClamped()
    {
        var caddyfile = Render(Enabled(cacheSeconds: int.MaxValue)).Caddyfile;

        Assert.Contains($"max-age={EdgeModeRenderer.MaximumStaticCacheSeconds}", caddyfile, StringComparison.Ordinal);
    }

    /// <summary>Edge mode composes with everything else on the site rather than
    /// replacing any of it.</summary>
    [Fact]
    public void ItComposesWithTheRestOfTheSiteBlock()
    {
        var config = new DomainConfig
        {
            Edge = Enabled(cacheSeconds: 3600),
            Domains =
            [
                new DomainEntry
                {
                    Domain = "app.example.com",
                    TargetContainer = "app-1",
                    TargetPort = 3000,
                    Enabled = true,
                    RateLimit = new RateLimit { Enabled = true, BurstRequests = 20 },
                },
            ],
        };

        var caddyfile = CaddyfileGenerator.Generate(config);

        Assert.Contains("trusted_proxies static", caddyfile, StringComparison.Ordinal);
        Assert.Contains("@pinqops_static", caddyfile, StringComparison.Ordinal);
        Assert.Contains("rate_limit {", caddyfile, StringComparison.Ordinal);
        Assert.Contains("X-Content-Type-Options nosniff", caddyfile, StringComparison.Ordinal);
        Assert.Contains("reverse_proxy app-1:3000", caddyfile, StringComparison.Ordinal);
    }
}
