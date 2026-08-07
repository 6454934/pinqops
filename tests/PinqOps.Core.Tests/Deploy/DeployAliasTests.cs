using Xunit;

namespace PinqOps.Tests.Deploy;

/// <summary>
/// The alias is the name the proxy forwards to. Two projects sharing one share
/// traffic, so the rules that keep them apart are worth pinning on their own.
/// </summary>
public class DeployAliasTests
{
    [Fact]
    public void TheAliasIsDeployManaged()
    {
        // Hand-editing it would be a project silently joining another's pool, and a
        // deploy-managed key is shown read-only rather than accepted and lost.
        Assert.True(Deployer.IsDeployManagedVariable(Deployer.AliasVariable));
    }

    [Fact]
    public void TheOtherManagedKeysStillAre()
    {
        Assert.True(Deployer.IsDeployManagedVariable(Deployer.TagVariable));
        Assert.True(Deployer.IsDeployManagedVariable(Deployer.ImageVariable));
    }

    /// <summary>The port variables stay editable — they are the operator's, not the
    /// deploy's.</summary>
    [Theory]
    [InlineData("PINQOPS_HOST_PORT")]
    [InlineData("PINQOPS_CONTAINER_PORT")]
    [InlineData("DATABASE_URL")]
    public void EverythingElseStaysEditable(string key)
    {
        Assert.False(Deployer.IsDeployManagedVariable(key));
    }
}
