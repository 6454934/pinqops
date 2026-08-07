using PinqOps;
using PinqOps.Web.Tests.Fakes;
using Xunit;

namespace PinqOps.Web.Tests;

public class DockerServiceCreateContainerTests
{
    private static CreateContainerRequest Minimal(
        string image = "nginx:latest",
        string? name = null,
        PortMappingRequest[]? ports = null,
        string[]? env = null,
        string[]? labels = null,
        VolumeMountRequest[]? volumes = null,
        string? restartPolicy = null,
        string[]? command = null,
        string? memory = null,
        string? cpus = null,
        string? network = null) =>
        new(image, name, ports, env, labels, volumes, restartPolicy, command, memory, cpus, network);

    /// <summary>
    /// A container that has to talk to other containers needs the network named
    /// when it is created; nothing joins it afterwards. The database upgrade
    /// depends on this — its replacement container is reachable by name or it is
    /// reachable by nothing, because it publishes no ports while the old one is
    /// still holding them.
    /// </summary>
    [Fact]
    public async Task CreateContainerAsync_PutsTheContainerOnTheNamedNetwork()
    {
        var runner = new FakeProcessRunner();

        await new DockerService(runner).CreateContainerAsync(Minimal(network: "pinqops-apps"));

        var arguments = runner.Invocations.Single().Arguments.ToList();
        Assert.Contains("--network", arguments);
        Assert.Equal("pinqops-apps", arguments[arguments.IndexOf("--network") + 1]);
    }

    [Fact]
    public async Task CreateContainerAsync_NamesNoNetworkWhenNoneIsAskedFor()
    {
        var runner = new FakeProcessRunner();

        await new DockerService(runner).CreateContainerAsync(Minimal());

        Assert.DoesNotContain("--network", runner.Invocations.Single().Arguments);
    }

    [Fact]
    public async Task CreateContainerAsync_RejectsANetworkNameDockerWouldNot()
    {
        var runner = new FakeProcessRunner();

        await Assert.ThrowsAsync<ArgumentException>(
            () => new DockerService(runner).CreateContainerAsync(Minimal(network: "-not a network")));
    }

    [Fact]
    public async Task CreateContainerAsync_BuildsAFixedArgvFromTheSpec()
    {
        var runner = new FakeProcessRunner();
        var docker = new DockerService(runner);

        await docker.CreateContainerAsync(Minimal(
            image: "redis:7",
            name: "cache",
            ports: [new PortMappingRequest(6380, 6379)],
            env: ["REDIS_ARGS=--save 60 1"],
            labels: ["team=platform"],
            volumes: [new VolumeMountRequest("cache-data", "/data")],
            restartPolicy: "always",
            memory: "512m",
            cpus: "1.5",
            command: ["redis-server"]));

        var a = runner.Invocations.Single().Arguments;
        Assert.Equal("run", a[0]);
        Assert.Equal("-d", a[1]);
        Assert.Contains("--name", a);
        Assert.Contains("cache", a);
        Assert.Contains("6380:6379", a);
        Assert.Contains("REDIS_ARGS=--save 60 1", a);
        Assert.Contains("team=platform", a);
        Assert.Contains("cache-data:/data", a);
        Assert.Contains("512m", a);
        Assert.Contains("1.5", a);
        // Image precedes the command, and the command tail is preserved.
        Assert.True(a.ToList().IndexOf("redis:7") < a.ToList().IndexOf("redis-server"));
    }

    [Fact]
    public async Task CreateContainerAsync_DefaultsToUnlessStoppedRestart()
    {
        var runner = new FakeProcessRunner();
        var docker = new DockerService(runner);

        await docker.CreateContainerAsync(Minimal());

        var a = runner.Invocations.Single().Arguments;
        var i = a.ToList().IndexOf("--restart");
        Assert.True(i >= 0);
        Assert.Equal("unless-stopped", a[i + 1]);
    }

    [Fact]
    public async Task CreateContainerAsync_RejectsAnInvalidImage()
    {
        var docker = new DockerService(new FakeProcessRunner());

        await Assert.ThrowsAsync<ArgumentException>(() => docker.CreateContainerAsync(Minimal(image: "")));
        await Assert.ThrowsAsync<ArgumentException>(() => docker.CreateContainerAsync(Minimal(image: "--pull=always")));
    }

    [Fact]
    public async Task CreateContainerAsync_RejectsAHostBindMount()
    {
        var docker = new DockerService(new FakeProcessRunner());

        // A host path as the volume "name" is not a valid resource name → blocked,
        // so bind mounts (and thus host filesystem escape) are impossible.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            docker.CreateContainerAsync(Minimal(volumes: [new VolumeMountRequest("/etc", "/host")])));
    }

    [Fact]
    public async Task CreateContainerAsync_RejectsAMountPathWithOptions()
    {
        var docker = new DockerService(new FakeProcessRunner());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            docker.CreateContainerAsync(Minimal(volumes: [new VolumeMountRequest("data", "/data:ro,z")])));
    }

    [Fact]
    public async Task CreateContainerAsync_RejectsARelativeMountPath()
    {
        var docker = new DockerService(new FakeProcessRunner());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            docker.CreateContainerAsync(Minimal(volumes: [new VolumeMountRequest("data", "data")])));
    }

    [Fact]
    public async Task CreateContainerAsync_RejectsABadEnvKey()
    {
        var docker = new DockerService(new FakeProcessRunner());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            docker.CreateContainerAsync(Minimal(env: ["9INVALID=1"])));
    }

    [Fact]
    public async Task CreateContainerAsync_RejectsAnOutOfRangePort()
    {
        var docker = new DockerService(new FakeProcessRunner());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            docker.CreateContainerAsync(Minimal(ports: [new PortMappingRequest(70000, 80)])));
    }

    [Fact]
    public async Task CreateContainerAsync_RejectsAnUnknownRestartPolicy()
    {
        var docker = new DockerService(new FakeProcessRunner());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            docker.CreateContainerAsync(Minimal(restartPolicy: "maybe")));
    }

    [Fact]
    public async Task CreateContainerAsync_RejectsAnInvalidMemoryLimit()
    {
        var docker = new DockerService(new FakeProcessRunner());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            docker.CreateContainerAsync(Minimal(memory: "lots")));
    }
}
