using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

public class SetupPortsTests
{
    [Fact]
    public void ResolveContainer_DockerfileExposeWinsOverRequested()
    {
        // Stale PINQOPS_CONTAINER_PORT=8085 with EXPOSE 80 must not publish 8085→8085.
        Assert.Equal(80, SetupPorts.ResolveContainer(requested: 8085, detected: 80));
        Assert.Equal(80, SetupPorts.ResolveContainer(requested: 3000, detected: 80));
    }

    [Fact]
    public void ResolveContainer_ExplicitChoiceWinsWhenExposeUnknown()
    {
        Assert.Equal(3000, SetupPorts.ResolveContainer(requested: 3000, detected: null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void ResolveContainer_RejectsAnInvalidChoice(int port)
    {
        Assert.Throws<ArgumentException>(() => SetupPorts.ResolveContainer(port, detected: null));
    }

    [Fact]
    public void ResolveContainer_FallsBackToDetectedThenDefault()
    {
        Assert.Equal(5000, SetupPorts.ResolveContainer(requested: null, detected: 5000));
        Assert.Equal(DockerfileInspector.DefaultPort, SetupPorts.ResolveContainer(requested: null, detected: null));
    }

    [Fact]
    public void ResolveHost_ExplicitFreePortWins()
    {
        var port = SetupPorts.ResolveHost(
            requested: 8083, defaultPort: 8080, isAvailable: _ => true, findAvailable: _ => throw new Xunit.Sdk.XunitException("must not scan"));

        Assert.Equal(8083, port);
    }

    [Fact]
    public void ResolveHost_RejectsABusyExplicitPort()
    {
        // A taken port would only surface later as a failed `up -d` that takes
        // the app down — it must die while it is still just a form value.
        Assert.Throws<ArgumentException>(() =>
            SetupPorts.ResolveHost(requested: 8083, defaultPort: 8080, isAvailable: _ => false, findAvailable: _ => 8080));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void ResolveHost_RejectsAnInvalidChoice(int port)
    {
        Assert.Throws<ArgumentException>(() =>
            SetupPorts.ResolveHost(port, defaultPort: 8080, isAvailable: _ => true, findAvailable: _ => 8080));
    }

    [Fact]
    public void ResolveHost_WithoutAChoice_ScansFromTheDefault()
    {
        var port = SetupPorts.ResolveHost(
            requested: null, defaultPort: 8080, isAvailable: _ => true, findAvailable: preferred => preferred + 3);

        Assert.Equal(8083, port);
    }

    [Fact]
    public void ResolveHost_WhenTheScanFindsNothing_RefusesInsteadOfHandingBackTheBusyDefault()
    {
        // The old fallback returned the known-busy default, so the project was
        // generated onto a port the first deploy then died on — the exact failure
        // this resolution exists to prevent, only later and with less context.
        var exception = Assert.Throws<InvalidOperationException>(() => SetupPorts.ResolveHost(
            requested: null, defaultPort: 8080, isAvailable: _ => false, findAvailable: _ => null));

        Assert.Contains("8080", exception.Message, StringComparison.Ordinal);
    }
}
