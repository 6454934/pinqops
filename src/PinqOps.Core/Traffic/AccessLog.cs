using System.Globalization;
using System.Text.Json;

namespace PinqOps.Traffic;

/// <summary>One request, as the proxy recorded it.</summary>
/// <param name="Country">
/// Only ever set when the request carried a country header. pinqops embeds no GeoIP
/// database and makes no per-request lookup, so this is present behind a CDN that
/// adds one and absent otherwise.
/// </param>
public sealed record AccessEntry(
    DateTimeOffset At,
    string Host,
    string Route,
    int Status,
    long Bytes,
    double DurationSeconds,
    string? Country);

/// <summary>
/// Reads Caddy's JSON access log.
///
/// <para><b>One line at a time, and a bad one is skipped.</b> The file is being
/// appended to while it is read, so the last line is routinely half-written — and a
/// parser that threw on it would fail every time it ran on a busy server.</para>
/// </summary>
public static class AccessLog
{
    /// <summary>The headers a CDN uses to report the client's country, in the order they are believed.</summary>
    private static readonly string[] CountryHeaders = ["CF-IPCountry", "CloudFront-Viewer-Country", "X-Country-Code"];

    /// <summary>The entry in one log line, or null when the line is not one.</summary>
    public static AccessEntry? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (!root.TryGetProperty("request", out var request))
            {
                return null;
            }

            // Caddy writes ts as a float of Unix seconds by default.
            var at = root.TryGetProperty("ts", out var ts) && ts.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)(ts.GetDouble() * 1000))
                : DateTimeOffset.UtcNow;

            var host = Text(request, "host") ?? string.Empty;
            var uri = Text(request, "uri") ?? "/";

            return new AccessEntry(
                at,
                // The host carries the port when the request came in on one; the
                // domain is what anyone is asking about.
                host.Split(':')[0],
                RouteKey.Normalize(uri),
                root.TryGetProperty("status", out var status) && status.TryGetInt32(out var code) ? code : 0,
                root.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0,
                root.TryGetProperty("duration", out var duration) && duration.ValueKind == JsonValueKind.Number
                    ? duration.GetDouble()
                    : 0,
                CountryOf(request));
        }
        catch (JsonException)
        {
            // A half-written last line, which is the normal state of a file being
            // appended to while it is read.
            return null;
        }
    }

    private static string? CountryOf(JsonElement request)
    {
        if (!request.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in CountryHeaders)
        {
            foreach (var header in headers.EnumerateObject())
            {
                if (!string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase)
                    || header.Value.ValueKind != JsonValueKind.Array
                    || header.Value.GetArrayLength() == 0)
                {
                    continue;
                }

                var value = header.Value[0].GetString();
                // Two letters or nothing. A CDN reports XX for "unknown", and a
                // header from anywhere else is not something to render as a country.
                return value is { Length: 2 } && value.All(char.IsAsciiLetter)
                    ? value.ToUpperInvariant()
                    : null;
            }
        }

        return null;
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// Turns a request path into something worth counting.
///
/// <para><b>Why not the path itself.</b> <c>/orders/1041</c> and <c>/orders/1042</c>
/// are the same route and two different paths. Counting paths gives a top-routes
/// list where every entry has one hit, and a rollup whose size grows with traffic
/// rather than with the application.</para>
/// </summary>
public static class RouteKey
{
    public const int MaximumSegments = 8;

    public const string Placeholder = "{id}";

    /// <summary>
    /// The path with identifier-looking segments replaced, the query dropped, and
    /// the depth bounded.
    /// </summary>
    public static string Normalize(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        // The query is where the cardinality really is — a cache-buster alone would
        // make every request its own route.
        var path = uri.Split('?')[0].Split('#')[0];
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
        {
            return "/";
        }

        var kept = segments
            .Take(MaximumSegments)
            .Select(segment => LooksLikeAnIdentifier(segment) ? Placeholder : segment.ToLowerInvariant());

        var normalized = "/" + string.Join('/', kept);
        return segments.Length > MaximumSegments ? normalized + "/…" : normalized;
    }

    /// <summary>
    /// A segment that is a value rather than a name: all digits, a UUID, or a long
    /// hex string. Deliberately conservative — a slug like <c>my-first-post</c> is
    /// left alone, because collapsing it would hide a route somebody wrote.
    /// </summary>
    private static bool LooksLikeAnIdentifier(string segment)
    {
        if (segment.Length == 0)
        {
            return false;
        }

        if (segment.All(char.IsAsciiDigit))
        {
            return true;
        }

        if (Guid.TryParse(segment, out _))
        {
            return true;
        }

        return segment.Length >= 16 && segment.All(character => char.IsAsciiHexDigit(character));
    }
}
