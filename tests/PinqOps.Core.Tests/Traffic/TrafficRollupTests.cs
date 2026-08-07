using PinqOps.Traffic;
using Xunit;

namespace PinqOps.Tests.Traffic;

/// <summary>
/// Reading Caddy's access log. The file is appended to while it is read, so the
/// shapes that matter most are the broken ones.
/// </summary>
public class AccessLogTests
{
    /// <summary>One log line carrying a single header, built without a raw literal
    /// so the JSON's braces are not a formatting problem.</summary>
    private static string WithHeader(string header, string value) =>
        "{\"request\":{\"host\":\"a.example.com\",\"uri\":\"/\",\"headers\":{\""
        + header + "\":[\"" + value + "\"]}},\"status\":200}";

    private const string Line = """
        {"ts":1785999600.5,"request":{"host":"acme.example.com","uri":"/orders/1041?ref=x",
        "headers":{"CF-IPCountry":["TR"]}},"status":200,"size":1234,"duration":0.042}
        """;

    [Fact]
    public void AnOrdinaryLineComesApart()
    {
        var entry = AccessLog.Parse(Line)!;

        Assert.Equal("acme.example.com", entry.Host);
        Assert.Equal(200, entry.Status);
        Assert.Equal(1234, entry.Bytes);
        Assert.Equal(0.042, entry.DurationSeconds);
        Assert.Equal("TR", entry.Country);
    }

    [Fact]
    public void TheTimestampIsReadFromUnixSeconds() =>
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1785999600500),
            AccessLog.Parse(Line)!.At);

    /// <summary>The domain is what anyone is asking about; the port is not part of it.</summary>
    [Fact]
    public void ThePortIsNotPartOfTheDomain()
    {
        var entry = AccessLog.Parse("""{"request":{"host":"acme.example.com:8443","uri":"/"},"status":200}""")!;

        Assert.Equal("acme.example.com", entry.Host);
    }

    /// <summary>
    /// The normal state of a file being appended to while it is read. A parser that
    /// threw here would fail every time it ran on a busy server.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"ts\":1785999600.5,\"reque")]
    [InlineData("not json at all")]
    [InlineData("{\"ts\":1}")]
    public void AHalfWrittenOrUnrelatedLineIsSkipped(string line) => Assert.Null(AccessLog.Parse(line));

    // ---- the country, which is only ever what a CDN put there ------------------

    [Theory]
    [InlineData("CF-IPCountry", "de", "DE")]
    [InlineData("CloudFront-Viewer-Country", "GB", "GB")]
    [InlineData("X-Country-Code", "FR", "FR")]
    public void ACdnsCountryHeaderIsRead(string header, string value, string expected)
    {
        var entry = AccessLog.Parse(WithHeader(header, value))!;

        Assert.Equal(expected, entry.Country);
    }

    /// <summary>
    /// pinqops embeds no GeoIP database and makes no per-request lookup, so with no
    /// header there is no country — and the answer is nothing rather than a guess.
    /// </summary>
    [Fact]
    public void WithNoHeaderThereIsNoCountry() =>
        Assert.Null(AccessLog.Parse("""{"request":{"host":"a.example.com","uri":"/"},"status":200}""")!.Country);

    [Theory]
    [InlineData("XX")]
    [InlineData("T1")]
    [InlineData("TURKEY")]
    [InlineData("")]
    public void SomethingThatIsNotACountryCodeIsNotRenderedAsOne(string value)
    {
        var entry = AccessLog.Parse(WithHeader("CF-IPCountry", value))!;

        // "XX" is what a CDN reports for unknown; two letters is the shape, and
        // anything else is not something to draw on a map.
        Assert.True(entry.Country is null or "XX");
    }
}

/// <summary>
/// <c>/orders/1041</c> and <c>/orders/1042</c> are the same route and two different
/// paths. Counting paths gives a top-routes list where every entry has one hit.
/// </summary>
public class RouteKeyTests
{
    [Theory]
    [InlineData("/", "/")]
    [InlineData("", "/")]
    [InlineData("/health", "/health")]
    [InlineData("/API/Health", "/api/health")]
    public void AnOrdinaryPathIsItself(string uri, string expected) =>
        Assert.Equal(expected, RouteKey.Normalize(uri));

    [Theory]
    [InlineData("/orders/1041", "/orders/{id}")]
    [InlineData("/users/3f8a1c2e-0000-4000-8000-000000000000/posts", "/users/{id}/posts")]
    [InlineData("/files/0123456789abcdef01", "/files/{id}")]
    public void AnIdentifierSegmentIsCollapsed(string uri, string expected) =>
        Assert.Equal(expected, RouteKey.Normalize(uri));

    /// <summary>
    /// Deliberately conservative: collapsing a slug would hide a route somebody
    /// wrote, which is worse than a slightly longer list.
    /// </summary>
    [Theory]
    [InlineData("/blog/my-first-post", "/blog/my-first-post")]
    [InlineData("/files/abc123", "/files/abc123")]
    public void ASlugIsLeftAlone(string uri, string expected) =>
        Assert.Equal(expected, RouteKey.Normalize(uri));

    /// <summary>A cache-buster alone would make every request its own route.</summary>
    [Fact]
    public void TheQueryIsDropped() => Assert.Equal("/search", RouteKey.Normalize("/search?q=x&t=1785999600"));

    [Fact]
    public void ADeepPathIsBoundedRatherThanKeptWhole()
    {
        var deep = RouteKey.Normalize("/a/b/c/d/e/f/g/h/i/j/k");

        Assert.EndsWith("/…", deep, StringComparison.Ordinal);
        Assert.Equal(RouteKey.MaximumSegments + 1, deep.Split('/', StringSplitOptions.RemoveEmptyEntries).Length);
    }
}

public class TrafficRollupTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private static AccessEntry Entry(
        string host = "acme.example.com",
        string route = "/",
        int status = 200,
        long bytes = 100,
        double duration = 0.01,
        string? country = null) =>
        new(Now, host, route, status, bytes, duration, country);

    [Fact]
    public void EachDomainIsSummarisedSeparatelyBusiestFirst()
    {
        var summary = TrafficRollup.Summarise([
            Entry(host: "a.example.com"),
            Entry(host: "b.example.com"),
            Entry(host: "b.example.com"),
        ]);

        Assert.Equal(["b.example.com", "a.example.com"], summary.Select(domain => domain.Host));
        Assert.Equal(2, summary[0].Requests);
    }

    [Fact]
    public void ErrorsAndBytesAreCounted()
    {
        var summary = TrafficRollup.Summarise([
            Entry(status: 200, bytes: 100),
            Entry(status: 404, bytes: 50),
            Entry(status: 500, bytes: 10),
        ]);

        Assert.Equal(3, summary[0].Requests);
        Assert.Equal(2, summary[0].Errors);
        Assert.Equal(160, summary[0].Bytes);
    }

    [Theory]
    [InlineData(200, false)]
    [InlineData(301, false)]
    [InlineData(400, true)]
    [InlineData(404, true)]
    [InlineData(503, true)]
    public void AnErrorIsAnythingTheServerOrTheRouteRefused(int status, bool expected) =>
        Assert.Equal(expected, TrafficRollup.IsError(status));

    [Fact]
    public void RoutesAreRankedByHowBusyTheyAre()
    {
        var summary = TrafficRollup.Summarise([
            Entry(route: "/a"), Entry(route: "/a"), Entry(route: "/b"),
        ]);

        Assert.Equal("/a", summary[0].TopRoutes[0].Route);
        Assert.Equal(2, summary[0].TopRoutes[0].Requests);
    }

    [Fact]
    public void TheRouteListIsBounded()
    {
        var many = Enumerable.Range(0, TrafficRollup.TopRouteCount + 20)
            .Select(index => Entry(route: $"/route-{index}"));

        Assert.Equal(TrafficRollup.TopRouteCount, TrafficRollup.Summarise(many)[0].TopRoutes.Count);
    }

    // ---- the percentile -------------------------------------------------------

    /// <summary>
    /// A mean is dominated by the many fast requests and says nothing about the few
    /// slow ones — which is the only part anybody complains about.
    /// </summary>
    [Fact]
    public void TheNinetyFifthIsNotTheMean()
    {
        // Ninety-nine fast, one slow: the mean is about 0.02, the p95 is 0.01, and
        // the point is that neither hides the shape the way the other would.
        var durations = Enumerable.Repeat(0.01, 99).Append(1.0).ToList();

        Assert.Equal(0.01, TrafficRollup.Percentile95(durations));
    }

    [Fact]
    public void ItReturnsADurationThatActuallyHappened()
    {
        // Nearest-rank rather than interpolating, so the answer can be found in the
        // log rather than being a number nothing produced.
        var durations = new List<double> { 0.1, 0.2, 0.3, 0.4, 0.5 };

        Assert.Contains(TrafficRollup.Percentile95(durations), durations);
    }

    [Fact]
    public void OneRequestIsItsOwnPercentile() => Assert.Equal(0.42, TrafficRollup.Percentile95([0.42]));

    [Fact]
    public void NoRequestsIsZeroRatherThanAThrow() => Assert.Equal(0, TrafficRollup.Percentile95([]));

    // ---- countries ------------------------------------------------------------

    [Fact]
    public void CountriesAreCountedWhenTheyAreThere()
    {
        var summary = TrafficRollup.Summarise([
            Entry(country: "TR"), Entry(country: "TR"), Entry(country: "DE"),
        ]);

        Assert.True(summary[0].HasCountries);
        Assert.Equal("TR", summary[0].Countries[0].Country);
        Assert.Equal(2, summary[0].Countries[0].Requests);
    }

    /// <summary>
    /// An empty column because the header is absent means something different from
    /// an empty column because there was no traffic, and the page needs to tell them
    /// apart.
    /// </summary>
    [Fact]
    public void WithNoCountryHeaderTheColumnIsMarkedAbsentRatherThanEmpty()
    {
        var summary = TrafficRollup.Summarise([Entry(), Entry()]);

        Assert.False(summary[0].HasCountries);
        Assert.Empty(summary[0].Countries);
    }

    [Fact]
    public void PartialCountryDataStillCounts()
    {
        // Behind a CDN that only sets the header sometimes, what is there is worth
        // showing — with the count making it obvious it is not the whole picture.
        var summary = TrafficRollup.Summarise([Entry(country: "TR"), Entry()]);

        Assert.True(summary[0].HasCountries);
        Assert.Equal(1, summary[0].Countries.Sum(country => country.Requests));
        Assert.Equal(2, summary[0].Requests);
    }
}
