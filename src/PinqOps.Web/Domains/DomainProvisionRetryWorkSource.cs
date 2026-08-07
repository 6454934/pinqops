using System.Collections.Concurrent;
using PinqOps.Proxy;
using PinqOps.Scheduling;

namespace PinqOps.Web;

/// <summary>
/// Retries the Cloudflare HTTPS provision of domains that are still deferred.
///
/// <para><b>The hole this closes.</b> A domain added while a DNS provider is
/// configured is stored with <c>ProxyDeferred</c> so that Caddy does not start ACME
/// against a name that is still NXDOMAIN. The provisioner clears the flag when it
/// reaches Apply — but the DNS wait times out after ninety seconds and returns
/// <em>before</em> that step, so a name whose record needed longer than that stayed
/// deferred for good: no site block, no certificate, no HTTP, and nothing in the
/// domain list saying so. Recovery depended on the operator noticing the toast and
/// pressing Point here. Now DNS propagating late is enough on its own.</para>
///
/// <para><b>Why deferred is still the resting state.</b> Releasing the flag on
/// timeout would be the smaller change, but it hands Caddy a name that does not
/// resolve and every ACME attempt against it burns an authorization. Keeping the
/// route out of the Caddyfile until a provision genuinely succeeds keeps that cost
/// at zero, and this source is what makes "until it succeeds" happen by itself.</para>
///
/// <para>The cooldown is in memory on purpose: a restart retrying once, a minute
/// later, is the failure mode worth having.</para>
/// </summary>
public sealed class DomainProvisionRetryWorkSource : ScheduledWorkSource
{
    /// <summary>
    /// A retry rewrites the DNS record and can then sit in a ninety-second DNS wait,
    /// so this is spaced well clear of the host's one-minute tick.
    /// </summary>
    internal static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);

    /// <summary>Prefixes the job id, so a failure line names the domain.</summary>
    private const string JobPrefix = "domain-provision-retry:";

    private readonly ProxyService _proxy;
    private readonly DomainProvisionJobs _jobs;
    private readonly Func<string, bool> _start;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAttempt = new(StringComparer.Ordinal);

    public DomainProvisionRetryWorkSource(
        ProxyService proxy,
        DomainProvisionJobs jobs,
        CloudflareHttpsProvisioner provisioner,
        ILogger<DomainProvisionRetryWorkSource> logger)
        : this(
            proxy,
            jobs,
            domain =>
            {
                ArgumentNullException.ThrowIfNull(provisioner);
                ArgumentNullException.ThrowIfNull(logger);
                logger.LogInformation(
                    "Domain {Domain} is still waiting for DNS — retrying HTTPS provision", domain);
                return DomainProvisionRunner.Start(jobs, provisioner, proxy, logger, domain) is not null;
            })
    {
    }

    /// <summary>Test seam: decide what is due without Cloudflare or Docker.</summary>
    internal DomainProvisionRetryWorkSource(
        ProxyService proxy, DomainProvisionJobs jobs, Func<string, bool> start)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(start);
        _proxy = proxy;
        _jobs = jobs;
        _start = start;
    }

    public string Name => "domain-provision-retry";

    public IReadOnlyList<ScheduledJob> Due(DateTimeOffset now)
    {
        var deferred = _proxy.Store.Load().Domains
            .Where(entry => entry is { Enabled: true, ProxyDeferred: true })
            .ToList();

        // A domain that made it through (or was deleted) must not keep a cooldown
        // that would then delay the first retry after it is added again.
        var stillDeferred = deferred
            .Select(entry => DomainName.NormalizeForLookup(entry.Domain))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var domain in _lastAttempt.Keys.Where(key => !stillDeferred.Contains(key)))
        {
            _lastAttempt.TryRemove(domain, out _);
        }

        var due = new List<ScheduledJob>();
        foreach (var entry in deferred)
        {
            var lookup = DomainName.NormalizeForLookup(entry.Domain);
            if (_jobs.IsRunning(entry.Domain))
            {
                continue;
            }

            if (_lastAttempt.TryGetValue(lookup, out var last) && now - last < Cooldown)
            {
                continue;
            }

            // Recorded here rather than when the job runs: the host starts jobs
            // detached, and an attempt that could not start is still an attempt as
            // far as spacing the next one goes.
            _lastAttempt[lookup] = now;
            var domain = entry.Domain;
            due.Add(new ScheduledJob(JobPrefix + domain, _ =>
            {
                _start(domain);
                return Task.CompletedTask;
            }));
        }

        return due;
    }
}
