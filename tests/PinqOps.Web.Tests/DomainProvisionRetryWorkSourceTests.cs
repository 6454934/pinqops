using Microsoft.Extensions.Logging.Abstractions;
using PinqOps.DnsRecords;
using PinqOps.Proxy;
using PinqOps.Secrets;
using PinqOps.Web.Tests.Fakes;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// Getting a domain out of the deferred state without an operator.
///
/// <para>A domain added while Cloudflare is configured is stored with
/// <c>ProxyDeferred</c> so ACME is not aimed at a name that is still NXDOMAIN, and
/// the provisioner clears it when it reaches Apply. The DNS wait gives up after
/// ninety seconds and returns before that step, so a record that propagated slowly
/// left the domain deferred for good: absent from the Caddyfile, serving nothing,
/// and only recoverable by pressing Point here. These cover the timer that now
/// retries it.</para>
/// </summary>
public class DomainProvisionRetryWorkSourceTests : IDisposable
{
    private const string Domain = "app.example.com";

    private readonly string _directory;
    private readonly string _proxyDirectory;

    public DomainProvisionRetryWorkSourceTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-domain-retry-").FullName;
        _proxyDirectory = Path.Combine(_directory, "proxy");
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private ProxyService Proxy()
    {
        var runner = new FakeProcessRunner();
        return new ProxyService(
            new DockerService(runner),
            runner,
            new SecretStore(Path.Combine(_directory, "secrets.json")),
            NullLogger<ProxyService>.Instance,
            _proxyDirectory);
    }

    private static void Store(ProxyService proxy, bool enabled = true, bool deferred = true) =>
        proxy.Store.Save(new DomainConfig
        {
            Domains =
            [
                new DomainEntry
                {
                    Domain = Domain,
                    Target = "shop",
                    TargetContainer = "shop-app",
                    TargetPort = 3000,
                    Enabled = enabled,
                    ProxyDeferred = deferred,
                },
            ],
        });

    [Fact]
    public async Task ADeferredDomainIsDueAndStartsAProvision()
    {
        var proxy = Proxy();
        Store(proxy);
        var started = new List<string>();
        var source = new DomainProvisionRetryWorkSource(proxy, new DomainProvisionJobs(), domain =>
        {
            started.Add(domain);
            return true;
        });

        var due = source.Due(DateTimeOffset.UtcNow);

        Assert.Single(due);
        await due[0].Run(CancellationToken.None);
        Assert.Equal([Domain], started);
    }

    [Fact]
    public void ADomainThatIsNoLongerDeferredIsNotDue()
    {
        var proxy = Proxy();
        Store(proxy, deferred: false);
        var source = new DomainProvisionRetryWorkSource(proxy, new DomainProvisionJobs(), _ => true);

        Assert.Empty(source.Due(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ADisabledDomainIsNotDue()
    {
        var proxy = Proxy();
        Store(proxy, enabled: false);
        var source = new DomainProvisionRetryWorkSource(proxy, new DomainProvisionJobs(), _ => true);

        Assert.Empty(source.Due(DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// The host ticks every minute and a retry can sit in a ninety-second DNS wait,
    /// so without spacing every tick would queue another attempt.
    /// </summary>
    [Fact]
    public void ASecondTickInsideTheCooldownIsNotDue()
    {
        var proxy = Proxy();
        Store(proxy);
        var source = new DomainProvisionRetryWorkSource(proxy, new DomainProvisionJobs(), _ => true);
        var start = DateTimeOffset.UtcNow;

        Assert.Single(source.Due(start));
        Assert.Empty(source.Due(start + TimeSpan.FromMinutes(1)));
        Assert.Single(source.Due(start + DomainProvisionRetryWorkSource.Cooldown));
    }

    [Fact]
    public void ADomainWithAProvisionInFlightIsNotDue()
    {
        var proxy = Proxy();
        Store(proxy);
        var jobs = new DomainProvisionJobs();
        Assert.NotNull(jobs.TryStart(Domain));
        var source = new DomainProvisionRetryWorkSource(proxy, jobs, _ => true);

        Assert.Empty(source.Due(DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// A domain that succeeded (or was deleted) must not keep a cooldown, or being
    /// added again would wait out the rest of it before its first retry.
    /// </summary>
    [Fact]
    public void SucceedingClearsTheCooldown()
    {
        var proxy = Proxy();
        Store(proxy);
        var source = new DomainProvisionRetryWorkSource(proxy, new DomainProvisionJobs(), _ => true);
        var start = DateTimeOffset.UtcNow;

        Assert.Single(source.Due(start));

        Store(proxy, deferred: false);
        Assert.Empty(source.Due(start + TimeSpan.FromMinutes(1)));

        Store(proxy);
        Assert.Single(source.Due(start + TimeSpan.FromMinutes(2)));
    }

    /// <summary>
    /// The regression itself: a DNS wait that times out leaves the entry deferred —
    /// it must not be released (that would aim ACME at a name that does not
    /// resolve), and the next tick has to pick it up.
    /// </summary>
    [Fact]
    public async Task ADnsTimeoutLeavesTheDomainDeferredAndTheNextTickRetriesIt()
    {
        var proxy = Proxy();
        Store(proxy);
        var provisioner = new CloudflareHttpsProvisioner(
            (domain, _, _) => Task.FromResult(new DnsRecord("id", domain, "203.0.113.7")),
            (_, _) => Task.FromResult(
                new DnsCheckResult(Domain, [], ["203.0.113.7"], "203.0.113.7", Matches: false)),
            async (domain, _) =>
            {
                CloudflareHttpsProvisioner.ReleaseProxyDeferred(proxy, domain);
                await Task.CompletedTask;
            },
            (_, _) => Task.FromResult(new TlsProbeResult(Ok: true, Subject: "CN=" + Domain)),
            _ => false,
            (_, _) => Task.CompletedTask,
            NullLogger.Instance,
            dnsTimeout: TimeSpan.FromMilliseconds(50),
            tlsTimeout: TimeSpan.FromMilliseconds(50),
            dnsPoll: TimeSpan.FromMilliseconds(1),
            tlsPoll: TimeSpan.FromMilliseconds(1));

        var result = await provisioner.ProvisionAsync(Domain);

        Assert.False(result.DnsReady);
        Assert.NotNull(result.Error);
        Assert.True(proxy.Store.Load().Domains[0].ProxyDeferred);

        var source = new DomainProvisionRetryWorkSource(proxy, new DomainProvisionJobs(), _ => true);
        Assert.Single(source.Due(DateTimeOffset.UtcNow));
    }
}
