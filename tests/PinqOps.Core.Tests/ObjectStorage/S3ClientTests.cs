using System.Net;
using PinqOps.ObjectStorage;
using Xunit;

namespace PinqOps.Tests.ObjectStorage;

/// <summary>
/// The four operations an offsite backup needs. What is checked here is the shape of
/// what goes on the wire — the URL, the signed headers, the listing paging — because
/// each of those fails as an opaque 403 or an empty list rather than as an error
/// anyone could read.
/// </summary>
public class S3ClientTests
{
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _answer;

        public ScriptedHandler(Func<HttpRequestMessage, int, HttpResponseMessage> answer) => _answer = answer;

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_answer(request, Requests.Count - 1));
        }
    }

    private static readonly DateTimeOffset SignedAt = new(2026, 8, 2, 3, 0, 0, TimeSpan.Zero);

    private static S3Settings Settings(string endpoint = "https://s3.example.com", string prefix = "") =>
        new(endpoint, "auto", "backups", "AKIDEXAMPLE", "secret", prefix);

    private static S3Client Client(ScriptedHandler handler) =>
        new(new HttpClient(handler), () => SignedAt);

    private static HttpResponseMessage Ok(string body = "") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    // ---- addressing -----------------------------------------------------------

    /// <summary>
    /// Virtual-host style needs a wildcard DNS record and a wildcard certificate,
    /// which MinIO on a LAN does not have. Every S3-compatible service accepts path
    /// style, AWS included.
    /// </summary>
    [Fact]
    public async Task ItAddressesTheBucketInThePathRatherThanTheHost()
    {
        var handler = new ScriptedHandler((_, _) => Ok());
        using var body = new MemoryStream("data"u8.ToArray());

        await Client(handler).PutAsync(Settings(), "db/2026-08-02.tgz", body);

        Assert.Equal(
            "https://s3.example.com/backups/db/2026-08-02.tgz",
            handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task ThePrefixGoesInFrontOfEveryKey()
    {
        var handler = new ScriptedHandler((_, _) => Ok());
        using var body = new MemoryStream("data"u8.ToArray());

        await Client(handler).PutAsync(Settings(prefix: "server-1/"), "db.tgz", body);

        Assert.Equal("https://s3.example.com/backups/server-1/db.tgz", handler.Requests[0].RequestUri!.ToString());
    }

    [Theory]
    [InlineData("", "db.tgz", "db.tgz")]
    [InlineData("server-1", "db.tgz", "server-1/db.tgz")]
    [InlineData("/server-1/", "/db.tgz", "server-1/db.tgz")]
    public void ThePrefixAndKeyMeetAtExactlyOneSlash(string prefix, string key, string expected) =>
        Assert.Equal(expected, S3Client.FullKey(Settings(prefix: prefix), key));

    [Fact]
    public async Task APortInTheEndpointIsPartOfTheSignedHost()
    {
        // MinIO is almost always on a port, and a host header that omits it signs a
        // different request from the one being sent.
        var handler = new ScriptedHandler((_, _) => Ok());
        using var body = new MemoryStream("data"u8.ToArray());

        await Client(handler).PutAsync(Settings("http://minio:9000"), "db.tgz", body);

        Assert.Contains("host;", handler.Requests[0].Headers.GetValues("Authorization").First());
        Assert.Equal("http://minio:9000/backups/db.tgz", handler.Requests[0].RequestUri!.ToString());
    }

    // ---- what is signed -------------------------------------------------------

    [Fact]
    public async Task EveryRequestCarriesTheSignatureTheDateAndThePayloadHash()
    {
        var handler = new ScriptedHandler((_, _) => Ok());
        using var body = new MemoryStream("data"u8.ToArray());

        await Client(handler).PutAsync(Settings(), "db.tgz", body);

        var request = handler.Requests[0];
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/", request.Headers.GetValues("Authorization").First());
        Assert.Equal("20260802T030000Z", request.Headers.GetValues("x-amz-date").First());
        Assert.Equal(
            SigV4Signer.PayloadHash("data"u8),
            request.Headers.GetValues("x-amz-content-sha256").First());
    }

    [Fact]
    public async Task ABodylessRequestSignsTheEmptyPayloadHash()
    {
        var handler = new ScriptedHandler((_, _) => Ok());

        await Client(handler).DeleteAsync(Settings(), "db.tgz");

        Assert.Equal(
            SigV4Signer.EmptyPayloadHash,
            handler.Requests[0].Headers.GetValues("x-amz-content-sha256").First());
    }

    /// <summary>
    /// Hashing reads the stream to the end; leaving it there would upload nothing
    /// and report success.
    /// </summary>
    [Fact]
    public async Task TheBodyIsStillThereAfterItHasBeenHashed()
    {
        // Read inside the handler: the request — and its content — is disposed as
        // soon as the send returns.
        string? sent = null;
        var handler = new ScriptedHandler((request, _) =>
        {
            sent = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Ok();
        });
        using var body = new MemoryStream("data"u8.ToArray());

        await Client(handler).PutAsync(Settings(), "db.tgz", body);

        Assert.Equal("data", sent);
    }

    [Fact]
    public async Task AnObjectTooLargeForOneRequestIsRefusedWithAReason()
    {
        var handler = new ScriptedHandler((_, _) => Ok());
        using var body = new OversizedStream();

        var result = await Client(handler).PutAsync(Settings(), "huge.tgz", body);

        Assert.False(result.Ok);
        Assert.Contains("5 GB", result.Error);
        Assert.Empty(handler.Requests);
    }

    /// <summary>A stream that claims to be larger than the single-request ceiling.</summary>
    private sealed class OversizedStream : MemoryStream
    {
        public override long Length => S3Client.MaximumObjectBytes + 1;
    }

    // ---- listing --------------------------------------------------------------

    private const string FirstPage = """
        <?xml version="1.0" encoding="UTF-8"?>
        <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
          <IsTruncated>true</IsTruncated>
          <NextContinuationToken>page2</NextContinuationToken>
          <Contents><Key>db/b.tgz</Key><Size>200</Size><LastModified>2026-08-02T03:00:00.000Z</LastModified></Contents>
        </ListBucketResult>
        """;

    private const string SecondPage = """
        <?xml version="1.0" encoding="UTF-8"?>
        <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
          <IsTruncated>false</IsTruncated>
          <Contents><Key>db/a.tgz</Key><Size>100</Size><LastModified>2026-08-01T03:00:00.000Z</LastModified></Contents>
        </ListBucketResult>
        """;

    /// <summary>
    /// A bucket backed up nightly for two years holds more than one page, and a
    /// retention sweep that saw only the first thousand keys would delete the wrong
    /// ones.
    /// </summary>
    [Fact]
    public async Task AListingIsPagedThroughToTheEnd()
    {
        var handler = new ScriptedHandler((_, index) => Ok(index == 0 ? FirstPage : SecondPage));

        var (objects, error) = await Client(handler).ListAsync(Settings());

        Assert.Null(error);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("continuation-token=page2", handler.Requests[1].RequestUri!.Query);
        // Oldest first, which is the order a retention sweep deletes in.
        Assert.Equal(["db/a.tgz", "db/b.tgz"], objects.Select(entry => entry.Key));
        Assert.Equal(100, objects[0].Size);
    }

    [Fact]
    public async Task TheListingAsksForTheSettingsPrefix()
    {
        var handler = new ScriptedHandler((_, _) => Ok(SecondPage));

        await Client(handler).ListAsync(Settings(prefix: "server-1/"));

        Assert.Contains("prefix=server-1%2F", handler.Requests[0].RequestUri!.Query);
        Assert.Contains("list-type=2", handler.Requests[0].RequestUri!.Query);
    }

    /// <summary>
    /// The listing has to ask for the namespace the uploads actually wrote to.
    ///
    /// <para>A prefix is allowed a leading slash — <c>ThePrefixAndKeyMeetAtExactlyOneSlash</c>
    /// declares that form valid, and keys written under it lose it. Asking S3 for
    /// the un-normalized spelling matches nothing, and S3 answers an empty listing
    /// rather than an error: the retention sweep then finds nothing to delete and
    /// the bucket grows past its configured count forever, while the page offering
    /// the offsite copies back shows none — with every upload having succeeded.</para>
    /// </summary>
    [Theory]
    [InlineData("/server-1")]
    [InlineData("server-1")]
    [InlineData("/server-1/")]
    [InlineData("server-1/")]
    public async Task TheListingAsksForTheNamespaceTheKeysWereWrittenIn(string prefix)
    {
        var handler = new ScriptedHandler((_, _) => Ok(SecondPage));

        await Client(handler).ListAsync(Settings(prefix: prefix));

        // Exactly what FullKey puts in front of every key it writes.
        Assert.Contains("prefix=server-1%2F", handler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task NoPrefixAsksForNoNamespace()
    {
        var handler = new ScriptedHandler((_, _) => Ok(SecondPage));

        await Client(handler).ListAsync(Settings(prefix: ""));

        Assert.DoesNotContain("prefix=", handler.Requests[0].RequestUri!.Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// Read without binding to the S3 namespace: one service changing it would
    /// otherwise turn a working listing into an empty one with no error at all.
    /// </summary>
    [Fact]
    public void AListingInAnyNamespaceIsStillRead()
    {
        var (objects, continuation) = S3Client.ParseListing(
            SecondPage.Replace("http://s3.amazonaws.com/doc/2006-03-01/", "urn:example", StringComparison.Ordinal));

        Assert.Single(objects);
        Assert.Null(continuation);
    }

    [Fact]
    public void SomethingThatIsNotAListingIsNoObjectsRatherThanAThrow()
    {
        var (objects, continuation) = S3Client.ParseListing("not xml at all");

        Assert.Empty(objects);
        Assert.Null(continuation);
    }

    // ---- failures -------------------------------------------------------------

    /// <summary>
    /// The one failure whose own message ("access denied") sends people to look at
    /// bucket policies when the answer is a mistyped secret or the wrong region.
    /// </summary>
    [Fact]
    public async Task ARejectedSignatureSaysWhatToCheck()
    {
        var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(
                "<Error><Code>SignatureDoesNotMatch</Code><Message>Access denied</Message></Error>"),
        });

        var result = await Client(handler).DeleteAsync(Settings(), "db.tgz");

        Assert.False(result.Ok);
        Assert.Contains("secret key and the region", result.Error);
    }

    [Fact]
    public async Task AnyOtherFailureReportsTheServicesOwnCode()
    {
        var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(
                "<Error><Code>NoSuchBucket</Code><Message>The specified bucket does not exist</Message></Error>"),
        });

        var result = await Client(handler).PutAsync(Settings(), "db.tgz", new MemoryStream("x"u8.ToArray()));

        Assert.False(result.Ok);
        Assert.Contains("NoSuchBucket", result.Error);
    }

    [Fact]
    public async Task AFailureWithNoXmlBodyStillSaysSomething()
    {
        var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var result = await Client(handler).DeleteAsync(Settings(), "db.tgz");

        Assert.False(result.Ok);
        Assert.Contains("502", result.Error);
    }

    [Fact]
    public async Task ADownloadWritesTheBodyToTheDestination()
    {
        var handler = new ScriptedHandler((_, _) => Ok("the backup"));
        using var destination = new MemoryStream();

        var result = await Client(handler).GetAsync(Settings(), "db.tgz", destination);

        Assert.True(result.Ok);
        Assert.Equal("the backup", System.Text.Encoding.UTF8.GetString(destination.ToArray()));
    }

    [Fact]
    public async Task AFailedDownloadLeavesNothingBehind()
    {
        var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("<Error><Code>NoSuchKey</Code></Error>"),
        });
        using var destination = new MemoryStream();

        var result = await Client(handler).GetAsync(Settings(), "db.tgz", destination);

        Assert.False(result.Ok);
        Assert.Empty(destination.ToArray());
    }
}
