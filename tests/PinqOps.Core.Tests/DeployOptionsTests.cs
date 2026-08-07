using Xunit;

namespace PinqOps.Tests;

public class DeployOptionsTests
{
    [Fact]
    public void Create_AppliesDefaults()
    {
        var options = DeployOptions.Create("/opt/pinqops/docker-compose.yml");

        Assert.Equal("/opt/pinqops/docker-compose.yml", options.ComposeFilePath);
        Assert.True(options.PruneImages);
        Assert.Equal(TimeSpan.FromMinutes(5), options.Timeout);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_RejectsBlankComposePath(string? path)
    {
        Assert.Throws<ArgumentException>(() => DeployOptions.Create(path));
    }

    [Fact]
    public void Create_RejectsNonPositiveTimeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DeployOptions.Create("/opt/pinqops/docker-compose.yml", timeout: TimeSpan.Zero));
    }

    [Fact]
    public void Create_HonorsExplicitValues()
    {
        var options = DeployOptions.Create(
            "/srv/app/docker-compose.yml",
            pruneImages: false,
            timeout: TimeSpan.FromSeconds(90),
            tag: "sha-0123abc",
            healthCheckTimeout: TimeSpan.FromSeconds(10),
            keepImages: 3,
            trigger: DeployRecordValues.TriggerCi);

        Assert.False(options.PruneImages);
        Assert.Equal(TimeSpan.FromSeconds(90), options.Timeout);
        Assert.Equal("sha-0123abc", options.Tag);
        Assert.Equal(TimeSpan.FromSeconds(10), options.HealthCheckTimeout);
        Assert.Equal(3, options.KeepImages);
        Assert.Equal(DeployRecordValues.TriggerCi, options.Trigger);
    }

    [Fact]
    public void Create_TagDefaults()
    {
        var options = DeployOptions.Create("/opt/pinqops/docker-compose.yml");

        Assert.Null(options.Tag);
        Assert.Equal(TimeSpan.FromSeconds(60), options.HealthCheckTimeout);
        Assert.Equal(5, options.KeepImages);
        Assert.Equal(DeployRecordValues.TriggerManual, options.Trigger);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".leading-dot")]
    [InlineData("-leading-dash")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("new\nline")]
    public void Create_RejectsInvalidTags(string tag)
    {
        Assert.Throws<ArgumentException>(
            () => DeployOptions.Create("/opt/pinqops/docker-compose.yml", tag: tag));
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("sha-0af3c9d2b1e8f7a6c5d4e3f2a1b0c9d8e7f6a5b4")]
    [InlineData("v1.2.3")]
    [InlineData("_underscore")]
    public void Create_AcceptsValidTags(string tag)
    {
        Assert.Equal(tag, DeployOptions.Create("/opt/pinqops/docker-compose.yml", tag: tag).Tag);
    }

    [Fact]
    public void Create_RejectsNegativeHealthTimeoutAndZeroKeepImages()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DeployOptions.Create(
            "/opt/pinqops/docker-compose.yml", healthCheckTimeout: TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => DeployOptions.Create(
            "/opt/pinqops/docker-compose.yml", keepImages: 0));
    }

    [Fact]
    public void Create_ExpectedImageDefaultsToNull()
    {
        Assert.Null(DeployOptions.Create("/opt/pinqops/docker-compose.yml").ExpectedImage);
    }

    [Theory]
    [InlineData("ghcr.io/acme/app", "ghcr.io/acme/app")]
    [InlineData("  ghcr.io/acme/app  ", "ghcr.io/acme/app")]
    [InlineData("ghcr.io/acme/app:latest", "ghcr.io/acme/app")]
    [InlineData("localhost:5000/app:v1", "localhost:5000/app")]
    public void Create_NormalizesExpectedImageToRepository(string input, string expected)
    {
        var options = DeployOptions.Create("/opt/pinqops/docker-compose.yml", expectedImage: input);

        Assert.Equal(expected, options.ExpectedImage);
    }

    [Theory]
    [InlineData("ghcr.io/${{ github.repository }}")]
    [InlineData("ghcr.io/acme/app image")]
    [InlineData("ghcr.io/acme/app;rm -rf")]
    [InlineData("ghcr.io/acme/app$(whoami)")]
    [InlineData("-leading-dash")]
    public void Create_RejectsUnexpandedOrMalformedExpectedImage(string image)
    {
        Assert.Throws<ArgumentException>(
            () => DeployOptions.Create("/opt/pinqops/docker-compose.yml", expectedImage: image));
    }

    [Theory]
    [InlineData("ghcr.io/Acme/app")]
    [InlineData("ghcr.io/acme/App")]
    [InlineData("GHCR.IO/acme/app")]
    public void Create_RejectsAnUppercaseImageRepository(string image)
    {
        // A registry rejects these outright ("repository name must be
        // lowercase"); pinning one would only surface later as a broken compose.
        var exception = Assert.Throws<ArgumentException>(
            () => DeployOptions.Create("/opt/pinqops/docker-compose.yml", expectedImage: image));

        Assert.Contains("lowercase", exception.Message);
    }

    [Fact]
    public void Create_AllowsUppercaseInTheTag_WhichIsStrippedAnyway()
    {
        // Only the repository must be lowercase; a tag may legally contain uppercase.
        var options = DeployOptions.Create(
            "/opt/pinqops/docker-compose.yml", expectedImage: "ghcr.io/acme/app:sha-ABC123");

        Assert.Equal("ghcr.io/acme/app", options.ExpectedImage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankExpectedImageIsNull(string image)
    {
        Assert.Null(DeployOptions.Create("/opt/pinqops/docker-compose.yml", expectedImage: image).ExpectedImage);
    }
}
