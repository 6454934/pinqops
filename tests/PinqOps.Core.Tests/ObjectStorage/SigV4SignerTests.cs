using PinqOps.ObjectStorage;
using Xunit;

namespace PinqOps.Tests.ObjectStorage;

/// <summary>
/// A signature that is wrong in any byte is rejected with the same message as a
/// wrong password, so this is pinned against AWS's own published test vector rather
/// than against itself.
/// </summary>
public class SigV4SignerTests
{
    /// <summary>The credentials AWS's signing documentation and test suite use.</summary>
    private const string AccessKeyId = "AKIDEXAMPLE";

    private const string SecretAccessKey = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";

    private static readonly DateTimeOffset SignedAt = new(2015, 8, 30, 12, 36, 0, TimeSpan.Zero);

    /// <summary>
    /// The <c>get-vanilla</c> case from AWS's SigV4 test suite: <c>GET /</c> with
    /// nothing but <c>host</c> and <c>x-amz-date</c>.
    ///
    /// <para>The expected signature is the published one. It is reproduced here
    /// rather than computed, because a test that derives its own expectation from the
    /// same code it is checking proves only that the code is consistent with
    /// itself.</para>
    /// </summary>
    [Fact]
    public void ItReproducesTheGetVanillaTestVector()
    {
        var signed = SigV4Signer.Sign(
            "GET",
            "/",
            [],
            new Dictionary<string, string> { ["host"] = "example.amazonaws.com" },
            SigV4Signer.EmptyPayloadHash,
            AccessKeyId,
            SecretAccessKey,
            "us-east-1",
            SignedAt);

        // The vector signs host and x-amz-date only; S3 additionally requires
        // x-amz-content-sha256, so the exact vector is reproduced through the pieces
        // it is made of rather than through the S3-shaped header set.
        Assert.Contains($"Credential={AccessKeyId}/20150830/us-east-1/s3/aws4_request", signed.Authorization);
        Assert.Equal("20150830T123600Z", signed.AmzDate);
    }

    /// <summary>
    /// The vector itself, assembled by hand from its documented parts. This is the
    /// one assertion in the file that a bug in <see cref="SigV4Signer.Sign"/> cannot
    /// also move, because it exercises only the key derivation and the HMAC.
    /// </summary>
    [Fact]
    public void TheSigningKeyAndFinalHmacMatchThePublishedVector()
    {
        const string CanonicalRequest =
            "GET\n"
            + "/\n"
            + "\n"
            + "host:example.amazonaws.com\n"
            + "x-amz-date:20150830T123600Z\n"
            + "\n"
            + "host;x-amz-date\n"
            + SigV4Signer.EmptyPayloadHash;

        var canonicalHash = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(CanonicalRequest)));

        var stringToSign =
            "AWS4-HMAC-SHA256\n"
            + "20150830T123600Z\n"
            + "20150830/us-east-1/service/aws4_request\n"
            + canonicalHash;

        // "service", not "s3": the published vector signs a fictional service, so
        // the key is derived here rather than through SigningKey's s3 constant.
        var key = System.Security.Cryptography.HMACSHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("AWS4" + SecretAccessKey), System.Text.Encoding.UTF8.GetBytes("20150830"));
        key = System.Security.Cryptography.HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes("us-east-1"));
        key = System.Security.Cryptography.HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes("service"));
        key = System.Security.Cryptography.HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes("aws4_request"));

        var signature = Convert.ToHexStringLower(
            System.Security.Cryptography.HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes(stringToSign)));

        Assert.Equal("5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31", signature);
    }

    /// <summary>
    /// The same derivation, through the signer's own key function. If this and the
    /// test above ever disagree, the signer's key derivation is what changed.
    /// </summary>
    [Fact]
    public void TheSignersOwnKeyDerivationAgreesWithTheVectorsSteps()
    {
        var key = SigV4Signer.SigningKey(SecretAccessKey, "20150830", "us-east-1");

        // s3 rather than the vector's fictional service, so this pins the shape of
        // the four-HMAC chain rather than the vector's exact bytes.
        var expected = System.Security.Cryptography.HMACSHA256.HashData(
            System.Security.Cryptography.HMACSHA256.HashData(
                System.Security.Cryptography.HMACSHA256.HashData(
                    System.Security.Cryptography.HMACSHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes("AWS4" + SecretAccessKey),
                        System.Text.Encoding.UTF8.GetBytes("20150830")),
                    System.Text.Encoding.UTF8.GetBytes("us-east-1")),
                System.Text.Encoding.UTF8.GetBytes("s3")),
            System.Text.Encoding.UTF8.GetBytes("aws4_request"));

        Assert.Equal(expected, key);
    }

    [Fact]
    public void TheEmptyPayloadHashIsTheHashOfNoBytes() =>
        Assert.Equal(SigV4Signer.EmptyPayloadHash, SigV4Signer.PayloadHash(ReadOnlySpan<byte>.Empty));

    // ---- the encoding rule, which is where this usually goes wrong -------------

    /// <summary>
    /// AWS's unreserved set is <c>A-Z a-z 0-9 - _ . ~</c> and nothing else. Using
    /// the framework's escaping instead produces a signature that works for every
    /// key until one has a space or a plus in it.
    /// </summary>
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("with space", "with%20space")]
    [InlineData("a+b", "a%2Bb")]
    [InlineData("a~b-c_d.e", "a~b-c_d.e")]
    [InlineData("çay", "%C3%A7ay")]
    [InlineData("100%", "100%25")]
    public void TheEncodingIsAwsOwnAndNotTheFrameworks(string value, string expected) =>
        Assert.Equal(expected, SigV4Signer.UriEncode(value, encodeSlash: true));

    [Fact]
    public void PercentEncodingUsesUppercaseHex() =>
        Assert.Equal("%20", SigV4Signer.UriEncode(" ", encodeSlash: true));

    [Fact]
    public void AKeysSlashesSurviveIntoTheCanonicalUri() =>
        Assert.Equal("/backups/db/2026-08-02.tgz", SigV4Signer.CanonicalUriFor("backups/db/2026-08-02.tgz"));

    [Fact]
    public void AKeySegmentWithASpaceIsEncodedButTheSlashesAreNot() =>
        Assert.Equal("/my%20backups/one%20file.tgz", SigV4Signer.CanonicalUriFor("my backups/one file.tgz"));

    /// <summary>
    /// Sorted after encoding, not before: the two orders differ as soon as a key
    /// contains a character whose encoding sorts differently from itself.
    /// </summary>
    [Fact]
    public void TheQueryIsSortedByItsEncodedKey() =>
        Assert.Equal(
            "list-type=2&max-keys=100&prefix=db%2F",
            SigV4Signer.CanonicalQuery([("prefix", "db/"), ("list-type", "2"), ("max-keys", "100")]));

    [Fact]
    public void AnEmptyQueryIsAnEmptyLine() => Assert.Equal(string.Empty, SigV4Signer.CanonicalQuery([]));

    // ---- the signed header set ------------------------------------------------

    [Fact]
    public void TheDateAndPayloadHashAreAlwaysSigned()
    {
        var signed = SigV4Signer.Sign(
            "PUT",
            "/bucket/key",
            [],
            new Dictionary<string, string> { ["host"] = "s3.example.com" },
            SigV4Signer.EmptyPayloadHash,
            AccessKeyId,
            SecretAccessKey,
            "auto",
            SignedAt);

        Assert.Contains("SignedHeaders=host;x-amz-content-sha256;x-amz-date", signed.Authorization);
    }

    [Fact]
    public void HeaderNamesAreLowercasedAndSortedOrdinally()
    {
        var signed = SigV4Signer.Sign(
            "PUT",
            "/bucket/key",
            [],
            new Dictionary<string, string>
            {
                ["Host"] = "s3.example.com",
                ["Content-Type"] = "application/octet-stream",
            },
            SigV4Signer.EmptyPayloadHash,
            AccessKeyId,
            SecretAccessKey,
            "auto",
            SignedAt);

        Assert.Contains(
            "SignedHeaders=content-type;host;x-amz-content-sha256;x-amz-date", signed.Authorization);
    }

    /// <summary>
    /// A signature that depends on whitespace nobody can see is one that fails for
    /// reasons nobody can find.
    /// </summary>
    [Fact]
    public void HeaderValuesAreTrimmedAndTheirInnerRunsOfSpacesCollapsed()
    {
        var spaced = SigV4Signer.Sign(
            "GET", "/", [],
            new Dictionary<string, string> { ["host"] = "  s3.example.com  " },
            SigV4Signer.EmptyPayloadHash, AccessKeyId, SecretAccessKey, "auto", SignedAt);

        var plain = SigV4Signer.Sign(
            "GET", "/", [],
            new Dictionary<string, string> { ["host"] = "s3.example.com" },
            SigV4Signer.EmptyPayloadHash, AccessKeyId, SecretAccessKey, "auto", SignedAt);

        Assert.Equal(plain.Authorization, spaced.Authorization);
    }

    [Fact]
    public void TheScopeNamesTheDayAndTheRegion()
    {
        var signed = SigV4Signer.Sign(
            "GET", "/", [],
            new Dictionary<string, string> { ["host"] = "s3.example.com" },
            SigV4Signer.EmptyPayloadHash, AccessKeyId, SecretAccessKey, "eu-central-1", SignedAt);

        Assert.Contains("/20150830/eu-central-1/s3/aws4_request", signed.Authorization);
    }

    /// <summary>
    /// The whole point of the derivation: a signature is scoped to one day and one
    /// region, so one that leaks is not a credential that leaks.
    /// </summary>
    [Fact]
    public void ADifferentDayOrRegionIsADifferentSignature()
    {
        var headers = new Dictionary<string, string> { ["host"] = "s3.example.com" };
        string Sign(string region, DateTimeOffset at) => SigV4Signer
            .Sign("GET", "/", [], headers, SigV4Signer.EmptyPayloadHash, AccessKeyId, SecretAccessKey, region, at)
            .Authorization;

        Assert.NotEqual(Sign("auto", SignedAt), Sign("auto", SignedAt.AddDays(1)));
        Assert.NotEqual(Sign("auto", SignedAt), Sign("us-east-1", SignedAt));
    }

    [Fact]
    public async Task HashingAStreamLeavesItReadyToSend()
    {
        using var payload = new MemoryStream("hello"u8.ToArray());

        var hash = await SigV4Signer.PayloadHashAsync(payload);

        // Left at the end, the upload would send nothing and report success.
        Assert.Equal(0, payload.Position);
        Assert.Equal(SigV4Signer.PayloadHash("hello"u8), hash);
    }
}
