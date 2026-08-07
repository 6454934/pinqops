using Xunit;

namespace PinqOps.Tests;

public class DockerComposeCommandBuilderTests
{
    private const string ComposePath = "/opt/pinqops/docker-compose.yml";

    [Fact]
    public void Pull_BuildsFixedArguments()
    {
        Assert.Equal(
            new[] { "compose", "-f", ComposePath, "pull" },
            DockerComposeCommandBuilder.Pull(ComposePath));
    }

    [Fact]
    public void Up_BuildsFixedArguments()
    {
        Assert.Equal(
            new[] { "compose", "-f", ComposePath, "up", "-d" },
            DockerComposeCommandBuilder.Up(ComposePath));
    }

    [Fact]
    public void Down_OmitsVolumesByDefault()
    {
        Assert.Equal(
            new[] { "compose", "-f", ComposePath, "down" },
            DockerComposeCommandBuilder.Down(ComposePath));
    }

    [Fact]
    public void Down_CanRemoveVolumes()
    {
        Assert.Equal(
            new[] { "compose", "-f", ComposePath, "down", "-v" },
            DockerComposeCommandBuilder.Down(ComposePath, removeVolumes: true));
    }

    [Fact]
    public void Down_WithProject_PinsProjectName()
    {
        Assert.Equal(
            new[] { "compose", "-p", "ikv-quiz", "-f", ComposePath, "down", "-v" },
            DockerComposeCommandBuilder.Down(ComposePath, "ikv-quiz", removeVolumes: true));
    }

    [Fact]
    public void DownProject_WorksWithoutComposeFile()
    {
        Assert.Equal(
            new[] { "compose", "-p", "ikv-quiz", "down", "-v" },
            DockerComposeCommandBuilder.DownProject("ikv-quiz", removeVolumes: true));
    }

    [Fact]
    public void DownColor_CanRemoveVolumes()
    {
        Assert.Equal(
            new[]
            {
                "compose", "-p", "demo-blue", "--env-file", "/tmp/blue.env",
                "-f", ComposePath, "down", "-v",
            },
            DockerComposeCommandBuilder.DownColor(ComposePath, "demo-blue", "/tmp/blue.env", removeVolumes: true));
    }

    [Fact]
    public void PruneImages_BuildsFixedArguments()
    {
        Assert.Equal(new[] { "image", "prune", "-f" }, DockerComposeCommandBuilder.PruneImages());
    }

    [Fact]
    public void Ps_BuildsFixedArguments()
    {
        Assert.Equal(
            new[] { "compose", "-f", ComposePath, "ps", "-a", "--format", "json" },
            DockerComposeCommandBuilder.Ps(ComposePath));
    }

    [Fact]
    public void ConfigImages_BuildsFixedArguments()
    {
        Assert.Equal(
            new[] { "compose", "-f", ComposePath, "config", "--images" },
            DockerComposeCommandBuilder.ConfigImages(ComposePath));
    }

    [Fact]
    public void ListRepoImages_BuildsFixedArguments()
    {
        Assert.Equal(
            new[] { "images", "ghcr.io/o/r", "--format", "{{json .}}" },
            DockerComposeCommandBuilder.ListRepoImages("ghcr.io/o/r"));
    }

    [Fact]
    public void RemoveImage_And_InspectImage_KeepReferenceAsSingleArgument()
    {
        Assert.Equal(new[] { "rmi", "ghcr.io/o/r:sha-1" }, DockerComposeCommandBuilder.RemoveImage("ghcr.io/o/r:sha-1"));
        Assert.Equal(new[] { "image", "inspect", "ghcr.io/o/r:sha-1" }, DockerComposeCommandBuilder.InspectImage("ghcr.io/o/r:sha-1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Pull_RejectsBlankPath(string? path)
    {
        // null → ArgumentNullException, blank → ArgumentException; both derive
        // from ArgumentException.
        Assert.ThrowsAny<ArgumentException>(() => DockerComposeCommandBuilder.Pull(path!));
    }

    [Fact]
    public void Up_KeepsPathAsSingleArgument_NoInjection()
    {
        // A hostile-looking path must remain ONE argument; it is never split or
        // interpreted as extra flags/commands.
        const string trickyPath = "/opt/pinqops/docker-compose.yml; rm -rf /";
        var arguments = DockerComposeCommandBuilder.Up(trickyPath);

        Assert.Equal(trickyPath, arguments[2]);
        Assert.Equal(5, arguments.Count);
    }
}
