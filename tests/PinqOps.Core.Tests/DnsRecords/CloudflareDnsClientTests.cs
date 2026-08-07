using System.Net;
using PinqOps.DnsRecords;
using Xunit;

namespace PinqOps.Tests.DnsRecords;

/// <summary>
/// Answers Cloudflare's API by request shape rather than by call order, because the
/// client legitimately makes a different number of zone lookups depending on how
/// deep the name is.
/// </summary>
internal sealed class FakeCloudflare : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> _answer;

    public FakeCloudflare(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> answer) => _answer = answer;

    public List<(string Method, string Uri, string? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method.Method, request.RequestUri!.ToString(), body));

        var (status, responseBody) = _answer(request);
        return new HttpResponseMessage(status) { Content = new StringContent(responseBody) };
    }
}

public class CloudflareDnsClientTests
{
    private const string ZoneId = "zone-123";
    private const string RecordId = "rec-456";

    private static string Envelope(string result) =>
        "{\"success\":true,\"errors\":[],\"result\":" + result + "}";

    private static string Zones(params string[] names) =>
        Envelope("[" + string.Join(
            ",", names.Select(name => "{\"id\":\"" + ZoneId + "\",\"name\":\"" + name + "\"}")) + "]");

    private static string NoZones() => Envelope("[]");

    private static string RecordObject(string address) =>
        "{\"id\":\"" + RecordId + "\",\"name\":\"app.example.com\",\"content\":\"" + address
        + "\",\"zone_id\":\"" + ZoneId + "\"}";

    private static string Records(params string[] addresses) =>
        Envelope("[" + string.Join(",", addresses.Select(RecordObject)) + "]");

    private static string Written(string address) => Envelope(RecordObject(address));

    private static (CloudflareDnsClient Client, FakeCloudflare Handler) Build(
        Func<HttpRequestMessage, (HttpStatusCode, string)> answer,
        string? zoneId = null,
        string? accountId = null)
    {
        var handler = new FakeCloudflare(answer);
        return (new CloudflareDnsClient(new HttpClient(handler), "cf-token", zoneId: zoneId, accountId: accountId), handler);
    }

    private static bool IsZoneLookup(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.EndsWith("/zones", StringComparison.Ordinal);

    private static bool IsRecordLookup(HttpRequestMessage request) =>
        request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.Contains("dns_records", StringComparison.Ordinal);

    // ---- finding the zone ---------------------------------------------------

    /// <summary>
    /// Cloudflare identifies a zone by its registrable name, and the domain being
    /// routed is usually a subdomain of one — so the name is walked from the left
    /// until a zone matches.
    /// </summary>
    [Fact]
    public async Task ItWalksUpToTheRegistrableNameToFindTheZone()
    {
        var (client, handler) = Build(request =>
        {
            if (IsZoneLookup(request))
            {
                var wanted = request.RequestUri!.Query.Contains("example.com", StringComparison.Ordinal)
                    && !request.RequestUri.Query.Contains("app.example.com", StringComparison.Ordinal);
                return (HttpStatusCode.OK, wanted ? Zones("example.com") : NoZones());
            }

            return (HttpStatusCode.OK, Records());
        });

        await client.Find("app.example.com");

        var lookups = handler.Requests.Where(r => r.Uri.Contains("/zones?", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, lookups.Count);
        Assert.Contains("name=app.example.com", lookups[0].Uri, StringComparison.Ordinal);
        Assert.Contains("name=example.com", lookups[1].Uri, StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole name is asked for first, so an account that really does hold
    /// <c>app.example.com</c> as its own zone is found before its parent.
    /// </summary>
    [Fact]
    public async Task AZoneThatIsTheWholeNameIsFoundFirst()
    {
        var (client, handler) = Build(request => IsZoneLookup(request)
            ? (HttpStatusCode.OK, Zones("app.example.com"))
            : (HttpStatusCode.OK, Records()));

        await client.Find("app.example.com");

        Assert.Single(handler.Requests, r => r.Uri.Contains("/zones?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADomainOnNoZoneSaysSo()
    {
        var (client, _) = Build(_ => (HttpStatusCode.OK, NoZones()));

        var exception = await Assert.ThrowsAsync<DnsProviderException>(() => client.Find("app.example.com"));

        Assert.Contains("No Cloudflare zone covers", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A configured zone id skips the name walk — operators paste it when
    /// <c>GET /zones</c> is slow or times out. DNS Edit on the token is still required.
    /// </summary>
    [Fact]
    public async Task AConfiguredZoneIdSkipsTheNameWalk()
    {
        var (client, handler) = Build(
            request => IsRecordLookup(request)
                ? (HttpStatusCode.OK, Records())
                : (HttpStatusCode.OK, NoZones()),
            zoneId: ZoneId);

        await client.Find("app.example.com");

        Assert.DoesNotContain(handler.Requests, r => r.Uri.Contains("/zones?", StringComparison.Ordinal));
        Assert.Contains(
            handler.Requests,
            r => r.Uri.Contains($"zones/{ZoneId}/dns_records", StringComparison.Ordinal));
    }

    /// <summary>
    /// Account id narrows the zone search for multi-account tokens.
    /// </summary>
    [Fact]
    public async Task AnAccountIdIsSentOnZoneLookups()
    {
        var (client, handler) = Build(
            request => IsZoneLookup(request)
                ? (HttpStatusCode.OK, Zones("example.com"))
                : (HttpStatusCode.OK, Records()),
            accountId: "acct-99");

        await client.Find("app.example.com");

        var lookups = handler.Requests.Where(r => r.Uri.Contains("/zones?", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(lookups);
        Assert.All(lookups, r => Assert.Contains("account.id=acct-99", r.Uri, StringComparison.Ordinal));
    }

    // ---- writing the record -------------------------------------------------

    [Fact]
    public async Task ANameWithNoRecordGetsOneCreated()
    {
        var (client, handler) = Build(request =>
        {
            if (IsZoneLookup(request))
            {
                return (HttpStatusCode.OK, Zones("example.com"));
            }

            return IsRecordLookup(request)
                ? (HttpStatusCode.OK, Records())
                : (HttpStatusCode.OK, Written("203.0.113.7"));
        });

        var record = await client.Point("app.example.com", "203.0.113.7");

        var write = handler.Requests.Single(r => r.Method is "POST" or "PUT");
        Assert.Equal("POST", write.Method);
        Assert.Contains("\"type\":\"A\"", write.Body, StringComparison.Ordinal);
        Assert.Contains("203.0.113.7", write.Body, StringComparison.Ordinal);
        Assert.Equal("203.0.113.7", record.Address);
    }

    /// <summary>
    /// Replaced rather than added: two A records for one name is a round-robin
    /// nobody asked for, and half the requests would land nowhere.
    /// </summary>
    [Fact]
    public async Task AnExistingRecordIsReplacedNotDuplicated()
    {
        var (client, handler) = Build(request =>
        {
            if (IsZoneLookup(request))
            {
                return (HttpStatusCode.OK, Zones("example.com"));
            }

            return IsRecordLookup(request)
                ? (HttpStatusCode.OK, Records("198.51.100.4"))
                : (HttpStatusCode.OK, Written("203.0.113.7"));
        });

        await client.Point("app.example.com", "203.0.113.7");

        var write = handler.Requests.Single(r => r.Method is "POST" or "PUT");
        Assert.Equal("PUT", write.Method);
        Assert.Contains($"zones/{ZoneId}/dns_records/{RecordId}", write.Uri, StringComparison.Ordinal);
        // Composite zone/record ids must not be pasted into the path — that is
        // Cloudflare's 405 method_not_allowed (code 1001).
        Assert.DoesNotContain($"dns_records/{ZoneId}/{RecordId}", write.Uri, StringComparison.Ordinal);
    }

    /// <summary>
    /// Orange cloud is the default: that is what operators mean by pointing a name
    /// through Cloudflare. DNS-only remains available as an explicit choice.
    /// </summary>
    [Fact]
    public async Task TheRecordIsProxiedThroughCloudflareByDefault()
    {
        var (client, handler) = Build(request => IsZoneLookup(request)
            ? (HttpStatusCode.OK, Zones("example.com"))
            : IsRecordLookup(request) ? (HttpStatusCode.OK, Records()) : (HttpStatusCode.OK, Written("203.0.113.7")));

        await client.Point("app.example.com", "203.0.113.7");

        Assert.Contains(
            "\"proxied\":true", handler.Requests.Single(r => r.Method == "POST").Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADnsOnlyRecordCanBeRequested()
    {
        var (client, handler) = Build(request => IsZoneLookup(request)
            ? (HttpStatusCode.OK, Zones("example.com"))
            : IsRecordLookup(request) ? (HttpStatusCode.OK, Records()) : (HttpStatusCode.OK, Written("203.0.113.7")));

        await client.Point("app.example.com", "203.0.113.7", proxied: false);

        Assert.Contains(
            "\"proxied\":false", handler.Requests.Single(r => r.Method == "POST").Body, StringComparison.Ordinal);
    }

    // ---- the token ----------------------------------------------------------

    /// <summary>
    /// On each request rather than on the client's default headers, so one shared
    /// HttpClient can serve callers holding different tokens.
    /// </summary>
    [Fact]
    public async Task TheTokenRidesEveryRequest()
    {
        var handler = new FakeCloudflare(_ => (HttpStatusCode.OK, NoZones()));
        using var http = new HttpClient(handler);
        var client = new CloudflareDnsClient(http, "cf-token");

        await Assert.ThrowsAsync<DnsProviderException>(() => client.Find("app.example.com"));

        Assert.Null(http.DefaultRequestHeaders.Authorization);
    }

    // ---- failures -----------------------------------------------------------

    /// <summary>
    /// Cloudflare's own wording names the actual problem — a token without
    /// Zone:Edit, a zone on another account. Paraphrasing it is how someone ends up
    /// guessing.
    /// </summary>
    [Fact]
    public async Task CloudflaresOwnMessageIsQuotedBack()
    {
        var (client, _) = Build(_ => (
            HttpStatusCode.Forbidden,
            """{"success":false,"errors":[{"code":10000,"message":"Authentication error"}]}"""));

        var exception = await Assert.ThrowsAsync<DnsProviderException>(() => client.Find("app.example.com"));

        Assert.Contains("Authentication error", exception.Message, StringComparison.Ordinal);
        Assert.Contains("code 10000", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Zone DNS Edit", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Global API Key", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Zone ID alone", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Cloudflare answers 200 with <c>success: false</c> for some
    /// failures, so the status code alone is not the check.</summary>
    [Fact]
    public async Task ATwoHundredThatSaysItFailedIsAFailure()
    {
        var (client, _) = Build(_ => (
            HttpStatusCode.OK,
            """{"success":false,"errors":[{"code":81044,"message":"Record already exists"}]}"""));

        var exception = await Assert.ThrowsAsync<DnsProviderException>(() => client.Find("app.example.com"));

        Assert.Contains("Record already exists", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SomethingThatIsNotJsonIsReportedAsSuch()
    {
        var (client, _) = Build(_ => (HttpStatusCode.BadGateway, "<html>gateway error</html>"));

        var exception = await Assert.ThrowsAsync<DnsProviderException>(() => client.Find("app.example.com"));

        Assert.Contains("not JSON", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyCloudflareErrorStillNamesTheHttpStatus()
    {
        var (client, _) = Build(_ => (
            HttpStatusCode.Forbidden,
            """{"success":false,"errors":[{"code":9109,"message":""}]}"""));

        var exception = await Assert.ThrowsAsync<DnsProviderException>(() => client.Find("app.example.com"));

        Assert.Contains("9109", exception.Message, StringComparison.Ordinal);
        Assert.Contains("403", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("no reason given", exception.Message, StringComparison.Ordinal);
    }

    // ---- removal ------------------------------------------------------------

    /// <summary>
    /// The record id carries its zone, because Cloudflare's delete path needs both
    /// and re-resolving the zone at delete time could land on a different one.
    /// </summary>
    [Fact]
    public async Task RemovingUsesTheZoneTheRecordCameFrom()
    {
        var (client, handler) = Build(_ => (HttpStatusCode.OK, """{"success":true,"errors":[],"result":{}}"""));

        await client.Remove($"{ZoneId}/{RecordId}");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("DELETE", request.Method);
        Assert.Contains($"zones/{ZoneId}/dns_records/{RecordId}", request.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARecordIdWithoutItsZoneIsRefused()
    {
        var (client, _) = Build(_ => (HttpStatusCode.OK, "{}"));

        await Assert.ThrowsAsync<DnsProviderException>(() => client.Remove("just-an-id"));
    }
}
