using System.Security.Cryptography;
using System.Text;

namespace PinqOps.Proxy;

/// <summary>What a rate limit counts requests by.</summary>
public static class RateLimitKeys
{
    /// <summary>One bucket per client address.</summary>
    public const string ClientAddress = "clientAddress";

    /// <summary>One bucket per value of a named request header — an API key, a tenant id.</summary>
    public const string Header = "header";

    public static bool IsValid(string? key) => key is ClientAddress or Header;
}

/// <summary>
/// A per-domain request ceiling.
///
/// <para><b>Two windows, not one.</b> A single limit has to choose between
/// stopping a burst and stopping a grind, and it cannot do both: set it low enough
/// to blunt a burst and ordinary page loads start failing; set it high enough for
/// a page load and someone can hold that rate all day. So a domain can have a
/// short window sized for what a browser does in a second, and a long one sized for
/// what a person does in an hour. Either alone is fine; the pair is what makes the
/// limit usable.</para>
///
/// <para><b>Off by default.</b> A ceiling that fires on a legitimate user is worse
/// than no ceiling, and pinqops has no idea what a given app's normal traffic looks
/// like. There is no safe number to guess.</para>
/// </summary>
public sealed class RateLimit
{
    public bool Enabled { get; set; }

    public string Key { get; set; } = RateLimitKeys.ClientAddress;

    /// <summary>The header to count by when <see cref="Key"/> is <c>header</c>.</summary>
    public string HeaderName { get; set; } = string.Empty;

    /// <summary>Requests allowed in the short window. Zero leaves that window out.</summary>
    public int BurstRequests { get; set; }

    public int BurstWindowSeconds { get; set; } = 1;

    /// <summary>Requests allowed in the long window. Zero leaves that window out.</summary>
    public int SustainedRequests { get; set; }

    public int SustainedWindowSeconds { get; set; } = 60;
}

/// <summary>
/// Renders the <c>rate_limit</c> block, from the module compiled into pinqops' own
/// Caddy build — stock Caddy has no rate limiting at all.
///
/// <para>The zone name is derived from the domain and never taken from the caller.
/// It is a Caddyfile identifier that has to be unique across the whole file, so a
/// caller-supplied one is both an injection surface and a way to make two domains
/// silently share a bucket.</para>
/// </summary>
public static class RateLimitRenderer
{
    public const int MaximumRequests = 1_000_000;

    /// <summary>An hour. Past that a "rate" is a quota, and this is the wrong tool.</summary>
    public const int MaximumWindowSeconds = 3600;

    /// <summary>
    /// The Caddy placeholder for the client's address as resolved through
    /// <c>trusted_proxies</c>.
    /// </summary>
    private const string ClientAddressPlaceholder = "{http.request.client_ip}";

    /// <summary>
    /// A header name safe to put inside a Caddy placeholder. Deliberately narrower
    /// than the HTTP grammar — the punctuation RFC 9110 allows in a token
    /// (<c>{</c>, <c>}</c>, <c>|</c>) is exactly what would break out of one.
    /// </summary>
    public static bool IsValidHeaderName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 64
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    /// <summary>How many hex characters of the domain's digest disambiguate a zone.</summary>
    private const int DomainDigestLength = 8;

    /// <summary>
    /// The zone identifier for one domain and window. Every character that is not a
    /// letter or digit becomes an underscore, so a domain can only ever produce a
    /// plain identifier — and a digest of the domain is appended, so two domains can
    /// never produce the same one.
    ///
    /// <para><b>Why the digest.</b> Sanitizing alone is not injective:
    /// <c>api-staging.example.com</c> and <c>api.staging.example.com</c> both flatten
    /// to <c>rl_api_staging_example_com</c>. Both spellings are valid domains, both
    /// site blocks land in one Caddyfile, and caddy-ratelimit keys its shared limiter
    /// state by zone name — so one host would spend the other's allowance and 429 its
    /// visitors, with nothing in a position to notice: each renderer here sees one
    /// domain at a time, so a collision cannot even be reported as a skip.</para>
    ///
    /// <para>Deterministic, so the same config regenerates the same file every
    /// time — the Caddyfile is compared and reloaded, not just written.</para>
    /// </summary>
    public static string ZoneName(string domain, string window)
    {
        ArgumentNullException.ThrowIfNull(domain);

        // Folded first, so the digest and the readable part agree on one spelling of
        // the domain — and so a zone name stays what it was for callers passing the
        // un-normalized form.
        var normalized = domain.ToLowerInvariant();

        var builder = new StringBuilder("rl_");
        foreach (var character in normalized)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) ? character : '_');
        }

        return builder.Append('_').Append(Digest(normalized)).Append('_').Append(window).ToString();
    }

    private static string Digest(string normalizedDomain) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedDomain)))[..DomainDigestLength];

    /// <summary>
    /// The block's lines, already indented one level inside the site block, or an
    /// empty list when the domain has no limit. Anything refused is reported rather
    /// than dropped: a limit the operator set and that is not being enforced is
    /// exactly the thing to hear about.
    /// </summary>
    public static IReadOnlyList<string> Render(
        RateLimit? limit, string domain, string domainLabel, List<CaddyfileSkip> skipped)
    {
        ArgumentNullException.ThrowIfNull(skipped);

        if (limit is null || !limit.Enabled)
        {
            return [];
        }

        if (!TryKey(limit, domainLabel, skipped, out var key))
        {
            return [];
        }

        var zones = new List<string>();
        AppendZone(zones, ZoneName(domain, "burst"), key, limit.BurstRequests, limit.BurstWindowSeconds);
        AppendZone(zones, ZoneName(domain, "sustained"), key, limit.SustainedRequests, limit.SustainedWindowSeconds);

        if (zones.Count == 0)
        {
            // Enabled with neither window set is a half-filled form, not a policy.
            // Emitting an empty rate_limit block would look enforced and enforce
            // nothing.
            skipped.Add(new CaddyfileSkip(
                $"{domainLabel} (rate limit)", "it is switched on but neither window has a request count"));
            return [];
        }

        var lines = new List<string> { "rate_limit {" };
        lines.AddRange(zones);
        lines.Add("}");
        return lines;
    }

    private static bool TryKey(RateLimit limit, string domainLabel, List<CaddyfileSkip> skipped, out string key)
    {
        key = string.Empty;

        if (string.Equals(limit.Key, RateLimitKeys.Header, StringComparison.Ordinal))
        {
            if (!IsValidHeaderName(limit.HeaderName))
            {
                skipped.Add(new CaddyfileSkip(
                    $"{domainLabel} (rate limit)",
                    $"'{limit.HeaderName}' is not a header name pinqops will count by"));
                return false;
            }

            key = $"{{http.request.header.{limit.HeaderName.Trim()}}}";
            return true;
        }

        if (!RateLimitKeys.IsValid(limit.Key))
        {
            skipped.Add(new CaddyfileSkip(
                $"{domainLabel} (rate limit)", $"'{limit.Key}' is not something pinqops counts requests by"));
            return false;
        }

        // The forwarded client address, not the immediate TCP peer.
        // {http.request.remote.host} is the socket's far end and trusted_proxies does
        // not touch it, so behind a CDN every visitor arriving through one edge node
        // shares a bucket — ordinary traffic is refused before the app hears about
        // it, and an abuser is indistinguishable from everyone else. client_ip
        // resolves through the trusted networks, and falls back to the remote address
        // when none are configured, so an install with no CDN counts what it always
        // counted.
        key = ClientAddressPlaceholder;
        return true;
    }

    /// <summary>
    /// One zone, or nothing when its request count is zero. Counts and windows are
    /// clamped rather than refused: a number that is too large is a typo, and
    /// silently enforcing no limit at all because of one is the worse failure.
    /// </summary>
    private static void AppendZone(List<string> zones, string name, string key, int requests, int windowSeconds)
    {
        if (requests <= 0)
        {
            return;
        }

        var events = Math.Min(requests, MaximumRequests);
        var window = Math.Clamp(windowSeconds, 1, MaximumWindowSeconds);

        zones.Add($"    zone {name} {{");
        zones.Add($"        key {key}");
        zones.Add($"        events {events}");
        zones.Add($"        window {window}s");
        zones.Add("    }");
    }
}
