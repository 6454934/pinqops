using Microsoft.Extensions.Logging;
using PinqOps.DnsRecords;
using PinqOps.Proxy;
using PinqOps.Secrets;

namespace PinqOps.Web;

/// <summary>
/// Writes the address record that makes a domain resolve to this server.
///
/// <para><b>Only when a provider is configured, and only Cloudflare so far.</b>
/// With none — or with one pinqops can answer a DNS-01 challenge for but cannot
/// write records through — nothing here runs and the operator adds the record by
/// hand, exactly as before. The DNS preflight on the Domains page is what tells
/// them whether it worked, and that has not changed.</para>
/// </summary>
public sealed class DnsRecordService
{
    /// <summary>
    /// Contabo and similar hosts often take longer than 20s to reach
    /// api.cloudflare.com under load; a short timeout looked like "Cloudflare is
    /// down" when the request was still in flight.
    /// </summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly ProxyService _proxy;
    private readonly SecretStore _secrets;
    private readonly ILogger<DnsRecordService> _logger;

    public DnsRecordService(ProxyService proxy, SecretStore secrets, ILogger<DnsRecordService> logger)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(logger);
        _proxy = proxy;
        _secrets = secrets;
        _logger = logger;
    }

    /// <summary>
    /// Whether records can be written right now, and why not when they cannot. The
    /// dashboard asks so it can offer the button instead of offering it and failing.
    /// </summary>
    public (bool Available, string Reason) Availability() => AvailabilityOf(_proxy.Store.Load().Dns);

    /// <summary>
    /// The same answer from settings alone, so the proxy status can report it
    /// without depending on this service — which depends on the proxy.
    /// </summary>
    public static (bool Available, string Reason) AvailabilityOf(DnsChallenge? dns)
    {
        if (dns is null || !dns.IsUsable())
        {
            return (false, "No DNS provider is configured, so records have to be added by hand.");
        }

        return string.Equals(dns.Provider, DnsProviders.Cloudflare, StringComparison.Ordinal)
            ? (true, string.Empty)
            : (false, $"pinqops can answer a certificate challenge through {dns.Provider} but cannot write its records yet.");
    }

    /// <summary>
    /// Points <paramref name="domain"/> at this server's public address. Returns
    /// the record and the address it was set to.
    /// </summary>
    /// <param name="proxied">
    /// Cloudflare orange cloud when true (default). DNS-only when false. A proxied
    /// write also turns edge mode on when it is off, because without trusted CDN
    /// ranges every log and rate limit would see the CDN instead of the visitor.
    /// </param>
    public async Task<DnsRecord> Point(
        string domain, bool proxied = true, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "DNS point starting for {Domain} (proxied={Proxied})", domain, proxied);

        var client = Client();
        _logger.LogInformation("DNS point: resolving this server's public address");
        var address = await _proxy.PublicAddressAsync()
            ?? throw new InvalidOperationException(
                "This server's public address could not be worked out (public-IP lookup failed), "
                + "so there is nothing to point the domain at.");

        _logger.LogInformation("DNS point: public address is {Address}", address);

        // A wildcard record is a legitimate thing to write, and Cloudflare accepts
        // the name verbatim, so nothing here has to special-case it.
        DnsRecord record;
        try
        {
            record = await client
                .Point(DomainName.NormalizeForLookup(domain), address, proxied, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DnsProviderException exception)
        {
            _logger.LogWarning(exception, "DNS point failed for {Domain}: {Message}", domain, exception.Message);
            throw;
        }

        if (proxied)
        {
            _logger.LogInformation("DNS point: ensuring edge mode for proxied Cloudflare traffic");
            await _proxy.EnsureEdgeEnabledForProxiedDnsAsync(cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "DNS point succeeded for {Domain} → {Address} (proxied={Proxied})",
            record.Name, record.Address, proxied);
        return record;
    }

    /// <summary>The records currently pointing a domain anywhere.</summary>
    public Task<IReadOnlyList<DnsRecord>> Find(string domain, CancellationToken cancellationToken = default) =>
        Client().Find(DomainName.NormalizeForLookup(domain), cancellationToken);

    /// <summary>Removes every address record for a domain. Returns how many went.</summary>
    public async Task<int> Remove(string domain, CancellationToken cancellationToken = default)
    {
        var client = Client();
        var records = await client.Find(DomainName.NormalizeForLookup(domain), cancellationToken).ConfigureAwait(false);
        foreach (var record in records)
        {
            await client.Remove(record.Id, cancellationToken).ConfigureAwait(false);
        }

        return records.Count;
    }

    private DnsZoneClient Client()
    {
        var (available, reason) = Availability();
        if (!available)
        {
            throw new InvalidOperationException(reason);
        }

        var dns = _proxy.Store.Load().Dns!;
        string token;
        try
        {
            token = _secrets.Reveal(SecretScopes.Global, dns.SecretName, version: null).Value;
        }
        catch (KeyNotFoundException exception)
        {
            throw new InvalidOperationException(
                $"The DNS challenge names the secret '{dns.SecretName}', which does not exist. "
                + "Add it under Secrets first.",
                exception);
        }
        catch (ArgumentException exception)
        {
            // Same split as the proxy makes for the same secret: "does not exist"
            // would send the operator to add a name the vault is going to refuse.
            throw new InvalidOperationException(
                $"The DNS challenge names '{dns.SecretName}', which is not a usable secret name. "
                + "Use letters, digits and underscores.",
                exception);
        }

        // One shared HttpClient, a fresh wrapper per call. The wrapper never touches
        // the client's default headers — the token rides each request — so sharing
        // it cannot leak one caller's credential into another's.
        return new CloudflareDnsClient(
            Http,
            token,
            message => _logger.LogInformation("{Message}", message),
            zoneId: dns.ZoneId,
            accountId: dns.AccountId);
    }
}
