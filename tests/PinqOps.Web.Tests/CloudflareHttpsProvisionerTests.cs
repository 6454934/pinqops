using Microsoft.Extensions.Logging.Abstractions;
using PinqOps.DnsRecords;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Tests;

public class CloudflareHttpsProvisionerTests
{
    private static CloudflareHttpsProvisioner Build(
        Func<string, bool, CancellationToken, Task<DnsRecord>>? point = null,
        Func<string, CancellationToken, Task<DnsCheckResult>>? checkDns = null,
        Func<string, CancellationToken, Task>? releaseAndApply = null,
        Func<string, CancellationToken, Task<TlsProbeResult>>? probe = null,
        Func<string, bool>? isWildcard = null,
        List<string>? log = null)
    {
        var points = log ?? [];
        return new CloudflareHttpsProvisioner(
            point ?? ((domain, proxied, _) =>
            {
                points.Add($"point:{(proxied ? "orange" : "grey")}");
                return Task.FromResult(new DnsRecord("id", domain, "203.0.113.7"));
            }),
            checkDns ?? ((_, _) => Task.FromResult(
                new DnsCheckResult("app.example.com", ["203.0.113.7"], ["203.0.113.7"], "203.0.113.7", Matches: true))),
            releaseAndApply ?? ((domain, _) =>
            {
                points.Add($"apply:{domain}");
                return Task.CompletedTask;
            }),
            probe ?? ((_, _) => Task.FromResult(new TlsProbeResult(Ok: true, Subject: "CN=app.example.com"))),
            isWildcard ?? (_ => false),
            (_, _) => Task.CompletedTask,
            NullLogger.Instance,
            dnsTimeout: TimeSpan.FromMilliseconds(50),
            tlsTimeout: TimeSpan.FromMilliseconds(50),
            dnsPoll: TimeSpan.FromMilliseconds(1),
            tlsPoll: TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task OrdinaryDomain_DnsOnlyThenApplyThenProxied()
    {
        var steps = new List<string>();
        var provisioner = Build(log: steps);

        var result = await provisioner.ProvisionAsync("app.example.com", preferProxied: true);

        Assert.Equal(["point:grey", "apply:app.example.com", "point:orange"], steps);
        Assert.True(result.DnsOnlyOk);
        Assert.True(result.DnsReady);
        Assert.True(result.CertReady);
        Assert.True(result.Proxied);
        Assert.Equal("203.0.113.7", result.Address);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task OrdinaryDomain_DnsTimeout_LeavesDnsOnlyWithoutApplyOrOrange()
    {
        var steps = new List<string>();
        var provisioner = Build(
            point: (domain, proxied, _) =>
            {
                steps.Add($"point:{(proxied ? "orange" : "grey")}");
                return Task.FromResult(new DnsRecord("id", domain, "203.0.113.7"));
            },
            checkDns: (_, _) => Task.FromResult(
                new DnsCheckResult("app.example.com", [], ["203.0.113.7"], "203.0.113.7", Matches: false)),
            releaseAndApply: (domain, _) =>
            {
                steps.Add($"apply:{domain}");
                return Task.CompletedTask;
            },
            log: steps);

        var result = await provisioner.ProvisionAsync("app.example.com");

        Assert.Equal(["point:grey"], steps);
        Assert.True(result.DnsOnlyOk);
        Assert.False(result.DnsReady);
        Assert.False(result.CertReady);
        Assert.False(result.Proxied);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task OrdinaryDomain_TlsTimeout_LeavesDnsOnlyWithoutOrange()
    {
        var steps = new List<string>();
        var provisioner = Build(
            point: (domain, proxied, _) =>
            {
                steps.Add($"point:{(proxied ? "orange" : "grey")}");
                return Task.FromResult(new DnsRecord("id", domain, "203.0.113.7"));
            },
            releaseAndApply: (domain, _) =>
            {
                steps.Add($"apply:{domain}");
                return Task.CompletedTask;
            },
            probe: (_, _) => Task.FromResult(new TlsProbeResult(Ok: false, Error: "handshake failed")),
            log: steps);

        var result = await provisioner.ProvisionAsync("app.example.com");

        Assert.Equal(["point:grey", "apply:app.example.com"], steps);
        Assert.True(result.DnsOnlyOk);
        Assert.True(result.DnsReady);
        Assert.False(result.CertReady);
        Assert.False(result.Proxied);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task OrdinaryDomain_PreferNotProxied_StopsAfterCert()
    {
        var steps = new List<string>();
        var provisioner = Build(log: steps);

        var result = await provisioner.ProvisionAsync("app.example.com", preferProxied: false);

        Assert.Equal(["point:grey", "apply:app.example.com"], steps);
        Assert.True(result.CertReady);
        Assert.False(result.Proxied);
    }

    [Fact]
    public async Task Wildcard_AppliesThenWaitsForCertThenPointsProxied()
    {
        var steps = new List<string>();
        var provisioner = Build(
            point: (domain, proxied, _) =>
            {
                steps.Add($"point:{(proxied ? "orange" : "grey")}");
                return Task.FromResult(new DnsRecord("id", domain, "203.0.113.7"));
            },
            releaseAndApply: (domain, _) =>
            {
                steps.Add($"apply:{domain}");
                return Task.CompletedTask;
            },
            isWildcard: _ => true,
            log: steps);

        var result = await provisioner.ProvisionAsync("*.example.com", preferProxied: true);

        Assert.Equal(["apply:*.example.com", "point:orange"], steps);
        Assert.True(result.Proxied);
        Assert.True(result.CertReady);
    }

    [Fact]
    public async Task Wildcard_CertTimeout_DoesNotPointProxied()
    {
        var steps = new List<string>();
        var provisioner = Build(
            point: (domain, proxied, _) =>
            {
                steps.Add($"point:{(proxied ? "orange" : "grey")}");
                return Task.FromResult(new DnsRecord("id", domain, "203.0.113.7"));
            },
            releaseAndApply: (domain, _) =>
            {
                steps.Add($"apply:{domain}");
                return Task.CompletedTask;
            },
            probe: (_, _) => Task.FromResult(new TlsProbeResult(Ok: false, Error: "no cert")),
            isWildcard: _ => true,
            log: steps);

        var result = await provisioner.ProvisionAsync("*.example.com", preferProxied: true);

        Assert.Equal(["apply:*.example.com"], steps);
        Assert.False(result.Proxied);
        Assert.False(result.CertReady);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task OrdinaryDomain_ReportsProgressPhasesInOrder()
    {
        var phases = new List<string>();
        var provisioner = Build();
        var progress = new InlineProgress(phases);

        await provisioner.ProvisionAsync("app.example.com", preferProxied: true, progress);

        Assert.Equal(
            [
                DomainProvisionPhases.WritingDns,
                DomainProvisionPhases.WaitingDns,
                DomainProvisionPhases.Applying,
                DomainProvisionPhases.WaitingCert,
                DomainProvisionPhases.Proxying,
            ],
            phases);
    }

    [Fact]
    public async Task Wildcard_ReportsApplyWaitCertProxyingOrder()
    {
        var phases = new List<string>();
        var provisioner = Build(isWildcard: _ => true);
        var progress = new InlineProgress(phases);

        await provisioner.ProvisionAsync("*.example.com", preferProxied: true, progress);

        Assert.Equal(
            [
                DomainProvisionPhases.Applying,
                DomainProvisionPhases.WaitingCert,
                DomainProvisionPhases.Proxying,
            ],
            phases);
    }

    private sealed class InlineProgress(List<string> phases) : IProgress<string>
    {
        public void Report(string value) => phases.Add(value);
    }

    [Fact]
    public void TryStart_RejectsSecondJobForSameDomain()
    {
        var jobs = new DomainProvisionJobs();
        Assert.NotNull(jobs.TryStart("app.example.com"));
        Assert.Null(jobs.TryStart("app.example.com"));
        Assert.NotNull(jobs.TryStart("other.example.com"));
    }

    [Fact]
    public void CancelDomain_CancelsActiveJob()
    {
        var jobs = new DomainProvisionJobs();
        var job = jobs.TryStart("app.example.com");
        Assert.NotNull(job);

        Assert.Equal(1, jobs.CancelDomain("app.example.com"));
        Assert.True(job.Finished);
        Assert.Equal(DomainProvisionPhases.Error, job.Phase);
        Assert.True(job.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task BehindCdnAlone_DoesNotCountAsDnsReadyForHttp01()
    {
        var steps = new List<string>();
        var provisioner = Build(
            point: (domain, proxied, _) =>
            {
                steps.Add($"point:{(proxied ? "orange" : "grey")}");
                return Task.FromResult(new DnsRecord("id", domain, "203.0.113.7"));
            },
            checkDns: (_, _) => Task.FromResult(
                new DnsCheckResult(
                    "app.example.com",
                    ["104.16.1.1"],
                    ["203.0.113.7"],
                    "203.0.113.7",
                    Matches: true,
                    BehindCdn: true)),
            releaseAndApply: (domain, _) =>
            {
                steps.Add($"apply:{domain}");
                return Task.CompletedTask;
            },
            log: steps);

        var result = await provisioner.ProvisionAsync("app.example.com");

        Assert.Equal(["point:grey"], steps);
        Assert.False(result.DnsReady);
        Assert.False(result.Proxied);
    }
}
