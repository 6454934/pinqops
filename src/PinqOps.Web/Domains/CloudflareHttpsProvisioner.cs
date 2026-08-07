using Microsoft.Extensions.Logging;
using PinqOps.DnsRecords;
using PinqOps.Proxy;

namespace PinqOps.Web;

/// <summary>
/// Orders Cloudflare DNS and Caddy ACME so Let's Encrypt sees a public A record
/// before HTTP-01 runs, then flips the record to Proxied once a certificate answers
/// on localhost:443. Operators never have to grey-cloud by hand.
/// </summary>
public sealed class CloudflareHttpsProvisioner
{
    private static readonly TimeSpan DefaultDnsTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan DefaultTlsTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan DefaultDnsPoll = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultTlsPoll = TimeSpan.FromSeconds(3);

    private readonly Func<string, bool, CancellationToken, Task<DnsRecord>> _point;
    private readonly Func<string, CancellationToken, Task<DnsCheckResult>> _checkDns;
    private readonly Func<string, CancellationToken, Task> _releaseAndApply;
    private readonly Func<string, CancellationToken, Task<TlsProbeResult>> _probe;
    private readonly Func<string, bool> _isWildcard;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _logger;
    private readonly TimeSpan _dnsTimeout;
    private readonly TimeSpan _tlsTimeout;
    private readonly TimeSpan _dnsPoll;
    private readonly TimeSpan _tlsPoll;

    public CloudflareHttpsProvisioner(
        DnsRecordService dns,
        ProxyService proxy,
        ILogger<CloudflareHttpsProvisioner> logger)
        : this(
            (domain, proxied, ct) => dns.Point(domain, proxied, ct),
            // Public resolver, not this box's: the preflight on the way in cached the
            // name as NXDOMAIN before the record existed, and that entry outlives the
            // whole DNS wait.
            (domain, ct) => proxy.CheckDnsSeenPubliclyAsync(
                domain, allowWildcard: DomainName.IsWildcard(domain), ct),
            async (domain, ct) =>
            {
                ReleaseProxyDeferred(proxy, domain);
                await proxy.ApplyAsync().ConfigureAwait(false);
            },
            DomainTlsService.ProbeAsync,
            DomainName.IsWildcard,
            Task.Delay,
            logger,
            DefaultDnsTimeout,
            DefaultTlsTimeout,
            DefaultDnsPoll,
            DefaultTlsPoll)
    {
    }

    /// <summary>Test seam: inject steps and clocks without Docker or Cloudflare.</summary>
    internal CloudflareHttpsProvisioner(
        Func<string, bool, CancellationToken, Task<DnsRecord>> point,
        Func<string, CancellationToken, Task<DnsCheckResult>> checkDns,
        Func<string, CancellationToken, Task> releaseAndApply,
        Func<string, CancellationToken, Task<TlsProbeResult>> probe,
        Func<string, bool> isWildcard,
        Func<TimeSpan, CancellationToken, Task> delay,
        ILogger logger,
        TimeSpan dnsTimeout,
        TimeSpan tlsTimeout,
        TimeSpan dnsPoll,
        TimeSpan tlsPoll)
    {
        ArgumentNullException.ThrowIfNull(point);
        ArgumentNullException.ThrowIfNull(checkDns);
        ArgumentNullException.ThrowIfNull(releaseAndApply);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(isWildcard);
        ArgumentNullException.ThrowIfNull(delay);
        ArgumentNullException.ThrowIfNull(logger);

        _point = point;
        _checkDns = checkDns;
        _releaseAndApply = releaseAndApply;
        _probe = probe;
        _isWildcard = isWildcard;
        _delay = delay;
        _logger = logger;
        _dnsTimeout = dnsTimeout;
        _tlsTimeout = tlsTimeout;
        _dnsPoll = dnsPoll;
        _tlsPoll = tlsPoll;
    }

    /// <summary>
    /// When <paramref name="preferProxied"/> is true, ends on orange cloud after a
    /// live certificate. Ordinary names use a temporary DNS-only record so HTTP-01
    /// can reach the origin; wildcards (DNS-01) skip the grey step and flip Proxied
    /// only after the certificate answers.
    /// </summary>
    /// <param name="progress">
    /// Optional phase reporter for the dashboard (<see cref="DomainProvisionPhases"/>).
    /// </param>
    public async Task<CloudflareHttpsProvisionResult> ProvisionAsync(
        string domain,
        bool preferProxied = true,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);

        if (_isWildcard(domain))
        {
            return await ProvisionWildcardAsync(domain, preferProxied, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        return await ProvisionOrdinaryAsync(domain, preferProxied, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CloudflareHttpsProvisionResult> ProvisionOrdinaryAsync(
        string domain,
        bool preferProxied,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        string? address = null;
        try
        {
            progress?.Report(DomainProvisionPhases.WritingDns);
            _logger.LogInformation(
                "HTTPS provision {Domain}: writing DNS-only A record (temporary grey cloud)", domain);
            var dnsOnly = await _point(domain, false, cancellationToken).ConfigureAwait(false);
            address = dnsOnly.Address;
        }
        catch (Exception exception) when (exception is DnsProviderException or InvalidOperationException)
        {
            _logger.LogWarning(
                exception, "HTTPS provision {Domain}: DNS-only Point failed: {Message}",
                domain, exception.Message);
            return new CloudflareHttpsProvisionResult(
                DnsOnlyOk: false, DnsReady: false, CertReady: false, Proxied: false,
                Address: null, Error: exception.Message);
        }

        progress?.Report(DomainProvisionPhases.WaitingDns);
        _logger.LogInformation("HTTPS provision {Domain}: waiting for public DNS to match this server", domain);
        if (!await WaitUntilAsync(
                () => DnsMatchesOriginAsync(domain, cancellationToken),
                _dnsTimeout, _dnsPoll, cancellationToken).ConfigureAwait(false))
        {
            var error =
                "DNS was written (DNS-only) but has not propagated yet — certificate and Proxied "
                + "were not finished. Use Point here to retry.";
            _logger.LogWarning("HTTPS provision {Domain}: {Error}", domain, error);
            return new CloudflareHttpsProvisionResult(
                DnsOnlyOk: true, DnsReady: false, CertReady: false, Proxied: false,
                Address: address, Error: error);
        }

        progress?.Report(DomainProvisionPhases.Applying);
        _logger.LogInformation("HTTPS provision {Domain}: applying Caddy (ACME after DNS is live)", domain);
        await _releaseAndApply(domain, cancellationToken).ConfigureAwait(false);

        progress?.Report(DomainProvisionPhases.WaitingCert);
        _logger.LogInformation("HTTPS provision {Domain}: waiting for localhost TLS certificate", domain);
        if (!await WaitUntilAsync(
                async () => (await _probe(domain, cancellationToken).ConfigureAwait(false)).Ok,
                _tlsTimeout, _tlsPoll, cancellationToken).ConfigureAwait(false))
        {
            var error =
                "DNS is live (DNS-only) but no certificate answered yet — left DNS-only so the "
                + "origin stays reachable. Use Point here to retry Proxied after the cert is ready.";
            _logger.LogWarning("HTTPS provision {Domain}: {Error}", domain, error);
            return new CloudflareHttpsProvisionResult(
                DnsOnlyOk: true, DnsReady: true, CertReady: false, Proxied: false,
                Address: address, Error: error);
        }

        if (!preferProxied)
        {
            return new CloudflareHttpsProvisionResult(
                DnsOnlyOk: true, DnsReady: true, CertReady: true, Proxied: false,
                Address: address, Error: null);
        }

        try
        {
            progress?.Report(DomainProvisionPhases.Proxying);
            _logger.LogInformation("HTTPS provision {Domain}: flipping Cloudflare record to Proxied", domain);
            var orange = await _point(domain, true, cancellationToken).ConfigureAwait(false);
            address = orange.Address;
        }
        catch (Exception exception) when (exception is DnsProviderException or InvalidOperationException)
        {
            _logger.LogWarning(
                exception,
                "HTTPS provision {Domain}: Proxied flip failed (cert is ready, still DNS-only): {Message}",
                domain, exception.Message);
            return new CloudflareHttpsProvisionResult(
                DnsOnlyOk: true, DnsReady: true, CertReady: true, Proxied: false,
                Address: address, Error: exception.Message);
        }

        _logger.LogInformation(
            "HTTPS provision {Domain}: complete — cert ready, Proxied → {Address}", domain, address);
        return new CloudflareHttpsProvisionResult(
            DnsOnlyOk: true, DnsReady: true, CertReady: true, Proxied: true,
            Address: address, Error: null);
    }

    /// <summary>
    /// Wildcards use DNS-01 — Apply first so Caddy can obtain the cert, wait until
    /// TLS answers, then Point Proxied (EnsureEdge reload happens after ACME).
    /// </summary>
    private async Task<CloudflareHttpsProvisionResult> ProvisionWildcardAsync(
        string domain,
        bool preferProxied,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(DomainProvisionPhases.Applying);
        _logger.LogInformation("HTTPS provision {Domain}: wildcard — Apply (DNS-01), then wait for cert", domain);
        await _releaseAndApply(domain, cancellationToken).ConfigureAwait(false);

        progress?.Report(DomainProvisionPhases.WaitingCert);
        var certReady = await WaitUntilAsync(
            async () => (await _probe(domain, cancellationToken).ConfigureAwait(false)).Ok,
            _tlsTimeout, _tlsPoll, cancellationToken).ConfigureAwait(false);

        if (!certReady)
        {
            return new CloudflareHttpsProvisionResult(
                DnsOnlyOk: false,
                DnsReady: false,
                CertReady: false,
                Proxied: false,
                Address: null,
                Error: "Wildcard route was applied but the certificate is not answering yet.");
        }

        if (!preferProxied)
        {
            return new CloudflareHttpsProvisionResult(
                DnsOnlyOk: true, DnsReady: true, CertReady: true, Proxied: false,
                Address: null, Error: null);
        }

        string? address;
        try
        {
            progress?.Report(DomainProvisionPhases.Proxying);
            _logger.LogInformation("HTTPS provision {Domain}: flipping wildcard record to Proxied", domain);
            var record = await _point(domain, true, cancellationToken).ConfigureAwait(false);
            address = record.Address;
        }
        catch (Exception exception) when (exception is DnsProviderException or InvalidOperationException)
        {
            _logger.LogWarning(
                exception, "HTTPS provision {Domain}: wildcard Proxied Point failed: {Message}",
                domain, exception.Message);
            return new CloudflareHttpsProvisionResult(
                DnsOnlyOk: false, DnsReady: true, CertReady: true, Proxied: false,
                Address: null, Error: exception.Message);
        }

        return new CloudflareHttpsProvisionResult(
            DnsOnlyOk: false,
            DnsReady: true,
            CertReady: true,
            Proxied: true,
            Address: address,
            Error: null);
    }

    internal static void ReleaseProxyDeferred(ProxyService proxy, string domain)
    {
        var lookup = DomainName.NormalizeForLookup(domain);
        proxy.Store.Update(config =>
        {
            var entry = config.Domains.Find(candidate =>
                string.Equals(DomainName.NormalizeForLookup(candidate.Domain), lookup, StringComparison.Ordinal));
            if (entry is not null)
            {
                entry.ProxyDeferred = false;
            }

            return true;
        });
    }

    private async Task<bool> DnsMatchesOriginAsync(string domain, CancellationToken cancellationToken)
    {
        var check = await _checkDns(domain, cancellationToken).ConfigureAwait(false);
        // DNS-only must resolve to this server — behindCdn alone is not enough here
        // (that would mean orange cloud already, which we are trying to avoid for ACME).
        return check.Matches && !check.BehindCdn;
    }

    private async Task<bool> WaitUntilAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        TimeSpan poll,
        CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await condition().ConfigureAwait(false))
            {
                return true;
            }

            if (DateTime.UtcNow - started >= timeout)
            {
                return false;
            }

            await _delay(poll, cancellationToken).ConfigureAwait(false);
        }
    }
}
