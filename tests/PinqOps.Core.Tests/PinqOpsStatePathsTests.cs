using Xunit;

namespace PinqOps.Tests;

public class PinqOpsStatePathsTests
{
    [Fact]
    public void ComposeWorkingDirectory_ReturnsTheComposeFilesDirectory_WhenItExists()
    {
        var directory = Directory.CreateTempSubdirectory("pinqops-statepaths-tests").FullName;
        try
        {
            var composePath = Path.Combine(directory, "docker-compose.yml");
            File.WriteAllText(composePath, "services: {}\n");

            Assert.Equal(directory, PinqOpsStatePaths.ComposeWorkingDirectory(composePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ComposeWorkingDirectory_ResolvesTheDirectory_EvenWhenTheComposeFileIsAbsent()
    {
        // Only the directory needs to exist: docker itself reports a missing
        // compose file, and we must not turn that into a Process.Start throw.
        var directory = Directory.CreateTempSubdirectory("pinqops-statepaths-tests").FullName;
        try
        {
            var composePath = Path.Combine(directory, "docker-compose.yml");
            Assert.Equal(directory, PinqOpsStatePaths.ComposeWorkingDirectory(composePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("docker-compose.yml")]
    [InlineData("sub/docker-compose.yml")]
    public void ComposeWorkingDirectory_ReturnsNull_ForARelativePath(string relative)
    {
        // A relative -f would be re-resolved against the working directory we set,
        // pointing at a different file — so leave the process CWD untouched.
        Assert.Null(PinqOpsStatePaths.ComposeWorkingDirectory(relative));
    }

    [Fact]
    public void ComposeWorkingDirectory_ReturnsNull_WhenTheDirectoryDoesNotExist()
    {
        var missing = Path.Combine(
            Path.GetTempPath(),
            "pinqops-missing-" + Guid.NewGuid().ToString("N"),
            "docker-compose.yml");

        Assert.Null(PinqOpsStatePaths.ComposeWorkingDirectory(missing));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ComposeWorkingDirectory_ReturnsNull_ForBlankInput(string blank)
    {
        Assert.Null(PinqOpsStatePaths.ComposeWorkingDirectory(blank));
    }
}
