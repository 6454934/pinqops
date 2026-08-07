using PinqOps;
using PinqOps.Web.Tests.Fakes;
using Xunit;

namespace PinqOps.Web.Tests;

public class DockerServiceContainerActionTests
{
    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("restart")]
    [InlineData("kill")]
    [InlineData("pause")]
    [InlineData("unpause")]
    public async Task ContainerActionAsync_RunsTheAllowedVerbAfterADoubleDash(string action)
    {
        var runner = new FakeProcessRunner();
        var docker = new DockerService(runner);

        await docker.ContainerActionAsync("burze-api", action);

        var invocation = runner.Invocations.Single();
        Assert.Equal("docker", invocation.FileName);
        Assert.Equal([action, "--", "burze-api"], invocation.Arguments);
    }

    [Fact]
    public async Task ContainerActionAsync_RejectsAnUnknownVerb()
    {
        var docker = new DockerService(new FakeProcessRunner());

        await Assert.ThrowsAsync<ArgumentException>(() => docker.ContainerActionAsync("burze-api", "exec"));
    }

    [Fact]
    public async Task ContainerActionAsync_RejectsAFlagLikeContainerName()
    {
        var docker = new DockerService(new FakeProcessRunner());

        await Assert.ThrowsAsync<ArgumentException>(() => docker.ContainerActionAsync("--all", "stop"));
    }

    [Fact]
    public async Task RemoveContainerAsync_WithoutVolumes_ForceRemovesTheContainerOnly()
    {
        var runner = new FakeProcessRunner();
        var docker = new DockerService(runner);

        await docker.RemoveContainerAsync("burze-api", removeVolumes: false);

        var invocation = runner.Invocations.Single();
        Assert.Equal(["rm", "-f", "--", "burze-api"], invocation.Arguments);
    }

    [Fact]
    public async Task RemoveContainerAsync_WithVolumes_AddsTheVolumeFlag()
    {
        var runner = new FakeProcessRunner();
        var docker = new DockerService(runner);

        await docker.RemoveContainerAsync("burze-api", removeVolumes: true);

        var invocation = runner.Invocations.Single();
        Assert.Equal(["rm", "-f", "-v", "--", "burze-api"], invocation.Arguments);
    }

    [Fact]
    public async Task RemoveContainerAsync_RejectsAFlagLikeContainerName()
    {
        var docker = new DockerService(new FakeProcessRunner());

        await Assert.ThrowsAsync<ArgumentException>(() => docker.RemoveContainerAsync("--force", removeVolumes: false));
    }

    [Fact]
    public async Task RenameContainerAsync_ValidatesBothNames()
    {
        var runner = new FakeProcessRunner();
        var docker = new DockerService(runner);

        await docker.RenameContainerAsync("burze-api", "burze-api-2");

        Assert.Equal(["rename", "--", "burze-api", "burze-api-2"], runner.Invocations.Single().Arguments);
        await Assert.ThrowsAsync<ArgumentException>(() => docker.RenameContainerAsync("burze-api", "--evil"));
    }

    [Theory]
    [InlineData("no")]
    [InlineData("always")]
    [InlineData("on-failure")]
    [InlineData("unless-stopped")]
    public async Task UpdateRestartPolicyAsync_AppliesTheAllowedPolicy(string policy)
    {
        var runner = new FakeProcessRunner();
        var docker = new DockerService(runner);

        await docker.UpdateRestartPolicyAsync("burze-api", policy);

        Assert.Equal(["update", "--restart", policy, "--", "burze-api"], runner.Invocations.Single().Arguments);
    }

    [Fact]
    public async Task UpdateRestartPolicyAsync_RejectsAnUnknownPolicy()
    {
        var docker = new DockerService(new FakeProcessRunner());

        await Assert.ThrowsAsync<ArgumentException>(() => docker.UpdateRestartPolicyAsync("burze-api", "sometimes"));
    }

    [Fact]
    public async Task CommitContainerAsync_CommitsToTheImageReference()
    {
        var runner = new FakeProcessRunner();
        var docker = new DockerService(runner);

        await docker.CommitContainerAsync("burze-api", "ghcr.io/acme/burze-api:snapshot");

        Assert.Equal(["commit", "--", "burze-api", "ghcr.io/acme/burze-api:snapshot"], runner.Invocations.Single().Arguments);
    }

    [Fact]
    public async Task CommitContainerAsync_RejectsAFlagLikeImageReference()
    {
        var docker = new DockerService(new FakeProcessRunner());

        await Assert.ThrowsAsync<ArgumentException>(() => docker.CommitContainerAsync("burze-api", "--output=/etc/passwd"));
    }

    [Fact]
    public async Task ExecCommandAsync_PassesTheArgvAfterADoubleDash_WithoutAShell()
    {
        var runner = new FakeProcessRunner();
        var docker = new DockerService(runner);

        await docker.ExecCommandAsync("burze-api", ["sh", "-c", "echo hi && rm -rf /"]);

        // The whole "sh -c ..." is passed as literal argv tokens — docker/exec never
        // interprets a shell, so the metacharacters are inert arguments.
        Assert.Equal(["exec", "--", "burze-api", "sh", "-c", "echo hi && rm -rf /"], runner.Invocations.Single().Arguments);
    }

    [Fact]
    public async Task ExecCommandAsync_RejectsAnEmptyCommand()
    {
        var docker = new DockerService(new FakeProcessRunner());

        await Assert.ThrowsAsync<ArgumentException>(() => docker.ExecCommandAsync("burze-api", []));
    }

    [Fact]
    public async Task TopAsync_ListsProcessesAfterADoubleDash()
    {
        var runner = new FakeProcessRunner();
        var docker = new DockerService(runner);

        await docker.TopAsync("burze-api");

        Assert.Equal(["top", "--", "burze-api"], runner.Invocations.Single().Arguments);
    }

    [Fact]
    public async Task ImageIdAsync_InspectsTheReferenceId()
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(0, "sha256:abc123\n", string.Empty));
        var docker = new DockerService(runner);

        var id = await docker.ImageIdAsync("ghcr.io/acme/burze-api:latest");

        Assert.Equal("sha256:abc123", id);
        Assert.Equal(["image", "inspect", "--format", "{{.Id}}", "--", "ghcr.io/acme/burze-api:latest"], runner.Invocations.Single().Arguments);
    }

    [Fact]
    public async Task ImageIdAsync_ReturnsNullWhenTheImageIsMissing()
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(1, string.Empty, "No such image"));
        var docker = new DockerService(runner);

        Assert.Null(await docker.ImageIdAsync("ghcr.io/acme/missing:latest"));
    }
}
