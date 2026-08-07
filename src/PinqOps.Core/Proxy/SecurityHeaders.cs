namespace PinqOps.Proxy;

/// <summary>
/// The response headers the proxy adds to a domain.
///
/// <para><b>What is on by default, and why only these.</b> Four headers are sent
/// unless the domain turns them off, and each was chosen because it cannot
/// plausibly break an application that is behind a reverse proxy: content-type
/// sniffing off, framing limited to the same origin, the referrer trimmed to what
/// browsers already default to, and the three device permissions nothing needs by
/// accident. Anything that <em>can</em> break a site is opt-in.</para>
///
/// <para><b>Why HSTS is not on by default.</b> It is the one header here that is
/// sticky: once a browser has seen it, it refuses plain HTTP for the whole
/// max-age, and there is no way to reach those browsers and take it back. Turning
/// that on for somebody's existing domain during an upgrade is not a decision
/// pinqops gets to make for them.</para>
///
/// <para><b>Why there is no default Content-Security-Policy.</b> A useful CSP is a
/// description of one specific application — its scripts, its styles, its origins.
/// Any policy general enough to apply by default would break nearly every app that
/// has inline anything, and one loose enough not to would protect nothing.</para>
/// </summary>
public sealed class SecurityHeaders
{
    /// <summary>The four safe headers. Off sends none of them.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// <c>DENY</c>, <c>SAMEORIGIN</c> or <c>off</c>. Same-origin by default rather
    /// than deny: an app that frames its own pages is ordinary, and breaking it on
    /// upgrade to block a clickjacking vector that same-origin already blocks would
    /// be a bad trade.
    /// </summary>
    public string FrameOptions { get; set; } = FrameOptionsValues.SameOrigin;

    /// <summary>
    /// Defaults to what modern browsers already do, so switching this on changes
    /// nothing for a site that never thought about it — while a site that wants the
    /// referrer stripped entirely can say so.
    /// </summary>
    public string ReferrerPolicy { get; set; } = "strict-origin-when-cross-origin";

    public string PermissionsPolicy { get; set; } = "camera=(), microphone=(), geolocation=()";

    /// <summary>Days of HSTS. Zero — the default — sends no header at all.</summary>
    public int StrictTransportSecurityDays { get; set; }

    /// <summary>
    /// Whether HSTS covers subdomains. Off by default: it commits every name under
    /// the domain, including ones that do not exist yet and may never be served
    /// over HTTPS.
    /// </summary>
    public bool StrictTransportSecuritySubdomains { get; set; }

    /// <summary>Sent verbatim when set. Empty — the default — sends nothing.</summary>
    public string ContentSecurityPolicy { get; set; } = string.Empty;
}

/// <summary>The values <see cref="SecurityHeaders.FrameOptions"/> accepts.</summary>
public static class FrameOptionsValues
{
    public const string Deny = "DENY";

    public const string SameOrigin = "SAMEORIGIN";

    /// <summary>Send no <c>X-Frame-Options</c> at all.</summary>
    public const string Off = "off";

    public static bool IsValid(string? value) =>
        value is not null
        && (string.Equals(value, Deny, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, SameOrigin, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, Off, StringComparison.OrdinalIgnoreCase));

    /// <summary>Folds to the canonical spelling; anything unrecognised becomes the default.</summary>
    public static string Normalize(string? value) =>
        string.Equals(value, Deny, StringComparison.OrdinalIgnoreCase) ? Deny
        : string.Equals(value, Off, StringComparison.OrdinalIgnoreCase) ? Off
        : SameOrigin;
}

/// <summary>
/// Renders and validates the header block.
///
/// <para>Every value here ends up inside a Caddyfile that Caddy executes, and
/// <c>domains.json</c> is written by two processes and read without validation — so
/// nothing reaches the file that has not been checked. Enumerated values fall back
/// to the default, numbers are clamped, and the two free-text headers are refused
/// outright if they carry anything that could close the block or the quoted string
/// they sit in.</para>
/// </summary>
public static class SecurityHeaderRenderer
{
    /// <summary>The eight values the Referrer-Policy specification defines.</summary>
    private static readonly string[] ReferrerPolicies =
    [
        "no-referrer",
        "no-referrer-when-downgrade",
        "origin",
        "origin-when-cross-origin",
        "same-origin",
        "strict-origin",
        "strict-origin-when-cross-origin",
        "unsafe-url",
    ];

    /// <summary>
    /// Two years, the longest any preload list asks for. Past that a typo is far
    /// more likely than an intention, and the cost of a typo is a domain that
    /// browsers refuse over plain HTTP for longer than the mistake takes to notice.
    /// </summary>
    public const int MaximumStrictTransportSecurityDays = 730;

    public const int MaximumPolicyLength = 2048;

    public static bool IsValidReferrerPolicy(string? value) =>
        value is not null
        && Array.Exists(ReferrerPolicies, known => string.Equals(known, value, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a free-text header value can be emitted. It goes inside a
    /// double-quoted Caddyfile token, so a quote would close it and a brace or a
    /// newline would escape into the surrounding block.
    /// </summary>
    public static bool IsSafeHeaderValue(string? value) =>
        value is not null
        && value.Length <= MaximumPolicyLength
        && !value.Any(character =>
            char.IsControl(character) || character is '"' or '{' or '}' or '\\');

    /// <summary>
    /// The header directives for one domain, already indented, or an empty list
    /// when there is nothing to send. Anything refused is reported through
    /// <paramref name="skipped"/> rather than dropped in silence: a policy the
    /// operator set and that is not being sent is worth knowing about.
    /// </summary>
    public static IReadOnlyList<string> Render(
        SecurityHeaders? settings, string domainLabel, List<CaddyfileSkip> skipped)
    {
        ArgumentNullException.ThrowIfNull(skipped);

        // Absent means the defaults, which is what makes an existing domains.json
        // pick these up without anything having to rewrite it.
        settings ??= new SecurityHeaders();
        if (!settings.Enabled)
        {
            return [];
        }

        var lines = new List<string> { "X-Content-Type-Options nosniff" };

        var frameOptions = FrameOptionsValues.Normalize(settings.FrameOptions);
        if (!string.Equals(frameOptions, FrameOptionsValues.Off, StringComparison.Ordinal))
        {
            lines.Add($"X-Frame-Options {frameOptions}");
        }

        // An unrecognised policy falls back rather than being skipped: sending the
        // browser default is safer than sending nothing at all.
        var referrerPolicy = IsValidReferrerPolicy(settings.ReferrerPolicy)
            ? settings.ReferrerPolicy
            : "strict-origin-when-cross-origin";
        lines.Add($"Referrer-Policy {referrerPolicy}");

        Add(lines, "Permissions-Policy", settings.PermissionsPolicy, domainLabel, skipped);

        if (settings.StrictTransportSecurityDays > 0)
        {
            var days = Math.Min(settings.StrictTransportSecurityDays, MaximumStrictTransportSecurityDays);
            var value = $"max-age={days * 86400}"
                + (settings.StrictTransportSecuritySubdomains ? "; includeSubDomains" : string.Empty);
            lines.Add($"Strict-Transport-Security \"{value}\"");
        }

        Add(lines, "Content-Security-Policy", settings.ContentSecurityPolicy, domainLabel, skipped);

        return lines;
    }

    private static void Add(
        List<string> lines, string header, string? value, string domainLabel, List<CaddyfileSkip> skipped)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!IsSafeHeaderValue(value))
        {
            skipped.Add(new CaddyfileSkip(
                $"{domainLabel} ({header})",
                "the value contains a quote, a brace, a backslash or a control character"));
            return;
        }

        lines.Add($"{header} \"{value.Trim()}\"");
    }
}
