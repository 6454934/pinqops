using System.Net;

namespace PinqOps.Web;

/// <summary>
/// The reverse-proxy hops whose <c>X-Forwarded-For</c> the dashboard will believe.
///
/// Behind a proxy every request arrives from the proxy's own address, which
/// collapses the per-client login throttle and rate limiter into a single shared
/// bucket — one attacker's failures would lock out everyone. Reading the real
/// client from the header fixes that, but only where the operator has said which
/// hops to trust: without that, any caller could set the header and choose its
/// own throttle bucket. So the list is opt-in and the header is ignored when it
/// is empty.
/// </summary>
/// <param name="Addresses">Individual proxy addresses (e.g. <c>127.0.0.1</c>).</param>
/// <param name="Networks">Proxy networks in CIDR form (e.g. <c>10.0.0.0/8</c>).</param>
/// <param name="Invalid">Entries that parsed as neither, kept so startup can say so.</param>
public sealed record TrustedProxyList(
    IReadOnlyList<IPAddress> Addresses,
    IReadOnlyList<IPNetwork> Networks,
    IReadOnlyList<string> Invalid)
{
    public bool IsEmpty => Addresses.Count == 0 && Networks.Count == 0;
}

public static class TrustedProxies
{
    /// <summary>
    /// Parses a comma- or space-separated list of addresses and CIDR ranges.
    /// Unparseable entries are collected rather than thrown, so one typo cannot
    /// stop the dashboard from starting — it starts without trusting that entry,
    /// which is the safe direction.
    /// </summary>
    public static TrustedProxyList Parse(string? value)
    {
        var addresses = new List<IPAddress>();
        var networks = new List<IPNetwork>();
        var invalid = new List<string>();

        foreach (var entry in (value ?? string.Empty).Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entry.Contains('/'))
            {
                if (IPNetwork.TryParse(entry, out var network))
                {
                    networks.Add(network);
                }
                else
                {
                    invalid.Add(entry);
                }
            }
            else if (IPAddress.TryParse(entry, out var address))
            {
                addresses.Add(address);
            }
            else
            {
                invalid.Add(entry);
            }
        }

        return new TrustedProxyList(addresses, networks, invalid);
    }
}
