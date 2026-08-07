namespace PinqOps.Proxy;

/// <summary>
/// The DNS-01 challenge settings, which is what makes a wildcard certificate
/// possible.
///
/// <para><b>Why a wildcard needs this at all.</b> The HTTP-01 challenge proves
/// control of a name by serving a file at that name — which cannot be done for
/// <c>*.example.com</c>, because there is no such host to serve anything from. The
/// only proof that covers a wildcard is a DNS record, so a provider pinqops can
/// write records with has to be configured before a wildcard can be asked for.</para>
///
/// <para><b>Where the token lives.</b> Not here. This holds the <em>name</em> of a
/// secret in the vault; the value is resolved when the proxy container is created
/// and passed to it as an environment variable. The Caddyfile is regenerated
/// constantly and sits on disk next to a config two processes write — not a place
/// to put a credential that can edit an entire DNS zone.</para>
/// </summary>
public sealed class DnsChallenge
{
    public bool Enabled { get; set; }

    /// <summary>One of <see cref="DnsProviders.All"/>.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>The name of the vault secret holding the provider's API token.</summary>
    public string SecretName { get; set; } = string.Empty;

    /// <summary>
    /// Optional Cloudflare zone id. When set, pinqops skips the
    /// <c>GET /zones?name=…</c> walk and writes records in this zone directly —
    /// useful when that lookup is slow or times out. Does not grant DNS write;
    /// the API token still needs Zone DNS Edit.
    /// </summary>
    public string ZoneId { get; set; } = string.Empty;

    /// <summary>
    /// Optional Cloudflare account id. When set (and <see cref="ZoneId"/> is
    /// empty), zone name lookups are narrowed with <c>account.id</c>.
    /// </summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>
    /// Whether this is complete enough to attempt a DNS-01 challenge. A provider
    /// nobody configured cannot answer one, and a wildcard asked for anyway would
    /// fail at issuance rather than at the point somebody could fix it.
    /// </summary>
    public bool IsUsable() =>
        Enabled && DnsProviders.IsKnown(Provider) && !string.IsNullOrWhiteSpace(SecretName);
}

/// <summary>
/// The DNS providers pinqops' Caddy build has modules for.
///
/// <para>Deliberately short. Every provider is a module compiled into the image,
/// and each is one more thing that can break the proxy every domain on the server
/// depends on — so one is added when somebody needs it, not because it exists.</para>
/// </summary>
public static class DnsProviders
{
    public const string Cloudflare = "cloudflare";

    public const string Route53 = "route53";

    public const string DigitalOcean = "digitalocean";

    public static readonly string[] All = [Cloudflare, Route53, DigitalOcean];

    public static bool IsKnown(string? provider) =>
        provider is not null && Array.Exists(All, known => string.Equals(known, provider, StringComparison.Ordinal));

    /// <summary>
    /// The environment variable the proxy container carries the token in. One name
    /// whatever the provider is, so the Caddyfile does not have to change when the
    /// provider does — and so there is exactly one thing to clear when it is
    /// removed.
    /// </summary>
    public const string TokenVariable = "PINQOPS_DNS_API_TOKEN";
}
