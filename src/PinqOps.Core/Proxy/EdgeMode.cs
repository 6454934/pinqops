using System.Net;

namespace PinqOps.Proxy;

/// <summary>
/// Running behind a CDN that terminates TLS and forwards requests — Cloudflare
/// today.
///
/// <para><b>This is not a CDN, and it does not make pinqops one.</b> A content
/// delivery network is a fleet of machines in many places answering from the one
/// nearest each visitor; that cannot be built on one server, and nothing here
/// pretends otherwise. What this does is make pinqops correct when somebody has
/// put a CDN in front of it.</para>
///
/// <para><b>Why it is not cosmetic.</b> Behind a proxy every request arrives from
/// the proxy's address. Without being told which addresses to believe, Caddy sees
/// one client — so a rate limit buckets the entire internet together, an access log
/// records the CDN instead of the visitor, and any country header could have been
/// written by anyone. Naming the CDN's ranges is what makes all three mean
/// something again.</para>
/// </summary>
public sealed class EdgeMode
{
    public bool Enabled { get; set; }

    /// <summary>
    /// The networks whose <c>X-Forwarded-For</c> is believed. Stored rather than
    /// fetched at render time, so generating a Caddyfile never depends on the
    /// network — and so what is trusted is visible in the config rather than
    /// implied.
    /// </summary>
    public List<string> TrustedRanges { get; set; } = [];

    public DateTimeOffset RangesUpdatedAt { get; set; }

    /// <summary>
    /// How long a CDN may cache a static asset. Zero — the default — sends no
    /// <c>Cache-Control</c> and leaves the app's own headers alone, which is the
    /// only safe default: pinqops does not know which of an app's paths are
    /// genuinely immutable.
    /// </summary>
    public int StaticCacheSeconds { get; set; }
}

/// <summary>Renders the edge-mode parts of a Caddyfile.</summary>
public static class EdgeModeRenderer
{
    /// <summary>
    /// A year. Longer than any sensible asset lifetime and the value every
    /// "cache-forever" guide uses; past it the number is a typo.
    /// </summary>
    public const int MaximumStaticCacheSeconds = 31_536_000;

    /// <summary>
    /// How many ranges will be emitted. Cloudflare publishes about two dozen; a
    /// list far longer than that is a fetch that went wrong, and putting it in the
    /// file would trust networks nobody chose.
    /// </summary>
    public const int MaximumRanges = 128;

    /// <summary>
    /// Extensions treated as static assets. Deliberately a fixed list of things
    /// that are content-addressed or versioned in practice — not a pattern the
    /// operator writes, because a matcher that accidentally covers an HTML page
    /// caches a logged-in view at the edge.
    /// </summary>
    private static readonly string[] StaticExtensions =
    [
        "*.css", "*.js", "*.mjs", "*.woff", "*.woff2", "*.ttf", "*.otf",
        "*.png", "*.jpg", "*.jpeg", "*.gif", "*.svg", "*.webp", "*.avif", "*.ico",
    ];

    /// <summary>
    /// The valid, de-duplicated ranges from <paramref name="edge"/>. Anything that
    /// is not a network is dropped and reported: a malformed entry in
    /// <c>trusted_proxies</c> makes Caddy refuse the whole file, which would take
    /// every domain down.
    /// </summary>
    public static IReadOnlyList<string> TrustedRanges(EdgeMode? edge, List<CaddyfileSkip> skipped)
    {
        ArgumentNullException.ThrowIfNull(skipped);

        if (edge is null || !edge.Enabled)
        {
            return [];
        }

        var ranges = new List<string>();
        foreach (var range in edge.TrustedRanges)
        {
            var trimmed = (range ?? string.Empty).Trim();
            if (!IPNetwork.TryParse(trimmed, out _))
            {
                skipped.Add(new CaddyfileSkip("edge mode", $"'{range}' is not a network in CIDR form"));
                continue;
            }

            if (!ranges.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                ranges.Add(trimmed);
            }

            if (ranges.Count == MaximumRanges)
            {
                skipped.Add(new CaddyfileSkip(
                    "edge mode", $"only the first {MaximumRanges} trusted networks are used"));
                break;
            }
        }

        return ranges;
    }

    /// <summary>
    /// The global <c>servers</c> block naming the networks whose forwarded headers
    /// are believed, or empty when edge mode is off.
    /// </summary>
    public static IReadOnlyList<string> ServersBlock(IReadOnlyList<string> trustedRanges)
    {
        ArgumentNullException.ThrowIfNull(trustedRanges);
        if (trustedRanges.Count == 0)
        {
            return [];
        }

        return
        [
            "servers {",
            "    trusted_proxies static " + string.Join(' ', trustedRanges),
            "}",
        ];
    }

    /// <summary>
    /// The cache header for static assets, as lines inside a site block. Empty when
    /// no lifetime is set, which leaves the app's own headers untouched.
    /// </summary>
    public static IReadOnlyList<string> StaticCache(EdgeMode? edge)
    {
        if (edge is null || !edge.Enabled || edge.StaticCacheSeconds <= 0)
        {
            return [];
        }

        var seconds = Math.Min(edge.StaticCacheSeconds, MaximumStaticCacheSeconds);
        return
        [
            "@pinqops_static path " + string.Join(' ', StaticExtensions),
            $"header @pinqops_static Cache-Control \"public, max-age={seconds}\"",
        ];
    }
}
