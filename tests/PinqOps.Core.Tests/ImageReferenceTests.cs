using Xunit;

namespace PinqOps.Tests;

public class ImageReferenceTests
{
    [Theory]
    [InlineData("ghcr.io/acme/app:sha-abc123", "ghcr.io/acme/app")]
    [InlineData("ghcr.io/acme/app:latest", "ghcr.io/acme/app")]
    [InlineData("ghcr.io/acme/app", "ghcr.io/acme/app")]
    [InlineData("  ghcr.io/acme/app:latest  ", "ghcr.io/acme/app")]
    [InlineData("app:v1", "app")]
    [InlineData("app", "app")]
    public void RepositoryOf_StripsTag(string reference, string expected)
    {
        Assert.Equal(expected, ImageReference.RepositoryOf(reference));
    }

    [Fact]
    public void RepositoryOf_KeepsRegistryPort()
    {
        Assert.Equal("localhost:5000/app", ImageReference.RepositoryOf("localhost:5000/app:v1"));
        Assert.Equal("localhost:5000/app", ImageReference.RepositoryOf("localhost:5000/app"));
    }

    [Fact]
    public void RepositoryOf_DropsDigest()
    {
        Assert.Equal(
            "ghcr.io/acme/app",
            ImageReference.RepositoryOf("ghcr.io/acme/app@sha256:0123456789abcdef"));
        Assert.Equal(
            "ghcr.io/acme/app",
            ImageReference.RepositoryOf("ghcr.io/acme/app:v1@sha256:0123456789abcdef"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RepositoryOf_RejectsBlank(string reference)
    {
        Assert.Throws<ArgumentException>(() => ImageReference.RepositoryOf(reference));
    }
}
