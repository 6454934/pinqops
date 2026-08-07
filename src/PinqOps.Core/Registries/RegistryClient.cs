using System.Net;
using System.Net.Http.Headers;

namespace PinqOps.Registries;

/// <summary>What a registry said about a tag.</summary>
/// <param name="Digest">The manifest digest, or null when it could not be read.</param>
/// <param name="Problem">Why not, when there is no digest.</param>
public sealed record ManifestDigest(string? Digest, string? Problem)
{
    public bool Found => Digest is { Length: > 0 };
}

/// <summary>
/// Asks a registry what a tag currently points at.
///
/// <para><b>A HEAD, not a pull.</b> The answer is one header — the manifest digest —
/// and comparing it against what a container is running is the whole check. Pulling
/// to find out whether there is something to pull would cost the bandwidth this is
/// meant to save, on a schedule.</para>
///
/// <para><b>The token dance is the protocol, not an edge case.</b> A registry answers
/// the first request with 401 and a <c>WWW-Authenticate</c> header naming where to
/// get a token and for what scope. Anonymous access to a public image works exactly
/// the same way — there is a token, it is just free — so there is no shorter path
/// that works for Docker Hub at all.</para>
/// </summary>
public sealed class RegistryClient
{
    /// <summary>
    /// Every manifest type a registry might answer with. Without these a modern
    /// multi-architecture image answers 404 or, worse, a digest for a manifest
    /// nobody is running.
    /// </summary>
    private static readonly string[] ManifestTypes =
    [
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.docker.distribution.manifest.v2+json",
    ];

    private readonly HttpClient _httpClient;

    public RegistryClient(HttpClient? httpClient = null) =>
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    /// The digest <paramref name="reference"/>'s tag points at right now.
    ///
    /// <para><paramref name="credentials"/> is used only if the registry asks for
    /// them; a public image never sees them.</para>
    /// </summary>
    public async Task<ManifestDigest> DigestAsync(
        string reference,
        (string Username, string Password)? credentials = null,
        CancellationToken cancellationToken = default)
    {
        if (RegistryReference.Parse(reference) is not { } parts)
        {
            return new ManifestDigest(null, $"'{reference}' is not a valid image reference.");
        }

        if (parts.IsPinnedToADigest)
        {
            // Nothing to check: the reference already says exactly which image it
            // means, and it will never point anywhere else.
            return new ManifestDigest(parts.Digest, null);
        }

        var url = $"https://{RegistryReference.ApiHost(parts.Registry)}/v2/{parts.Repository}/manifests/{parts.Tag}";

        try
        {
            using var first = await SendAsync(url, token: null, cancellationToken).ConfigureAwait(false);
            if (first.StatusCode != HttpStatusCode.Unauthorized)
            {
                return Read(first, parts);
            }

            var challenge = BearerChallenge.Parse(first.Headers.WwwAuthenticate);
            if (challenge is null)
            {
                return new ManifestDigest(null, "The registry refused the request and did not say how to authenticate.");
            }

            var token = await TokenAsync(challenge, credentials, cancellationToken).ConfigureAwait(false);
            if (token is null)
            {
                return new ManifestDigest(null, "The registry would not issue a token for this image.");
            }

            using var second = await SendAsync(url, token, cancellationToken).ConfigureAwait(false);
            return Read(second, parts);
        }
        catch (HttpRequestException exception)
        {
            return new ManifestDigest(null, exception.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ManifestDigest(null, "The registry did not answer in time.");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string url, string? token, CancellationToken cancellationToken)
    {
        // HEAD: the digest is a header, and asking for the body would download a
        // manifest nobody reads.
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        foreach (var type in ManifestTypes)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(type));
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static ManifestDigest Read(HttpResponseMessage response, ImageReferenceParts parts)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new ManifestDigest(null, $"{parts.Repository}:{parts.Tag} is not in {parts.Registry}.");
        }

        if (!response.IsSuccessStatusCode)
        {
            return new ManifestDigest(null, $"The registry answered {(int)response.StatusCode}.");
        }

        // The header docker itself uses. A registry that omits it has told us
        // nothing, and guessing from the body would be a different digest.
        return response.Headers.TryGetValues("Docker-Content-Digest", out var values)
            && values.FirstOrDefault() is { Length: > 0 } digest
                ? new ManifestDigest(digest, null)
                : new ManifestDigest(null, "The registry did not report a digest.");
    }

    private async Task<string?> TokenAsync(
        BearerChallenge challenge,
        (string Username, string Password)? credentials,
        CancellationToken cancellationToken)
    {
        var query = new List<string>();
        if (challenge.Service is { Length: > 0 })
        {
            query.Add($"service={Uri.EscapeDataString(challenge.Service)}");
        }

        if (challenge.Scope is { Length: > 0 })
        {
            query.Add($"scope={Uri.EscapeDataString(challenge.Scope)}");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            query.Count > 0 ? $"{challenge.Realm}?{string.Join('&', query)}" : challenge.Realm);

        if (credentials is { } given)
        {
            // Basic, once, to the token endpoint the registry named — never to an
            // arbitrary host, because the realm comes from the registry's own
            // challenge and nothing else.
            var encoded = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{given.Username}:{given.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // A 2xx is not a promise of JSON. A captive portal, a proxy interstitial or
        // a load balancer's error page all answer 200 with HTML, and a registry that
        // answers a JSON array rather than an object gets just as far. Both used to
        // throw out of here — out of a method whose whole contract is to hand back a
        // reason — and this runs on the hourly update check, so one such registry
        // stopped the check for every image rather than failing its own lookup.
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }

            // Docker Hub answers with "token"; several registries answer with
            // "access_token" instead, and one of them is GitHub's.
            foreach (var name in (string[])["token", "access_token"])
            {
                if (document.RootElement.TryGetProperty(name, out var value)
                    && value.ValueKind == System.Text.Json.JsonValueKind.String
                    && value.GetString() is { Length: > 0 } token)
                {
                    return token;
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON at all, which is the same answer as JSON carrying no token.
        }

        return null;
    }
}

/// <summary>Where a registry says to get a token, and for what.</summary>
public sealed record BearerChallenge(string Realm, string? Service, string? Scope)
{
    /// <summary>
    /// The challenge in a <c>WWW-Authenticate</c> header, or null when it is not a
    /// Bearer one.
    ///
    /// <para>The realm is only ever used as given, and only over HTTPS — it comes
    /// from the registry the caller already chose to talk to.</para>
    /// </summary>
    public static BearerChallenge? Parse(HttpHeaderValueCollection<AuthenticationHeaderValue> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        foreach (var header in headers)
        {
            if (!string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
                || header.Parameter is not { Length: > 0 } parameter)
            {
                continue;
            }

            var fields = Fields(parameter);
            if (fields.TryGetValue("realm", out var realm)
                && Uri.TryCreate(realm, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps)
            {
                return new BearerChallenge(
                    realm,
                    fields.GetValueOrDefault("service"),
                    fields.GetValueOrDefault("scope"));
            }
        }

        return null;
    }

    /// <summary>
    /// <c>realm="…",service="…",scope="…"</c>. Split on commas outside quotes,
    /// because a scope legitimately contains them:
    /// <c>repository:acme/app:pull,push</c>.
    /// </summary>
    private static Dictionary<string, string> Fields(string parameter)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var quoted = false;
        var start = 0;

        for (var index = 0; index <= parameter.Length; index++)
        {
            if (index < parameter.Length)
            {
                if (parameter[index] == '"')
                {
                    quoted = !quoted;
                    continue;
                }

                if (parameter[index] != ',' || quoted)
                {
                    continue;
                }
            }

            Add(fields, parameter[start..index]);
            start = index + 1;
        }

        return fields;
    }

    private static void Add(Dictionary<string, string> fields, string pair)
    {
        var equals = pair.IndexOf('=');
        if (equals <= 0)
        {
            return;
        }

        fields[pair[..equals].Trim()] = pair[(equals + 1)..].Trim().Trim('"');
    }
}
