using System.Net;
using System.Net.Http.Headers;
using PinqOps.Registries;
using Xunit;

namespace PinqOps.Tests.Registries;

/// <summary>
/// Asking a registry what a tag points at. The token dance is the protocol rather
/// than an edge case — a public image gets a token too, it is just free — so most of
/// what is exercised here is the 401-and-retry.
/// </summary>
public class RegistryClientTests
{
    private const string Digest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    /// <summary>Answers each request from a script keyed by the request's path.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;

        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) => _answer = answer;

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_answer(request));
        }
    }

    private static HttpResponseMessage WithDigest(string digest = Digest)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("Docker-Content-Digest", digest);
        return response;
    }

    private static HttpResponseMessage Challenge(string parameter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Bearer", parameter));
        return response;
    }

    private static HttpResponseMessage Token(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private static RegistryClient Client(ScriptedHandler handler) => new(new HttpClient(handler));

    [Fact]
    public async Task ARegistryThatAnswersStraightAwayNeedsNoToken()
    {
        var handler = new ScriptedHandler(_ => WithDigest());

        var result = await Client(handler).DigestAsync("ghcr.io/acme/app:v1");

        Assert.Equal(Digest, result.Digest);
        Assert.Single(handler.Requests);
    }

    /// <summary>
    /// The manifest is a header, not a body. Asking for the body would download a
    /// manifest nobody reads, on a schedule.
    /// </summary>
    [Fact]
    public async Task ItAsksWithHeadAndForEveryManifestTypeARegistryMightUse()
    {
        var handler = new ScriptedHandler(_ => WithDigest());

        await Client(handler).DigestAsync("ghcr.io/acme/app:v1");

        var request = handler.Requests[0];
        Assert.Equal(HttpMethod.Head, request.Method);
        Assert.Equal("https://ghcr.io/v2/acme/app/manifests/v1", request.RequestUri!.ToString());
        // Without the index types a multi-architecture image answers 404, or a
        // digest for a manifest nobody is running.
        Assert.Contains(request.Headers.Accept, header =>
            header.MediaType == "application/vnd.oci.image.index.v1+json");
    }

    /// <summary>
    /// A token endpoint that answers 2xx with something that is not JSON — a
    /// captive portal, a proxy interstitial, an HTML error page a load balancer
    /// substituted. This has to come back as "no token", because the whole method
    /// hands back a reason rather than throwing one: it runs on the hourly update
    /// check, so an exception escaping here stops the check for every image
    /// instead of failing this one lookup.
    /// </summary>
    [Theory]
    [InlineData("<html><body>Sign in to continue</body></html>")]
    [InlineData("")]
    [InlineData("not json at all")]
    public async Task ATokenEndpointThatDoesNotAnswerJsonIsReportedNotThrown(string body)
    {
        var handler = new ScriptedHandler(request =>
            request.RequestUri!.Host == "auth.example.com"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : Challenge("""realm="https://auth.example.com/token",service="registry" """));

        var result = await Client(handler).DigestAsync("registry.example.com/acme/app:v1");

        Assert.Null(result.Digest);
        Assert.NotNull(result.Problem);
    }

    /// <summary>Valid JSON that simply carries no token is the same answer.</summary>
    [Theory]
    [InlineData("""{"error":"denied"}""")]
    [InlineData("""{"token":""}""")]
    [InlineData("""{"token":123}""")]
    [InlineData("[]")]
    public async Task AJsonBodyWithNoUsableTokenIsReportedToo(string body)
    {
        var handler = new ScriptedHandler(request =>
            request.RequestUri!.Host == "auth.example.com"
                ? Token(body)
                : Challenge("""realm="https://auth.example.com/token",service="registry" """));

        var result = await Client(handler).DigestAsync("registry.example.com/acme/app:v1");

        Assert.Null(result.Digest);
        Assert.NotNull(result.Problem);
    }

    [Fact]
    public async Task AChallengeIsFollowedToItsTokenEndpointAndTheRequestRetried()
    {
        var handler = new ScriptedHandler(request =>
            request.RequestUri!.Host == "auth.example.com"
                ? Token("""{"token":"issued"}""")
                : request.Headers.Authorization is null
                    ? Challenge("""realm="https://auth.example.com/token",service="registry",scope="repository:acme/app:pull" """)
                    : WithDigest());

        var result = await Client(handler).DigestAsync("registry.example.com/acme/app:v1");

        Assert.Equal(Digest, result.Digest);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("scope=repository%3Aacme%2Fapp%3Apull", handler.Requests[1].RequestUri!.Query);
        Assert.Equal("issued", handler.Requests[2].Headers.Authorization!.Parameter);
    }

    /// <summary>
    /// Docker Hub answers with <c>token</c>; GitHub's registry answers with
    /// <c>access_token</c>. Reading only one of them makes the other look like a
    /// registry that refuses to issue tokens.
    /// </summary>
    [Fact]
    public async Task EitherNameForTheTokenIsRead()
    {
        var handler = new ScriptedHandler(request =>
            request.RequestUri!.Host == "auth.example.com"
                ? Token("""{"access_token":"issued"}""")
                : request.Headers.Authorization is null
                    ? Challenge("""realm="https://auth.example.com/token",service="registry" """)
                    : WithDigest());

        Assert.Equal(Digest, (await Client(handler).DigestAsync("registry.example.com/acme/app:v1")).Digest);
    }

    [Fact]
    public async Task CredentialsAreOnlySentToTheTokenEndpointTheRegistryNamed()
    {
        var handler = new ScriptedHandler(request =>
            request.RequestUri!.Host == "auth.example.com"
                ? Token("""{"token":"issued"}""")
                : request.Headers.Authorization?.Scheme == "Bearer"
                    ? WithDigest()
                    : Challenge("""realm="https://auth.example.com/token",service="registry" """));

        await Client(handler).DigestAsync("registry.example.com/acme/app:v1", ("deploy", "hunter2"));

        // The manifest request never carries them; only the token request does.
        Assert.Null(handler.Requests[0].Headers.Authorization);
        Assert.Equal("Basic", handler.Requests[1].Headers.Authorization!.Scheme);
    }

    /// <summary>
    /// The realm comes from the registry's own challenge, and a plain-HTTP one would
    /// send a credential in the clear to whatever it named.
    /// </summary>
    [Fact]
    public async Task AChallengePointingAtPlainHttpIsNotFollowed()
    {
        var handler = new ScriptedHandler(_ => Challenge("""realm="http://auth.example.com/token" """));

        var result = await Client(handler).DigestAsync("registry.example.com/acme/app:v1", ("deploy", "hunter2"));

        Assert.False(result.Found);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AReferenceAlreadyPinnedToADigestIsNotAskedAbout()
    {
        var handler = new ScriptedHandler(_ => throw new Xunit.Sdk.XunitException("must not ask"));

        var result = await Client(handler).DigestAsync($"ghcr.io/acme/app@{Digest}");

        Assert.Equal(Digest, result.Digest);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ATagThatIsNotThereSaysSo()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await Client(handler).DigestAsync("ghcr.io/acme/app:nope");

        Assert.False(result.Found);
        Assert.Contains("is not in ghcr.io", result.Problem);
    }

    [Fact]
    public async Task ARegistryThatReportsNoDigestHasToldUsNothing()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var result = await Client(handler).DigestAsync("ghcr.io/acme/app:v1");

        Assert.False(result.Found);
        Assert.Contains("did not report a digest", result.Problem);
    }

    [Fact]
    public async Task AnUnreachableRegistryIsAProblemRatherThanAThrow()
    {
        var handler = new ScriptedHandler(_ => throw new HttpRequestException("Name or service not known"));

        var result = await Client(handler).DigestAsync("ghcr.io/acme/app:v1");

        Assert.False(result.Found);
        Assert.Contains("Name or service not known", result.Problem);
    }

    [Fact]
    public async Task SomethingThatIsNotAReferenceIsRefusedBeforeAnyRequest()
    {
        var handler = new ScriptedHandler(_ => WithDigest());

        Assert.False((await Client(handler).DigestAsync("-oops")).Found);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// A scope legitimately contains commas — <c>repository:acme/app:pull,push</c> —
    /// so splitting the header on every comma loses the rest of it.
    /// </summary>
    [Fact]
    public void ACommaInsideAQuotedScopeDoesNotEndTheField()
    {
        var headers = new HttpResponseMessage().Headers.WwwAuthenticate;
        headers.Add(new AuthenticationHeaderValue(
            "Bearer", """realm="https://auth.example.com/token",service="reg",scope="repository:acme/app:pull,push" """));

        var challenge = BearerChallenge.Parse(headers);

        Assert.Equal("repository:acme/app:pull,push", challenge!.Scope);
        Assert.Equal("reg", challenge.Service);
    }

    [Fact]
    public void ANonBearerChallengeIsNotOne()
    {
        var headers = new HttpResponseMessage().Headers.WwwAuthenticate;
        headers.Add(new AuthenticationHeaderValue("Basic", "realm=\"registry\""));

        Assert.Null(BearerChallenge.Parse(headers));
    }
}
