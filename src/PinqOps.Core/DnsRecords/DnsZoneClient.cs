namespace PinqOps.DnsRecords;

/// <summary>One address record in a zone.</summary>
public sealed record DnsRecord(string Id, string Name, string Address);

/// <summary>
/// Creating and removing the address record that points a domain at this server.
///
/// <para><b>Separate from the DNS-01 challenge.</b> Caddy answers a challenge by
/// writing a TXT record with its own compiled-in module, and it does that for three
/// providers. This is pinqops writing the A record that makes the domain resolve
/// here in the first place — a different job, and one only implemented for
/// Cloudflare so far. A provider that can do one and not the other is not a
/// contradiction; it just means the record is added by hand.</para>
/// </summary>
public interface DnsZoneClient
{
    /// <summary>The provider this talks to, as <c>DnsProviders</c> names it.</summary>
    string Provider { get; }

    /// <summary>The address records for a name, or empty when there are none.</summary>
    Task<IReadOnlyList<DnsRecord>> Find(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Points <paramref name="name"/> at <paramref name="address"/>, replacing an
    /// existing record rather than adding a second one — two A records for one name
    /// is a round-robin nobody asked for.
    /// </summary>
    /// <param name="proxied">
    /// When true (the default operators want for Cloudflare), the record is orange-cloud
    /// proxied. When false it is DNS-only. Proxied terminates visitor TLS at the CDN;
    /// the origin still needs its own certificate for Full / Full Strict, and edge mode
    /// must be on so logs and rate limits see the visitor rather than the CDN.
    /// </param>
    Task<DnsRecord> Point(
        string name, string address, bool proxied = true, CancellationToken cancellationToken = default);

    /// <summary>Removes a record by the id <see cref="Find"/> reported.</summary>
    Task Remove(string recordId, CancellationToken cancellationToken = default);
}

/// <summary>What a provider said when it refused.</summary>
public sealed class DnsProviderException : Exception
{
    public DnsProviderException(string message)
        : base(message)
    {
    }

    public DnsProviderException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
