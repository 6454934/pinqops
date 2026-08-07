using System.Net;
using System.Text.Json;

namespace PinqOps.Web;

/// <summary>
/// Resolves a name through a public DNS-over-HTTPS resolver instead of this box's
/// own resolver.
///
/// <para><b>Why not just <c>Dns.GetHostAddressesAsync</c>.</b> Adding a domain
/// looks the name up before the record exists, and a caching resolver —
/// systemd-resolved on most of these hosts — then holds that NXDOMAIN for the
/// zone's SOA minimum, which is five minutes on Cloudflare. The record is written
/// a second later and is live at the authority immediately, but every lookup this
/// server makes keeps answering "no such name" for the rest of that window. The
/// ninety-second wait for DNS could therefore never succeed for a name that had
/// just been created: it was not waiting for propagation, it was waiting out a
/// negative cache entry it had created itself.</para>
///
/// <para>This is only consulted when the local resolver has nothing, so the normal
/// path stays a local lookup and no request leaves the box for a name that already
/// resolves.</para>
/// </summary>
public static class PublicDnsLookup
{
    /// <summary>
    /// Cloudflare first because it is authoritative for the zones pinqops writes to,
    /// so it sees a new record with no propagation delay at all; Google is there for
    /// when that host is blocked or down.
    /// </summary>
    private static readonly string[] Endpoints =
    [
        "https://cloudflare-dns.com/dns-query",
        "https://dns.google/resolve",
    ];

    private const int ARecord = 1;

    private const int AaaaRecord = 28;

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>
    /// The addresses a public resolver sees for <paramref name="domain"/>, or an
    /// empty list. Never throws: this answers "can the world see it yet", and every
    /// way of failing to find out means "not yet".
    /// </summary>
    public static Task<IReadOnlyList<string>> ResolveAsync(
        string domain, CancellationToken cancellationToken = default) =>
        ResolveAsync(Client, domain, cancellationToken);

    /// <summary>The same over a caller's client, so a test needs no network.</summary>
    public static async Task<IReadOnlyList<string>> ResolveAsync(
        HttpClient client, string domain, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(domain))
        {
            return [];
        }

        foreach (var endpoint in Endpoints)
        {
            foreach (var type in new[] { "A", "AAAA" })
            {
                var found = await QueryAsync(client, endpoint, domain, type, cancellationToken)
                    .ConfigureAwait(false);
                if (found.Count > 0)
                {
                    return found;
                }
            }
        }

        return [];
    }

    private static async Task<IReadOnlyList<string>> QueryAsync(
        HttpClient client, string endpoint, string domain, string type, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{endpoint}?name={Uri.EscapeDataString(domain)}&type={type}");
            // Cloudflare serves the JSON shape only for this Accept; Google ignores it.
            request.Headers.TryAddWithoutValidation("accept", "application/dns-json");

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Parse(body);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The addresses out of a DoH JSON answer. A CNAME chain arrives in the same
    /// list as type 5 records whose data is a name, so the type is checked rather
    /// than assuming every answer holds an address.
    /// </summary>
    internal static IReadOnlyList<string> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("Answer", out var answers)
            || answers.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var addresses = new List<string>();
        foreach (var answer in answers.EnumerateArray())
        {
            if (!answer.TryGetProperty("type", out var type)
                || !type.TryGetInt32(out var recordType)
                || recordType is not (ARecord or AaaaRecord))
            {
                continue;
            }

            if (answer.TryGetProperty("data", out var data)
                && data.GetString() is { Length: > 0 } address
                && IPAddress.TryParse(address, out var parsed))
            {
                addresses.Add(parsed.ToString());
            }
        }

        return addresses;
    }
}
