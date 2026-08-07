using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PinqOps.ObjectStorage;

/// <summary>What signing one request produced, ready to put on it.</summary>
/// <param name="Authorization">The <c>Authorization</c> header value.</param>
/// <param name="AmzDate">The <c>x-amz-date</c> value the signature was computed over.</param>
/// <param name="PayloadHash">The <c>x-amz-content-sha256</c> value, which S3 requires.</param>
public sealed record SignedRequest(string Authorization, string AmzDate, string PayloadHash);

/// <summary>
/// AWS Signature Version 4, by hand.
///
/// <para><b>Why not the SDK.</b> pinqops ships as one self-contained binary and
/// speaks to docker through its CLI; taking AWSSDK.S3 for four operations would add
/// tens of megabytes and a dependency tree to a program whose whole shape is "no
/// dependency tree". The signing algorithm is a page of HMAC and this is that page.</para>
///
/// <para><b>Everything here is exact or it is nothing.</b> A signature that is wrong
/// in any byte is rejected with the same message as a wrong password, so the tests
/// pin it against AWS's own published test vector rather than against itself.</para>
/// </summary>
public static class SigV4Signer
{
    public const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>The service name S3-compatible endpoints sign under, including R2 and MinIO.</summary>
    public const string Service = "s3";

    /// <summary>SHA-256 of no bytes, which is the payload hash of every bodyless request.</summary>
    public const string EmptyPayloadHash =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>
    /// Signs one request.
    ///
    /// <para><paramref name="headers"/> must include <c>host</c>: it is the only
    /// header S3 requires be signed, and it is what stops a signature being replayed
    /// against a different endpoint.</para>
    /// </summary>
    public static SignedRequest Sign(
        string method,
        string canonicalUri,
        IReadOnlyList<(string Key, string Value)> query,
        IReadOnlyDictionary<string, string> headers,
        string payloadHash,
        string accessKeyId,
        string secretAccessKey,
        string region,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalUri);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessKeyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretAccessKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);

        var amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = amzDate[..8];

        // Signed headers are lowercased, trimmed and sorted by name — the sort is
        // ordinal on the lowercased name, because that is the order the service
        // rebuilds them in and a different one produces a different string to sign.
        var signed = headers
            .Select(header => (Key: header.Key.ToLowerInvariant(), Value: Collapse(header.Value)))
            .Concat<(string Key, string Value)>([("x-amz-date", amzDate), ("x-amz-content-sha256", payloadHash)])
            .GroupBy(header => header.Key, StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderBy(header => header.Key, StringComparer.Ordinal)
            .ToList();

        var canonicalHeaders = string.Concat(signed.Select(header => $"{header.Key}:{header.Value}\n"));
        var signedHeaders = string.Join(';', signed.Select(header => header.Key));

        var canonicalRequest = string.Join(
            '\n',
            method.ToUpperInvariant(),
            canonicalUri,
            CanonicalQuery(query),
            canonicalHeaders,
            signedHeaders,
            payloadHash);

        var scope = $"{date}/{region}/{Service}/aws4_request";
        var stringToSign = string.Join('\n', Algorithm, amzDate, scope, Hex(Sha256(canonicalRequest)));

        var signature = Hex(HmacSha256(SigningKey(secretAccessKey, date, region), stringToSign));

        return new SignedRequest(
            $"{Algorithm} Credential={accessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}",
            amzDate,
            payloadHash);
    }

    /// <summary>What a presigned request needs in place of a payload hash.</summary>
    public const string UnsignedPayload = "UNSIGNED-PAYLOAD";

    /// <summary>
    /// The query string for a link that carries its own authorisation.
    ///
    /// <para><b>Everything moves into the query.</b> A presigned URL is handed to
    /// someone who cannot set headers — a browser following a link, a webhook
    /// receiver — so the credential, the date, the signed-header list and the
    /// signature all travel as parameters. <c>host</c> is still the only signed
    /// header, which is what keeps the link tied to one endpoint.</para>
    ///
    /// <para><b>The expiry is part of the signature.</b> Editing it in the URL does
    /// not extend the link, it invalidates it — which is the whole reason a
    /// presigned URL can be shared at all.</para>
    /// </summary>
    public static string PresignQuery(
        string method,
        string canonicalUri,
        string host,
        string accessKeyId,
        string secretAccessKey,
        string region,
        int expiresInSeconds,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessKeyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretAccessKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentOutOfRangeException.ThrowIfLessThan(expiresInSeconds, 1);

        var amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = amzDate[..8];
        var scope = $"{date}/{region}/{Service}/aws4_request";

        var query = new List<(string Key, string Value)>
        {
            ("X-Amz-Algorithm", Algorithm),
            ("X-Amz-Credential", $"{accessKeyId}/{scope}"),
            ("X-Amz-Date", amzDate),
            ("X-Amz-Expires", expiresInSeconds.ToString(CultureInfo.InvariantCulture)),
            ("X-Amz-SignedHeaders", "host"),
        };

        var canonicalRequest = string.Join(
            '\n',
            method.ToUpperInvariant(),
            canonicalUri,
            CanonicalQuery(query),
            $"host:{host}\n",
            "host",
            // A presigned GET has no body to hash, and the reader is not going to
            // compute one either.
            UnsignedPayload);

        var stringToSign = string.Join('\n', Algorithm, amzDate, scope, Hex(Sha256(canonicalRequest)));
        var signature = Hex(HmacSha256(SigningKey(secretAccessKey, date, region), stringToSign));

        // Appended after signing, because the signature cannot be part of what it
        // signs.
        query.Add(("X-Amz-Signature", signature));
        return CanonicalQuery(query);
    }

    /// <summary>
    /// The canonical query string: each key and value URI-encoded, sorted by encoded
    /// key. Sorted after encoding, not before — the two orders differ once a key
    /// contains a character that encodes to something ordering differently.
    /// </summary>
    public static string CanonicalQuery(IReadOnlyList<(string Key, string Value)> query)
    {
        ArgumentNullException.ThrowIfNull(query);

        return string.Join(
            '&',
            query
                .Select(pair => (Key: UriEncode(pair.Key, encodeSlash: true), Value: UriEncode(pair.Value, encodeSlash: true)))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ThenBy(pair => pair.Value, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));
    }

    /// <summary>
    /// A key as it appears in the canonical URI: every segment encoded, the slashes
    /// left alone.
    /// </summary>
    public static string CanonicalUriFor(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return "/" + string.Join('/', key.Split('/').Select(segment => UriEncode(segment, encodeSlash: true)));
    }

    /// <summary>
    /// AWS's own encoding rule, which is not <see cref="Uri.EscapeDataString"/>'s:
    /// unreserved characters are <c>A-Z a-z 0-9 - _ . ~</c> and everything else is
    /// percent-encoded with uppercase hex. Getting this wrong is the classic cause
    /// of a signature that works for every key until one has a space in it.
    /// </summary>
    public static string UriEncode(string value, bool encodeSlash)
    {
        ArgumentNullException.ThrowIfNull(value);

        var builder = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var character = (char)b;
            if (char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '~')
            {
                builder.Append(character);
            }
            else if (character == '/' && !encodeSlash)
            {
                builder.Append('/');
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The per-day, per-region signing key. Derived from the secret through four
    /// HMACs so the key that actually signs is scoped to one day and one region —
    /// which is why a leaked signature is not a leaked credential.
    /// </summary>
    public static byte[] SigningKey(string secretAccessKey, string date, string region)
    {
        var key = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secretAccessKey), date);
        key = HmacSha256(key, region);
        key = HmacSha256(key, Service);
        return HmacSha256(key, "aws4_request");
    }

    /// <summary>The hex payload hash S3 expects in <c>x-amz-content-sha256</c>.</summary>
    public static string PayloadHash(ReadOnlySpan<byte> payload) =>
        payload.Length == 0 ? EmptyPayloadHash : Convert.ToHexStringLower(SHA256.HashData(payload));

    /// <summary>The same, for a file too large to hold in memory.</summary>
    public static async Task<string> PayloadHashAsync(Stream payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var hash = await SHA256.HashDataAsync(payload, cancellationToken).ConfigureAwait(false);
        if (payload.CanSeek)
        {
            // The caller is about to send this same stream; leaving it at the end
            // would upload nothing and report success.
            payload.Position = 0;
        }

        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// A header value as the algorithm sees it: trimmed, with runs of spaces
    /// collapsed. Skipping this makes a signature that depends on whitespace nobody
    /// can see.
    /// </summary>
    private static string Collapse(string value)
    {
        var trimmed = value.Trim();
        var builder = new StringBuilder(trimmed.Length);
        var previousWasSpace = false;

        foreach (var character in trimmed)
        {
            var isSpace = character == ' ';
            if (!isSpace || !previousWasSpace)
            {
                builder.Append(character);
            }

            previousWasSpace = isSpace;
        }

        return builder.ToString();
    }

    private static byte[] Sha256(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static byte[] HmacSha256(byte[] key, string value) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));

    private static string Hex(byte[] value) => Convert.ToHexStringLower(value);
}
