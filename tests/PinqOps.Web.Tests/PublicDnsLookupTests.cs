using System.Net;
using PinqOps.Web.Tests.Fakes;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// Asking someone other than this box whether a name resolves.
///
/// <para>The Domains page looks a name up before its record exists, so the local
/// resolver caches NXDOMAIN for the zone's SOA minimum — five minutes on
/// Cloudflare. The record is written a second later, and the ninety-second wait
/// that follows was spent re-reading that cache entry rather than watching the
/// record appear. These cover the way out of it.</para>
/// </summary>
public class PublicDnsLookupTests
{
    private const string Answer = """
        {"Status":0,"Answer":[{"name":"app.example.com.","type":1,"TTL":300,"data":"203.0.113.7"}]}
        """;

    private const string NoAnswer = """{"Status":3}""";

    [Fact]
    public async Task TheAddressAPublicResolverSeesIsReturned()
    {
        var handler = new SequencedHttpMessageHandler().Enqueue(HttpStatusCode.OK, Answer);
        using var client = new HttpClient(handler);

        var resolved = await PublicDnsLookup.ResolveAsync(client, "app.example.com");

        Assert.Equal(["203.0.113.7"], resolved);
        Assert.Contains("/dns-query", handler.Requests[0].Path);
        Assert.Contains("name=app.example.com", handler.Requests[0].Path);
    }

    /// <summary>
    /// A CNAME arrives in the same list as the addresses, and its data is a name.
    /// Taking every answer's data would hand a hostname to an IP comparison.
    /// </summary>
    [Fact]
    public void ACnameInTheAnswerIsNotAnAddress()
    {
        var resolved = PublicDnsLookup.Parse("""
            {"Status":0,"Answer":[
              {"name":"app.example.com.","type":5,"TTL":300,"data":"origin.example.net."},
              {"name":"origin.example.net.","type":1,"TTL":300,"data":"203.0.113.7"}]}
            """);

        Assert.Equal(["203.0.113.7"], resolved);
    }

    [Fact]
    public async Task TheSecondResolverIsAskedWhenTheFirstHasNothing()
    {
        // Cloudflare A, Cloudflare AAAA, then Google A.
        var handler = new SequencedHttpMessageHandler()
            .Enqueue(HttpStatusCode.ServiceUnavailable)
            .Enqueue(HttpStatusCode.OK, NoAnswer)
            .Enqueue(HttpStatusCode.OK, Answer);
        using var client = new HttpClient(handler);

        var resolved = await PublicDnsLookup.ResolveAsync(client, "app.example.com");

        Assert.Equal(["203.0.113.7"], resolved);
        Assert.Contains("/resolve", handler.Requests[2].Path);
    }

    /// <summary>
    /// "Cannot find out" and "not there yet" are the same answer to the caller: the
    /// wait carries on rather than an unreachable resolver failing a provision.
    /// </summary>
    [Fact]
    public async Task AResolverThatAnswersNothingUsableIsEmptyRatherThanAFailure()
    {
        var handler = new SequencedHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, NoAnswer)
            .Enqueue(HttpStatusCode.OK, NoAnswer)
            .Enqueue(HttpStatusCode.OK, "not json")
            .Enqueue(HttpStatusCode.InternalServerError);
        using var client = new HttpClient(handler);

        Assert.Empty(await PublicDnsLookup.ResolveAsync(client, "app.example.com"));
    }
}
