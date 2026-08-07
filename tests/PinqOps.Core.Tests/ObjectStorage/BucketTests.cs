using System.Net;
using PinqOps.ObjectStorage;
using Xunit;

namespace PinqOps.Tests.ObjectStorage;

public class BucketNameTests
{
    [Theory]
    [InlineData("uploads")]
    [InlineData("acme-uploads")]
    [InlineData("acme.uploads")]
    [InlineData("a1b")]
    public void AnOrdinaryNameIsAccepted(string name) => Assert.True(BucketName.IsValid(name));

    /// <summary>
    /// Refused rather than lowercased: otherwise <c>MyBucket</c> and <c>mybucket</c>
    /// are the same bucket in pinqops and two different names in whatever the
    /// operator typed into their application's configuration.
    /// </summary>
    [Fact]
    public void UppercaseIsRefusedRatherThanFolded() => Assert.False(BucketName.IsValid("MyBucket"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData(".leading")]
    [InlineData("two..dots")]
    [InlineData("has_underscore")]
    [InlineData("has/slash")]
    // A dotted quad collides with virtual-host addressing, so every service
    // refuses it.
    [InlineData("192.168.1.1")]
    public void AnythingThatIsNotOneIsRefused(string? name) => Assert.False(BucketName.IsValid(name));

    [Fact]
    public void AnAbsurdlyLongNameIsRefused() =>
        Assert.False(BucketName.IsValid(new string('a', BucketName.MaximumLength + 1)));
}

/// <summary>
/// A link that carries its own authorisation. The expiry is part of the signature,
/// which is the whole reason one can be shared at all.
/// </summary>
public class PresignedUrlTests
{
    private static readonly DateTimeOffset SignedAt = new(2026, 8, 2, 3, 0, 0, TimeSpan.Zero);

    private static S3Settings Settings(string endpoint = "https://s3.example.com", string prefix = "") =>
        new(endpoint, "auto", "uploads", "AKIDEXAMPLE", "secret", prefix);

    private static S3Client Client() => new(new HttpClient(new NeverCalledHandler()), () => SignedAt);

    /// <summary>Presigning is arithmetic, so nothing should reach the network.</summary>
    private sealed class NeverCalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("presigning must not make a request");
    }

    [Fact]
    public void TheLinkCarriesEverythingAReaderNeedsInTheQuery()
    {
        var url = Client().PresignGet(Settings(), "photo.jpg", 900);

        Assert.StartsWith("https://s3.example.com/uploads/photo.jpg?", url, StringComparison.Ordinal);
        foreach (var parameter in (string[])
        [
            "X-Amz-Algorithm=AWS4-HMAC-SHA256",
            "X-Amz-Credential=",
            "X-Amz-Date=20260802T030000Z",
            "X-Amz-Expires=900",
            "X-Amz-SignedHeaders=host",
            "X-Amz-Signature=",
        ])
        {
            Assert.Contains(parameter, url, StringComparison.Ordinal);
        }
    }

    /// <summary>Editing the expiry in the URL does not extend the link, it invalidates it.</summary>
    [Fact]
    public void TheExpiryIsPartOfWhatIsSigned() =>
        Assert.NotEqual(
            Signature(Client().PresignGet(Settings(), "photo.jpg", 60)),
            Signature(Client().PresignGet(Settings(), "photo.jpg", 3600)));

    [Fact]
    public void ADifferentKeyIsADifferentSignature() =>
        Assert.NotEqual(
            Signature(Client().PresignGet(Settings(), "a.jpg", 900)),
            Signature(Client().PresignGet(Settings(), "b.jpg", 900)));

    /// <summary>
    /// <c>host</c> is the one signed header, which ties the link to one endpoint —
    /// the same URL replayed against another service does not verify.
    /// </summary>
    [Fact]
    public void ADifferentEndpointIsADifferentSignature() =>
        Assert.NotEqual(
            Signature(Client().PresignGet(Settings("https://s3.example.com"), "a.jpg", 900)),
            Signature(Client().PresignGet(Settings("https://other.example.com"), "a.jpg", 900)));

    [Fact]
    public void APortIsCarriedIntoBothTheHostAndTheUrl() =>
        Assert.StartsWith(
            "http://minio:9000/uploads/a.jpg?",
            Client().PresignGet(Settings("http://minio:9000"), "a.jpg", 900),
            StringComparison.Ordinal);

    [Fact]
    public void ThePrefixIsPartOfTheLink() =>
        Assert.StartsWith(
            "https://s3.example.com/uploads/server-1/a.jpg?",
            Client().PresignGet(Settings(prefix: "server-1/"), "a.jpg", 900),
            StringComparison.Ordinal);

    [Fact]
    public void AKeyWithASpaceIsEncodedTheWayItWasSigned() =>
        Assert.Contains(
            "/uploads/my%20photo.jpg?",
            Client().PresignGet(Settings(), "my photo.jpg", 900),
            StringComparison.Ordinal);

    private static string Signature(string url) =>
        url.Split("X-Amz-Signature=", StringSplitOptions.None)[1];
}

public class BucketOperationTests
{
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

    private static S3Settings Settings() => new("https://s3.example.com", "auto", "unused", "AKIDEXAMPLE", "secret");

    [Fact]
    public async Task ListingBucketsReadsTheNames()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                <ListAllMyBucketsResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
                  <Buckets>
                    <Bucket><Name>uploads</Name></Bucket>
                    <Bucket><Name>backups</Name></Bucket>
                  </Buckets>
                </ListAllMyBucketsResult>
                """),
        });

        var (buckets, error) = await new S3Client(new HttpClient(handler)).ListBucketsAsync(Settings());

        Assert.Null(error);
        Assert.Equal(["uploads", "backups"], buckets);
        Assert.Equal("https://s3.example.com/", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task CreatingABucketIsAPutOnItsName()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var result = await new S3Client(new HttpClient(handler)).CreateBucketAsync(Settings(), "uploads");

        Assert.True(result.Ok);
        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Equal("https://s3.example.com/uploads", handler.Requests[0].RequestUri!.ToString());
    }

    [Theory]
    [InlineData("Uploads")]
    [InlineData("a")]
    [InlineData("has/slash")]
    public async Task ANameThatIsNotOneIsRefusedBeforeAnyRequest(string bucket)
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new S3Client(new HttpClient(handler));

        Assert.False((await client.CreateBucketAsync(Settings(), bucket)).Ok);
        Assert.False((await client.DeleteBucketAsync(Settings(), bucket)).Ok);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// The service refuses while the bucket holds anything, and pinqops does not
    /// empty it first — a button that reads "delete bucket" must not be the one that
    /// deletes what is in it.
    /// </summary>
    [Fact]
    public async Task ANonEmptyBucketIsRefusedByTheServiceAndTheReasonIsPassedOn()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("<Error><Code>BucketNotEmpty</Code></Error>"),
        });

        var result = await new S3Client(new HttpClient(handler)).DeleteBucketAsync(Settings(), "uploads");

        Assert.False(result.Ok);
        Assert.Contains("BucketNotEmpty", result.Error);
    }
}
