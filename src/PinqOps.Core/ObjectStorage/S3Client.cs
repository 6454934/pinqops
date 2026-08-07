using System.Globalization;
using System.Net;
using System.Xml.Linq;

namespace PinqOps.ObjectStorage;

/// <summary>Where to put objects, and who to sign as.</summary>
/// <param name="Endpoint">
/// The service URL — <c>https://s3.eu-central-1.amazonaws.com</c>,
/// <c>https://&lt;account&gt;.r2.cloudflarestorage.com</c>, <c>http://minio:9000</c>.
/// </param>
/// <param name="Region">
/// The signing region. R2 uses <c>auto</c>; MinIO takes whatever it is configured
/// with. It is part of the signature, so a wrong one fails as an unhelpful 403.
/// </param>
public sealed record S3Settings(
    string Endpoint,
    string Region,
    string Bucket,
    string AccessKeyId,
    string SecretAccessKey,
    string Prefix = "");

/// <summary>One object in a listing.</summary>
public sealed record S3Object(string Key, long Size, DateTimeOffset LastModified);

/// <summary>What an operation did, or why it did not.</summary>
public sealed record S3Result(bool Ok, string? Error)
{
    public static readonly S3Result Success = new(true, null);
}

/// <summary>
/// The four S3 operations an offsite backup needs, over any S3-compatible endpoint.
///
/// <para><b>Path-style addressing, always.</b> Virtual-host style
/// (<c>bucket.s3.example.com</c>) needs a wildcard DNS record and a wildcard
/// certificate, which MinIO on a LAN and a self-hosted registry behind a private CA
/// do not have. Every S3-compatible service accepts path style; only AWS prefers the
/// other, and it accepts this one too.</para>
///
/// <para><b>No multipart upload.</b> A backup is a single tarball written in one
/// request, which S3 allows up to 5 GB. Past that the upload fails with the service's
/// own message rather than silently truncating — and multipart is a state machine
/// with its own abort path that would need to be right before it was worth
/// having.</para>
/// </summary>
public sealed class S3Client
{
    /// <summary>The single-request ceiling S3 imposes, so a larger file is refused with a reason.</summary>
    public const long MaximumObjectBytes = 5L * 1024 * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly Func<DateTimeOffset> _now;

    public S3Client(HttpClient? httpClient = null, Func<DateTimeOffset>? now = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The buckets this account can see, oldest first.</summary>
    public async Task<(IReadOnlyList<string> Buckets, string? Error)> ListBucketsAsync(
        S3Settings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var root = new UriBuilder(settings.Endpoint.TrimEnd('/')) { Path = "/" }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, root);
        var signed = await SignAsync(request, settings, "/", [], SigV4Signer.EmptyPayloadHash).ConfigureAwait(false);

        using var response = await _httpClient.SendAsync(signed, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return ([], (await FailureAsync(response, cancellationToken).ConfigureAwait(false)).Error);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return (ParseBuckets(body), null);
    }

    /// <summary>
    /// Creates a bucket. A name that already exists is reported by the service,
    /// which distinguishes "somebody else has it" from "you already made it" —
    /// a distinction pinqops has no way to draw on its own.
    /// </summary>
    public async Task<S3Result> CreateBucketAsync(
        S3Settings settings, string bucket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!BucketName.IsValid(bucket))
        {
            return new S3Result(false, $"'{bucket}' is not a bucket name.");
        }

        var url = new UriBuilder(settings.Endpoint.TrimEnd('/')) { Path = "/" + bucket }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        return await SendAsync(
            request, settings, "/" + bucket, [], SigV4Signer.EmptyPayloadHash, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a bucket. Every service refuses while it holds anything, and pinqops
    /// does not empty it first — a button that reads "delete bucket" must not be the
    /// one that deletes what is in it.
    /// </summary>
    public async Task<S3Result> DeleteBucketAsync(
        S3Settings settings, string bucket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!BucketName.IsValid(bucket))
        {
            return new S3Result(false, $"'{bucket}' is not a bucket name.");
        }

        var url = new UriBuilder(settings.Endpoint.TrimEnd('/')) { Path = "/" + bucket }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        return await SendAsync(
            request, settings, "/" + bucket, [], SigV4Signer.EmptyPayloadHash, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A link that carries its own authorisation, good for
    /// <paramref name="expiresInSeconds"/>.
    ///
    /// <para>Computed rather than requested: presigning is arithmetic over the
    /// credential, so there is no round trip and nothing to fail. That also means
    /// the link exists whether or not the object does — a presigned URL for a
    /// missing key is a valid link to a 404.</para>
    /// </summary>
    public string PresignGet(S3Settings settings, string key, int expiresInSeconds)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var endpoint = new Uri(settings.Endpoint.TrimEnd('/'));
        var host = endpoint.IsDefaultPort ? endpoint.Host : $"{endpoint.Host}:{endpoint.Port}";
        var path = ObjectPath(settings, key);

        var query = SigV4Signer.PresignQuery(
            "GET", path, host, settings.AccessKeyId, settings.SecretAccessKey, settings.Region, expiresInSeconds, _now());

        return $"{endpoint.Scheme}://{host}{path}?{query}";
    }

    private static List<string> ParseBuckets(string xml)
    {
        try
        {
            var root = XDocument.Parse(xml).Root;
            return
            [
                .. Elements(root, "Buckets")
                    .SelectMany(buckets => Elements(buckets, "Bucket"))
                    .Select(bucket => Value(bucket, "Name"))
                    .Where(name => name is { Length: > 0 })
                    .Select(name => name!),
            ];
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }
    }

    /// <summary>Uploads one object, replacing whatever was at that key.</summary>
    public async Task<S3Result> PutAsync(
        S3Settings settings, string key, Stream body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(body);

        if (body.CanSeek && body.Length > MaximumObjectBytes)
        {
            return new S3Result(
                false,
                $"This backup is larger than {MaximumObjectBytes / (1024 * 1024 * 1024)} GB, which is the most "
                + "that can be uploaded in one request.");
        }

        // Hashed before it is sent, because the hash is part of the signature. A
        // streaming alternative exists (STREAMING-AWS4-HMAC-SHA256-PAYLOAD) and is
        // a chunked framing format of its own; reading the file twice is the cheaper
        // correctness.
        var payloadHash = await SigV4Signer.PayloadHashAsync(body, cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Put, Url(settings, key, []))
        {
            Content = new StreamContent(body),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        return await SendAsync(request, settings, ObjectPath(settings, key), [], payloadHash, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Downloads one object into <paramref name="destination"/>.</summary>
    public async Task<S3Result> GetAsync(
        S3Settings settings, string key, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(destination);

        using var request = new HttpRequestMessage(HttpMethod.Get, Url(settings, key, []));
        var signed = await SignAsync(
            request, settings, ObjectPath(settings, key), [], SigV4Signer.EmptyPayloadHash).ConfigureAwait(false);

        using var response = await _httpClient
            .SendAsync(signed, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return await FailureAsync(response, cancellationToken).ConfigureAwait(false);
        }

        await response.Content.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        return S3Result.Success;
    }

    public async Task<S3Result> DeleteAsync(
        S3Settings settings, string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        using var request = new HttpRequestMessage(HttpMethod.Delete, Url(settings, key, []));
        return await SendAsync(
            request, settings, ObjectPath(settings, key), [], SigV4Signer.EmptyPayloadHash, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Everything under the settings' prefix, oldest first.
    ///
    /// <para>Paged through to the end: a bucket that has been backed up nightly for
    /// two years holds more than one page, and a retention sweep that saw only the
    /// first thousand keys would delete the wrong ones.</para>
    /// </summary>
    public async Task<(IReadOnlyList<S3Object> Objects, string? Error)> ListAsync(
        S3Settings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var objects = new List<S3Object>();
        string? continuation = null;

        do
        {
            var query = new List<(string, string)> { ("list-type", "2") };

            // Through FullKey, which is what put the prefix in front of every key
            // that was uploaded — so the namespace asked for here and the one
            // written to cannot come apart. Sending the configured spelling
            // instead meant a prefix with a leading slash, which FullKey strips,
            // listed a namespace nothing had ever been written to. S3 answers that
            // with an empty listing and no error, so retention deleted nothing and
            // the offsite copies could not be found again.
            var prefix = FullKey(settings, string.Empty);
            if (prefix.Length > 0)
            {
                query.Add(("prefix", prefix));
            }

            if (continuation is not null)
            {
                query.Add(("continuation-token", continuation));
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, Url(settings, key: null, query));
            var signed = await SignAsync(
                request, settings, BucketPath(settings), query, SigV4Signer.EmptyPayloadHash).ConfigureAwait(false);

            using var response = await _httpClient.SendAsync(signed, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return ([], (await FailureAsync(response, cancellationToken).ConfigureAwait(false)).Error);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            (var page, continuation) = ParseListing(body);
            objects.AddRange(page);
        }
        while (continuation is not null);

        return (
            [.. objects.OrderBy(entry => entry.LastModified).ThenBy(entry => entry.Key, StringComparer.Ordinal)],
            null);
    }

    /// <summary>
    /// Reads a <c>ListObjectsV2</c> response.
    ///
    /// <para>Namespace-agnostic on purpose: AWS answers in the S3 namespace, MinIO
    /// answers in the same one, and one of them changing it would otherwise turn a
    /// working listing into an empty one with no error.</para>
    /// </summary>
    internal static (List<S3Object> Objects, string? Continuation) ParseListing(string xml)
    {
        var objects = new List<S3Object>();
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return (objects, null);
        }

        foreach (var contents in Elements(document.Root, "Contents"))
        {
            var key = Value(contents, "Key");
            if (key is null)
            {
                continue;
            }

            _ = long.TryParse(Value(contents, "Size"), NumberStyles.None, CultureInfo.InvariantCulture, out var size);
            _ = DateTimeOffset.TryParse(
                Value(contents, "LastModified"),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var modified);

            objects.Add(new S3Object(key, size, modified));
        }

        var truncated = string.Equals(Value(document.Root, "IsTruncated"), "true", StringComparison.OrdinalIgnoreCase);
        return (objects, truncated ? Value(document.Root, "NextContinuationToken") : null);
    }

    private static IEnumerable<XElement> Elements(XElement? parent, string name) =>
        parent?.Elements().Where(element => element.Name.LocalName == name) ?? [];

    private static string? Value(XElement? parent, string name) =>
        Elements(parent, name).FirstOrDefault()?.Value;

    private async Task<S3Result> SendAsync(
        HttpRequestMessage request,
        S3Settings settings,
        string canonicalUri,
        IReadOnlyList<(string, string)> query,
        string payloadHash,
        CancellationToken cancellationToken)
    {
        var signed = await SignAsync(request, settings, canonicalUri, query, payloadHash).ConfigureAwait(false);
        using var response = await _httpClient.SendAsync(signed, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? S3Result.Success
            : await FailureAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private Task<HttpRequestMessage> SignAsync(
        HttpRequestMessage request,
        S3Settings settings,
        string canonicalUri,
        IReadOnlyList<(string Key, string Value)> query,
        string payloadHash)
    {
        var host = request.RequestUri!.IsDefaultPort
            ? request.RequestUri.Host
            : $"{request.RequestUri.Host}:{request.RequestUri.Port}";

        var signed = SigV4Signer.Sign(
            request.Method.Method,
            canonicalUri,
            query,
            new Dictionary<string, string> { ["host"] = host },
            payloadHash,
            settings.AccessKeyId,
            settings.SecretAccessKey,
            settings.Region,
            _now());

        request.Headers.TryAddWithoutValidation("Authorization", signed.Authorization);
        request.Headers.TryAddWithoutValidation("x-amz-date", signed.AmzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", signed.PayloadHash);
        return Task.FromResult(request);
    }

    /// <summary>
    /// The service's own message. S3 answers a failure with an XML body naming the
    /// code — <c>SignatureDoesNotMatch</c>, <c>NoSuchBucket</c>, <c>AccessDenied</c>
    /// — and those are the three things that actually go wrong; replacing them with
    /// the status code alone would throw away the answer.
    /// </summary>
    private static async Task<S3Result> FailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = string.Empty;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
        }

        string? code = null;
        string? message = null;
        try
        {
            var root = XDocument.Parse(body).Root;
            code = Value(root, "Code");
            message = Value(root, "Message");
        }
        catch (System.Xml.XmlException)
        {
        }

        if (response.StatusCode == HttpStatusCode.Forbidden && code is "SignatureDoesNotMatch")
        {
            // Named, because it is the one failure whose message ("access denied")
            // sends people to look at bucket policies when the answer is a mistyped
            // secret or a wrong region.
            return new S3Result(false, "The signature was rejected — check the secret key and the region.");
        }

        var detail = code is null ? body.Trim() : $"{code}: {message}".Trim(':', ' ');
        return new S3Result(
            false,
            detail.Length > 0 ? detail : $"The storage service answered {(int)response.StatusCode}.");
    }

    /// <summary>
    /// The request URL, path style. Values are encoded with AWS's rule rather than
    /// the framework's, so what is sent matches what was signed.
    /// </summary>
    private static Uri Url(S3Settings settings, string? key, IReadOnlyList<(string Key, string Value)> query)
    {
        var builder = new UriBuilder(settings.Endpoint.TrimEnd('/'))
        {
            Path = key is null ? BucketPath(settings) : ObjectPath(settings, key),
        };

        if (query.Count > 0)
        {
            builder.Query = SigV4Signer.CanonicalQuery(query);
        }

        return builder.Uri;
    }

    private static string BucketPath(S3Settings settings) =>
        "/" + SigV4Signer.UriEncode(settings.Bucket, encodeSlash: true);

    private static string ObjectPath(S3Settings settings, string key) =>
        BucketPath(settings) + SigV4Signer.CanonicalUriFor(FullKey(settings, key));

    /// <summary>The key with the settings' prefix in front, with exactly one slash between.</summary>
    public static string FullKey(S3Settings settings, string key)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(key);

        var prefix = settings.Prefix.Trim('/');
        var trimmed = key.TrimStart('/');
        return prefix.Length == 0 ? trimmed : $"{prefix}/{trimmed}";
    }
}
