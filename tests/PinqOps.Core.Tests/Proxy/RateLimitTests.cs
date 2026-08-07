using PinqOps.Proxy;
using Xunit;

namespace PinqOps.Tests.Proxy;

public class RateLimitTests
{
    private static CaddyfileRender Render(RateLimit limit, string domain = "app.example.com")
    {
        var entry = new DomainEntry
        {
            Domain = domain,
            TargetContainer = "app-1",
            TargetPort = 3000,
            Enabled = true,
            RateLimit = limit,
        };

        return CaddyfileGenerator.GenerateWithDiagnostics(new DomainConfig { Domains = [entry] });
    }

    private static RateLimit PerAddress(int burst = 0, int sustained = 0) => new()
    {
        Enabled = true,
        Key = RateLimitKeys.ClientAddress,
        BurstRequests = burst,
        SustainedRequests = sustained,
    };

    /// <summary>
    /// Every zone identifier the file declares, in order. Read out of the rendered
    /// Caddyfile rather than recomputed, because the property under test is that two
    /// site blocks in one file never name the same limiter — which is a fact about
    /// the file, not about the naming function.
    /// </summary>
    private static IReadOnlyList<string> ZoneNamesIn(string caddyfile) =>
    [
        .. caddyfile
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("zone ", StringComparison.Ordinal))
            .Select(line => line[5..].TrimEnd('{', ' ')),
    ];

    // ---- off by default -----------------------------------------------------

    /// <summary>
    /// A ceiling that fires on a legitimate user is worse than no ceiling, and
    /// there is no number pinqops could guess for an app whose traffic it has never
    /// seen.
    /// </summary>
    [Fact]
    public void ADomainWithNoLimitRendersNoRateLimitBlock()
    {
        var caddyfile = CaddyfileGenerator.Generate(new DomainConfig
        {
            Domains = [new DomainEntry { Domain = "app.example.com", TargetContainer = "app-1", TargetPort = 3000 }],
        });

        Assert.DoesNotContain("rate_limit", caddyfile, StringComparison.Ordinal);
    }

    [Fact]
    public void ADisabledLimitRendersNothing()
    {
        var render = Render(new RateLimit { Enabled = false, BurstRequests = 10 });

        Assert.DoesNotContain("rate_limit", render.Caddyfile, StringComparison.Ordinal);
        Assert.Empty(render.Skipped);
    }

    // ---- the two windows ----------------------------------------------------

    [Fact]
    public void ABurstWindowRendersItsOwnZone()
    {
        var caddyfile = Render(PerAddress(burst: 20)).Caddyfile;

        Assert.Contains("rate_limit {", caddyfile, StringComparison.Ordinal);
        Assert.Contains($"zone {RateLimitRenderer.ZoneName("app.example.com", "burst")} {{", caddyfile, StringComparison.Ordinal);
        Assert.Contains("key {http.request.client_ip}", caddyfile, StringComparison.Ordinal);
        Assert.Contains("events 20", caddyfile, StringComparison.Ordinal);
        Assert.Contains("window 1s", caddyfile, StringComparison.Ordinal);
    }

    /// <summary>
    /// The pair is the point: one window has to choose between stopping a burst and
    /// stopping a grind, and cannot do both.
    /// </summary>
    [Fact]
    public void BothWindowsCanBeSetAtOnce()
    {
        var limit = PerAddress(burst: 20, sustained: 600);
        limit.SustainedWindowSeconds = 60;

        var caddyfile = Render(limit).Caddyfile;

        Assert.Contains($"zone {RateLimitRenderer.ZoneName("app.example.com", "burst")} {{", caddyfile, StringComparison.Ordinal);
        Assert.Contains($"zone {RateLimitRenderer.ZoneName("app.example.com", "sustained")} {{", caddyfile, StringComparison.Ordinal);
        Assert.Contains("events 600", caddyfile, StringComparison.Ordinal);
        Assert.Contains("window 60s", caddyfile, StringComparison.Ordinal);
    }

    [Fact]
    public void AWindowWithNoRequestCountIsLeftOut()
    {
        var caddyfile = Render(PerAddress(sustained: 600)).Caddyfile;

        Assert.DoesNotContain("_burst", caddyfile, StringComparison.Ordinal);
        Assert.Contains("_sustained", caddyfile, StringComparison.Ordinal);
    }

    /// <summary>
    /// Switched on with neither window filled in is a half-completed form. An empty
    /// block would look enforced and enforce nothing, so it is refused and said so.
    /// </summary>
    [Fact]
    public void EnabledWithNoWindowsIsRefusedAndReported()
    {
        var render = Render(PerAddress());

        Assert.DoesNotContain("rate_limit", render.Caddyfile, StringComparison.Ordinal);
        Assert.Single(render.Skipped);
    }

    // ---- what it counts by --------------------------------------------------

    [Fact]
    public void ItCanCountByAHeaderInsteadOfAnAddress()
    {
        var limit = PerAddress(sustained: 100);
        limit.Key = RateLimitKeys.Header;
        limit.HeaderName = "X-Api-Key";

        Assert.Contains(
            "key {http.request.header.X-Api-Key}", Render(limit).Caddyfile, StringComparison.Ordinal);
    }

    /// <summary>
    /// The header name goes inside a Caddy placeholder, so it is checked more
    /// narrowly than the HTTP grammar allows — the punctuation a token may legally
    /// contain is exactly what would break out of one.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("X-Api Key")]
    [InlineData("X-Api}{Key")]
    [InlineData("X.Api.Key")]
    public void AHeaderNameThatCouldBreakOutIsRefused(string headerName)
    {
        var limit = PerAddress(sustained: 100);
        limit.Key = RateLimitKeys.Header;
        limit.HeaderName = headerName;

        var render = Render(limit);

        Assert.DoesNotContain("rate_limit", render.Caddyfile, StringComparison.Ordinal);
        Assert.Single(render.Skipped);
    }

    [Fact]
    public void AnUnknownKeyIsRefused()
    {
        var limit = PerAddress(sustained: 100);
        limit.Key = "byPhaseOfTheMoon";

        Assert.Single(Render(limit).Skipped);
    }

    /// <summary>
    /// Behind a CDN every request arrives from the CDN, so a per-address limit is
    /// only a per-address limit if the address it counts is the forwarded one.
    /// <c>{http.request.remote.host}</c> is the immediate TCP peer and
    /// <c>trusted_proxies</c> does not touch it, so every visitor through one edge
    /// node shares a bucket and ordinary traffic is refused before the app hears
    /// about it — the exact failure edge mode was added to fix. Only
    /// <c>{http.request.client_ip}</c> resolves through the trusted networks.
    /// </summary>
    [Fact]
    public void BehindAnEdgeTheLimitCountsTheVisitorNotTheEdgeNode()
    {
        var config = new DomainConfig
        {
            Edge = new EdgeMode { Enabled = true, TrustedRanges = ["173.245.48.0/20"] },
            Domains =
            [
                new DomainEntry
                {
                    Domain = "app.example.com", TargetContainer = "app-1", TargetPort = 3000,
                    RateLimit = PerAddress(burst: 20),
                },
            ],
        };

        var caddyfile = CaddyfileGenerator.Generate(config);

        Assert.Contains("trusted_proxies static 173.245.48.0/20", caddyfile, StringComparison.Ordinal);
        Assert.Contains("key {http.request.client_ip}", caddyfile, StringComparison.Ordinal);
        Assert.DoesNotContain("remote.host", caddyfile, StringComparison.Ordinal);
    }

    /// <summary>
    /// With no trusted networks configured Caddy resolves <c>client_ip</c> to the
    /// remote address, so an install with no CDN in front of it counts exactly what
    /// it counted before.
    /// </summary>
    [Fact]
    public void WithNoEdgeTheSameKeyStillCountsTheRemoteAddress()
    {
        var caddyfile = Render(PerAddress(burst: 20)).Caddyfile;

        Assert.DoesNotContain("trusted_proxies", caddyfile, StringComparison.Ordinal);
        Assert.Contains("key {http.request.client_ip}", caddyfile, StringComparison.Ordinal);
    }

    // ---- the zone name ------------------------------------------------------

    /// <summary>
    /// Derived from the domain and never taken from the caller: a zone name is a
    /// Caddyfile identifier that has to be unique across the whole file, so a
    /// caller-supplied one is both an injection surface and a way to make two
    /// domains silently share a bucket.
    /// </summary>
    /// <summary>
    /// Still readable at a glance and still fixed for a given domain, so the same
    /// config regenerates the same file on every run — the digest is appended, not
    /// substituted for the sanitized name.
    /// </summary>
    [Theory]
    [InlineData("app.example.com", "rl_app_example_com_28059829_burst")]
    [InlineData("APP.Example.COM", "rl_app_example_com_28059829_burst")]
    [InlineData("pr-8.shop.example.com", "rl_pr_8_shop_example_com_4a98696d_burst")]
    public void TheZoneNameIsDerivedFromTheDomain(string domain, string expected)
    {
        Assert.Equal(expected, RateLimitRenderer.ZoneName(domain, "burst"));
    }

    [Fact]
    public void TwoDomainsGetDifferentZones()
    {
        var caddyfile = CaddyfileGenerator.Generate(TwoLimitedDomains("one.example.com", "two.example.com"));

        Assert.Equal(2, ZoneNamesIn(caddyfile).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Mapping every non-alphanumeric character to '_' is not injective:
    /// <c>api-staging.example.com</c> and <c>api.staging.example.com</c> sanitize to
    /// the same identifier. Both spellings pass <c>DomainName.Normalize</c>, both
    /// site blocks land in one Caddyfile, and caddy-ratelimit keys its shared
    /// limiter state by zone name — so traffic to the staging host would spend the
    /// production host's allowance and 429 its visitors. Nothing downstream can
    /// notice: no renderer here sees more than one domain at a time.
    /// </summary>
    [Theory]
    [InlineData("api-staging.example.com", "api.staging.example.com")]
    [InlineData("my-app.example.com", "my.app.example.com")]
    public void TwoDomainsThatSanitizeAlikeStillGetDifferentZones(string first, string second)
    {
        Assert.NotEqual(RateLimitRenderer.ZoneName(first, "burst"), RateLimitRenderer.ZoneName(second, "burst"));

        var zones = ZoneNamesIn(CaddyfileGenerator.Generate(TwoLimitedDomains(first, second)));

        Assert.Equal(2, zones.Count);
        Assert.Equal(2, zones.Distinct(StringComparer.Ordinal).Count());
    }

    private static DomainConfig TwoLimitedDomains(string first, string second) => new()
    {
        Domains =
        [
            new DomainEntry
            {
                Domain = first, TargetContainer = "a", TargetPort = 80, RateLimit = PerAddress(burst: 5),
            },
            new DomainEntry
            {
                Domain = second, TargetContainer = "b", TargetPort = 80, RateLimit = PerAddress(burst: 5),
            },
        ],
    };

    // ---- clamping -----------------------------------------------------------

    /// <summary>
    /// Clamped rather than refused: a number too large is a typo, and enforcing no
    /// limit at all because of one is the worse failure.
    /// </summary>
    [Fact]
    public void AnAbsurdRequestCountIsClamped()
    {
        var caddyfile = Render(PerAddress(burst: int.MaxValue)).Caddyfile;

        Assert.Contains($"events {RateLimitRenderer.MaximumRequests}", caddyfile, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(99_999, RateLimitRenderer.MaximumWindowSeconds)]
    public void AnOutOfRangeWindowIsClamped(int configured, int expected)
    {
        var limit = PerAddress(burst: 5);
        limit.BurstWindowSeconds = configured;

        Assert.Contains($"window {expected}s", Render(limit).Caddyfile, StringComparison.Ordinal);
    }

    // ---- placement ----------------------------------------------------------

    /// <summary>
    /// Before the proxy line, so a refused request is refused without the upstream
    /// ever hearing about it — which is the whole point of a ceiling.
    /// </summary>
    [Fact]
    public void TheLimitIsAppliedBeforeTheRequestReachesTheApp()
    {
        var caddyfile = Render(PerAddress(burst: 5)).Caddyfile;

        Assert.True(
            caddyfile.IndexOf("rate_limit", StringComparison.Ordinal)
            < caddyfile.IndexOf("reverse_proxy", StringComparison.Ordinal));
    }

    [Fact]
    public void ARefusedLimitDoesNotTakeTheRouteWithIt()
    {
        var limit = PerAddress(sustained: 100);
        limit.Key = RateLimitKeys.Header;
        limit.HeaderName = "bad name";

        var render = Render(limit);

        Assert.Contains("app.example.com {", render.Caddyfile, StringComparison.Ordinal);
        Assert.Contains("reverse_proxy app-1:3000", render.Caddyfile, StringComparison.Ordinal);
    }
}
