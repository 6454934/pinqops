using PinqOps.Deploy;
using Xunit;

namespace PinqOps.Tests.Deploy;

public class DeployColorsTests
{
    [Fact]
    public void AProjectStartsOnADeterministicColour() =>
        Assert.Equal(DeployColors.Blue, DeployColors.First);

    [Fact]
    public void EachColourMovesToTheOther()
    {
        Assert.Equal(DeployColors.Green, DeployColors.Other(DeployColors.Blue));
        Assert.Equal(DeployColors.Blue, DeployColors.Other(DeployColors.Green));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("purple")]
    [InlineData("BLUE")]
    public void AnythingThatIsNotAColourReadsAsTheFirstOne(string? stored)
    {
        // It names a compose project and a network alias, so it can never be
        // anything but one of the two.
        Assert.Equal(DeployColors.First, DeployColors.Normalize(stored));
        Assert.False(DeployColors.IsKnown(stored));
    }

    [Fact]
    public void EachColourIsItsOwnComposeProject()
    {
        Assert.Equal("shop-blue", DeployColors.ProjectName("shop", DeployColors.Blue));
        Assert.Equal("shop-green", DeployColors.ProjectName("shop", DeployColors.Green));
    }

    [Fact]
    public void AProjectNameIsReducedTheWayComposeWouldReduceIt()
    {
        // Compose silently normalises an out-of-grammar -p argument, and then the
        // containers are not where anything expects them.
        Assert.Equal("myapp-blue", DeployColors.ProjectName("My.App", DeployColors.Blue));
    }

    /// <summary>
    /// Load-bearing rather than tidy: an unqualified alias would have both colours
    /// answering the same name, so the proxy would split traffic between the version
    /// being tested and the one serving — and the switch would have nothing to
    /// switch.
    /// </summary>
    [Fact]
    public void EachColourAnswersOnItsOwnNetworkAlias()
    {
        Assert.Equal("shop-blue", DeployColors.Alias("shop", DeployColors.Blue));
        Assert.NotEqual(
            DeployColors.Alias("shop", DeployColors.Blue),
            DeployColors.Alias("shop", DeployColors.Green));
    }
}

public class ColorEnvironmentTests : IDisposable
{
    private readonly string _directory;
    private readonly string _composePath;

    public ColorEnvironmentTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-color-env-tests").FullName;
        _composePath = Path.Combine(_directory, "docker-compose.yml");
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Env => PinqOpsStatePaths.EnvFile(_composePath);

    /// <summary>
    /// <c>--env-file</c> replaces the project's <c>.env</c>; compose does not merge
    /// them. A value left out here is gone, and for PINQOPS_IMAGE that means the
    /// compose default quietly deploying somebody else's image.
    /// </summary>
    [Fact]
    public void EveryValueFromTheProjectsEnvIsCarriedOver()
    {
        EnvFileStore.SetValue(Env, Deployer.ImageVariable, "ghcr.io/acme/shop");
        EnvFileStore.SetValue(Env, Deployer.TagVariable, "sha-abc123");
        EnvFileStore.SetValue(Env, "DB_PASSWORD", "hunter2");

        var written = ColorEnvironment.Write(_composePath, DeployColors.Green, "shop");

        Assert.Equal("ghcr.io/acme/shop", EnvFileStore.GetValue(written, Deployer.ImageVariable));
        Assert.Equal("sha-abc123", EnvFileStore.GetValue(written, Deployer.TagVariable));
        Assert.Equal("hunter2", EnvFileStore.GetValue(written, "DB_PASSWORD"));
    }

    [Fact]
    public void TheAliasIsTheOneValueThatDiffers()
    {
        EnvFileStore.SetValue(Env, Deployer.AliasVariable, "shop");

        var green = ColorEnvironment.Write(_composePath, DeployColors.Green, "shop");
        var blue = ColorEnvironment.Write(_composePath, DeployColors.Blue, "shop");

        Assert.Equal("shop-green", EnvFileStore.GetValue(green, Deployer.AliasVariable));
        Assert.Equal("shop-blue", EnvFileStore.GetValue(blue, Deployer.AliasVariable));
    }

    [Fact]
    public void ItLivesBesideTheComposeProject() =>
        Assert.Equal(
            Path.Combine(_directory, "colors", "green.env"),
            ColorEnvironment.FileFor(_composePath, DeployColors.Green));

    /// <summary>
    /// Rewritten from scratch, not edited: a value the operator removed from
    /// <c>.env</c> has to disappear here too, and an accumulated file would keep
    /// deploying it.
    /// </summary>
    [Fact]
    public void AValueRemovedFromTheProjectsEnvDisappearsFromTheColourToo()
    {
        EnvFileStore.SetValue(Env, "OLD_FLAG", "on");
        ColorEnvironment.Write(_composePath, DeployColors.Blue, "shop");

        EnvFileStore.RemoveValue(Env, "OLD_FLAG");
        var written = ColorEnvironment.Write(_composePath, DeployColors.Blue, "shop");

        Assert.Null(EnvFileStore.GetValue(written, "OLD_FLAG"));
    }
}
