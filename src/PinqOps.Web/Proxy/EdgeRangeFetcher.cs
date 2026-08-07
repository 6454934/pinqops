using System.Net;

namespace PinqOps.Web;

/// <summary>
/// Fetches the networks a CDN sends traffic from.
///
/// <para>Fetched and stored rather than compiled in, because the list changes and a
/// hardcoded copy goes stale silently — the failure being that a range the CDN
/// started using is not trusted, so requests through it look like they came from
/// nowhere in particular. Stored rather than fetched at render time, because
/// generating a Caddyfile must not depend on the network.</para>
/// </summary>
public sealed class EdgeRangeFetcher
{
    /// <summary>Cloudflare publishes both lists as plain text, one network per line.</summary>
    private static readonly string[] Sources =
    [
        "https://www.cloudflare.com/ips-v4",
        "https://www.cloudflare.com/ips-v6",
    ];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// The current list. Throws rather than returning a partial one: half a trust
    /// list is worse than none, because the missing half looks like untrusted
    /// traffic and its forwarded headers would be ignored.
    /// </summary>
    public static async Task<IReadOnlyList<string>> CloudflareRanges(CancellationToken cancellationToken = default)
    {
        var ranges = new List<string>();
        foreach (var source in Sources)
        {
            string body;
            try
            {
                body = await Http.GetStringAsync(source, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                throw new InvalidOperationException(
                    $"Could not fetch Cloudflare's address ranges from {source}. "
                    + "Check this server's outbound access and try again.");
            }

            foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // Validated here as well as in the generator: a published list that
                // has changed shape should fail at the fetch, where the message can
                // say so, rather than as a Caddyfile that will not load.
                if (IPNetwork.TryParse(line, out _))
                {
                    ranges.Add(line);
                }
            }
        }

        return ranges.Count > 0
            ? ranges
            : throw new InvalidOperationException("Cloudflare's published ranges came back empty.");
    }
}
