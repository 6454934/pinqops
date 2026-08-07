using PinqOps;
using PinqOps.Web;
using PinqOps.Web.Tests.Fakes;
using Xunit;

namespace PinqOps.Web.Tests;

public class DockerEndpointTests
{
    // The local command line must stay exactly what it was, or every existing
    // behaviour — and every test asserting on it — changes meaning.
    [Fact]
    public void LocalAddsNothingToTheCommandLine() =>
        Assert.Empty(DockerEndpoint.Local.Arguments);

    [Fact]
    public void SshRoutesThroughTheManagedAlias() =>
        Assert.Equal(["-H", "ssh://pinqops-prod"], DockerEndpoint.ForSsh("prod").Arguments);

    [Fact]
    public void ForLocalEnvironment_IsLocal() =>
        Assert.Empty(DockerEndpoint.For(ManagedEnvironment.Local()).Arguments);

    [Fact]
    public void ForSshEnvironment_Routes()
    {
        var environment = new ManagedEnvironment
        {
            Id = "prod", Name = "prod", Transport = ManagedEnvironment.TransportSsh, Host = "10.0.0.5", User = "deploy",
        };

        Assert.Equal(["-H", "ssh://pinqops-prod"], DockerEndpoint.For(environment).Arguments);
    }
}

public class DockerServiceEndpointTests
{
    [Fact]
    public async Task DefaultInstanceAddressesTheLocalDaemon()
    {
        var runner = new FakeProcessRunner();

        await new DockerService(runner).ListContainersAsync();

        Assert.Equal("ps", runner.Invocations.Single().Arguments[0]);
    }

    // Every docker call in the service goes through one place, so a bound
    // instance cannot silently reach the local daemon.
    [Theory]
    [InlineData("list")]
    [InlineData("logs")]
    [InlineData("inspect")]
    [InlineData("action")]
    [InlineData("prune")]
    public async Task BoundInstancePrefixesTheRoutingArguments(string call)
    {
        // `inspect` parses its stdout, so the fake has to answer with real JSON.
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(0, "[{}]", string.Empty));
        var docker = new DockerService(runner).For(DockerEndpoint.ForSsh("prod"));

        switch (call)
        {
            case "list": await docker.ListContainersAsync(); break;
            case "logs": await docker.ContainerLogsAsync("web", 10); break;
            case "inspect": await docker.InspectContainerAsync("web"); break;
            case "action": await docker.ContainerActionAsync("web", "stop"); break;
            case "prune": await docker.PruneImagesAsync(); break;
        }

        var arguments = runner.Invocations.Single().Arguments;
        Assert.Equal("-H", arguments[0]);
        Assert.Equal("ssh://pinqops-prod", arguments[1]);
    }

    // The catalog install path builds its argv separately, so it is pinned too.
    [Fact]
    public async Task InstallingACatalogAppAlsoRoutes()
    {
        var runner = new FakeProcessRunner();
        var docker = new DockerService(runner).For(DockerEndpoint.ForSsh("prod"));

        await docker.InstallAppAsync(AppCatalog.Find("redis")!, hostPorts: null);

        Assert.All(runner.Invocations, invocation => Assert.Equal("-H", invocation.Arguments[0]));
    }

    [Fact]
    public void ForReportsTheEnvironmentItAddresses() =>
        Assert.Equal("prod", new DockerService(new FakeProcessRunner()).For(DockerEndpoint.ForSsh("prod")).EnvironmentId);
}
