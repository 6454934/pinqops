using PinqOps.Tests.Fakes;
using Xunit;

namespace PinqOps.Tests;

public class DockerBootstrapperTests
{
    [Fact]
    public async Task EnsureReadyAsync_AlreadyInstalled_DoesNotInstall()
    {
        var runner = new FakeProcessRunner();
        var bootstrapper = new DockerBootstrapper(runner, readOsRelease: () => "ID=ubuntu\n");

        var ready = await bootstrapper.EnsureReadyAsync();

        Assert.True(ready);
        Assert.DoesNotContain(runner.Invocations, invocation => invocation.CommandLine.Contains("bash"));
    }

    [Fact]
    public async Task EnsureReadyAsync_MissingOnUbuntu_InstallsThenSucceeds()
    {
        var dockerReady = false;
        var runner = new FakeProcessRunner((fileName, arguments) =>
        {
            // The last install step enables docker — after that, probes succeed.
            if (fileName == "sudo" && arguments.Contains("systemctl"))
            {
                dockerReady = true;
            }

            if (fileName == "docker")
            {
                return dockerReady
                    ? new ProcessResult(0, "Docker version", string.Empty)
                    : new ProcessResult(127, string.Empty, "not found");
            }

            return new ProcessResult(0, string.Empty, string.Empty);
        });
        var logs = new List<string>();
        var bootstrapper = new DockerBootstrapper(
            runner,
            log: logs.Add,
            readOsRelease: () => "ID=ubuntu\nVERSION_CODENAME=jammy\n");

        var ready = await bootstrapper.EnsureReadyAsync(dockerGroupUser: "deploy");

        Assert.True(ready);
        Assert.Contains(logs, line => line.Contains("[  5%]", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("[ 70%]", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("[100%]", StringComparison.Ordinal));
        Assert.Contains(runner.Invocations, invocation =>
            invocation.CommandLine.Contains("usermod") && invocation.CommandLine.Contains("deploy"));
    }

    [Fact]
    public async Task EnsureReadyAsync_UnsupportedDistribution_DoesNotInstall()
    {
        var runner = new FakeProcessRunner((fileName, _) =>
            fileName == "docker"
                ? new ProcessResult(127, string.Empty, "not found")
                : new ProcessResult(0, string.Empty, string.Empty));
        var bootstrapper = new DockerBootstrapper(runner, readOsRelease: () => "ID=fedora\n");

        var ready = await bootstrapper.EnsureReadyAsync();

        Assert.False(ready);
        Assert.DoesNotContain(runner.Invocations, invocation => invocation.CommandLine.Contains("bash"));
    }
}
