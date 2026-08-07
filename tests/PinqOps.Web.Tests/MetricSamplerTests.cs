using Microsoft.Extensions.Logging.Abstractions;
using PinqOps.Alerts;
using PinqOps.Web.Tests.Fakes;
using Xunit;

namespace PinqOps.Web.Tests;

public class MetricSamplerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private const string PsLine =
        """{"Names":"acme-app-1","State":"running","Status":"Up 5 minutes"}""";

    private const string StatsLine =
        """{"Name":"acme-app-1","CPUPerc":"12.34%","MemPerc":"45.60%","MemUsage":"1.5GiB / 4GiB"}""";

    private static MetricSampler Sampler(Func<IReadOnlyList<string>, ProcessResult> respond)
    {
        var runner = new FakeProcessRunner((_, arguments) => respond(arguments));
        return new MetricSampler(
            new DockerService(runner),
            new SystemInfoService(),
            NullLogger<MetricSampler>.Instance);
    }

    private static ProcessResult Ok(string stdout) => new(0, stdout, string.Empty);

    private static ProcessResult DockerListings(IReadOnlyList<string> arguments)
    {
        if (arguments.Contains("ps"))
        {
            return Ok(PsLine);
        }

        return arguments.Contains("stats") ? Ok(StatsLine) : Ok(string.Empty);
    }

    [Fact]
    public async Task MapsDockerStatsOntoContainerSeries()
    {
        var sample = await Sampler(DockerListings).SampleAsync(Now);

        Assert.True(sample.DockerReachable);
        var container = Assert.Single(sample.Containers);
        Assert.Equal("acme-app-1", container.Name);
        Assert.Equal(12.34, container.Cpu);
        Assert.Equal(45.6, container.Memory);
        Assert.False(container.Down);
        Assert.False(container.Unhealthy);
    }

    [Fact]
    public async Task ExposesContainerReadingsThroughTheMetricLookup()
    {
        var sample = await Sampler(DockerListings).SampleAsync(Now);

        Assert.Equal(12.34, sample.Value(AlertMetrics.ContainerCpu, "acme-app-1"));
        Assert.Equal(0, sample.Value(AlertMetrics.ContainerDown, "acme-app-1"));
        Assert.Null(sample.Value(AlertMetrics.ContainerCpu, "not-a-container"));
    }

    [Fact]
    public async Task AStoppedContainerIsReportedAsDown()
    {
        var sample = await Sampler(arguments => arguments.Contains("ps")
            ? Ok("""{"Names":"db","State":"exited","Status":"Exited (137) 4 minutes ago"}""")
            : Ok(string.Empty)).SampleAsync(Now);

        Assert.Equal(1, sample.Value(AlertMetrics.ContainerDown, "db"));
    }

    [Fact]
    public async Task AContainerWithoutStats_StillReportsItsState()
    {
        // docker stats only lists running containers, so a stopped one appears in
        // ps and nowhere else. Its CPU is unknown; its state is not.
        var sample = await Sampler(arguments => arguments.Contains("ps")
            ? Ok("""{"Names":"db","State":"exited","Status":"Exited (0) 2 hours ago"}""")
            : Ok(string.Empty)).SampleAsync(Now);

        Assert.Null(sample.Value(AlertMetrics.ContainerCpu, "db"));
        Assert.Equal(1, sample.Value(AlertMetrics.ContainerDown, "db"));
    }

    [Fact]
    public async Task WhenDockerFails_TheSampleSaysSo_AndListsNoContainers()
    {
        var sample = await Sampler(_ => new ProcessResult(1, string.Empty, "Cannot connect to the Docker daemon"))
            .SampleAsync(Now);

        Assert.False(sample.DockerReachable);
        Assert.Empty(sample.Containers);
        Assert.Null(sample.Value(AlertMetrics.ContainerCpu, "acme-app-1"));
    }

    [Fact]
    public async Task HostMetricsSurviveADockerOutage()
    {
        // The separation is the point: a docker outage must not blind every host
        // rule on the box at the same time.
        var sample = await Sampler(_ => new ProcessResult(1, string.Empty, "docker: not found")).SampleAsync(Now);

        if (OperatingSystem.IsLinux())
        {
            Assert.NotNull(sample.Value(AlertMetrics.HostMemory, string.Empty));
            Assert.NotNull(sample.Value(AlertMetrics.HostLoad1, string.Empty));
        }
    }

    [Fact]
    public async Task CpuIsUnknownOnTheFirstTick()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // CPU usage is a delta between two /proc/stat readings, so the first tick
        // genuinely has nothing to report and says so rather than claiming 0%.
        // What a later tick reports depends on the kernel's counters having moved
        // on, which is a matter of wall-clock time — that arithmetic is covered
        // deterministically by CpuTimesTests.PercentBusy instead.
        Assert.Null((await Sampler(DockerListings).SampleAsync(Now)).Cpu);
    }

    [Fact]
    public async Task CpuIsPublishedForTheSystemPanel()
    {
        var system = new SystemInfoService();
        var runner = new FakeProcessRunner((_, arguments) => DockerListings(arguments));
        var sampler = new MetricSampler(new DockerService(runner), system, NullLogger<MetricSampler>.Instance);

        var sample = await sampler.SampleAsync(Now);

        // /api/system reads the figure the sampler computed rather than trying to
        // derive a delta of its own from a single reading.
        Assert.Equal(sample.Cpu, system.CpuPercent);
    }

    [Fact]
    public async Task SamplingCostsTwoDockerCommands_WhateverTheContainerCount()
    {
        var runner = new FakeProcessRunner((_, arguments) => DockerListings(arguments));
        var sampler = new MetricSampler(
            new DockerService(runner), new SystemInfoService(), NullLogger<MetricSampler>.Instance);

        await sampler.SampleAsync(Now);

        Assert.Equal(2, runner.Invocations.Count);
        Assert.All(runner.Invocations, invocation => Assert.Equal("docker", invocation.FileName));
    }

    [Fact]
    public async Task ContainerAliasesAreKeyedByTheirFirstName()
    {
        // docker ps prints "app,alias"; docker stats prints only "app". Keying them
        // differently would leave every aliased container without CPU or memory.
        var sample = await Sampler(arguments => arguments.Contains("ps")
            ? Ok("""{"Names":"acme-app-1,web","State":"running","Status":"Up 1 hour"}""")
            : Ok(StatsLine)).SampleAsync(Now);

        Assert.Equal(12.34, sample.Value(AlertMetrics.ContainerCpu, "acme-app-1"));
    }
}
