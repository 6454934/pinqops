using PinqOps.Registries;
using Xunit;

namespace PinqOps.Tests.Registries;

/// <summary>
/// Docker's reference grammar has three rules a naive split gets wrong, and each one
/// produces a request to the wrong place rather than an error — which is the worst
/// kind of wrong, because it looks like a credential problem.
/// </summary>
public class ImageReferenceParseTests
{
    private static ImageReferenceParts Parse(string reference) =>
        RegistryReference.Parse(reference) ?? throw new Xunit.Sdk.XunitException($"'{reference}' should parse");

    [Fact]
    public void AFullyQualifiedReferenceComesApartAsWritten()
    {
        var parts = Parse("ghcr.io/acme/app:sha-abc123");

        Assert.Equal("ghcr.io", parts.Registry);
        Assert.Equal("acme/app", parts.Repository);
        Assert.Equal("sha-abc123", parts.Tag);
        Assert.Null(parts.Digest);
    }

    [Fact]
    public void ANameWithNoTagMeansLatest() => Assert.Equal("latest", Parse("ghcr.io/acme/app").Tag);

    /// <summary>
    /// The first rule: a colon can be a port or a tag, and only its position says
    /// which. <c>registry:5000/app</c> has no tag at all.
    /// </summary>
    [Fact]
    public void AColonInTheHostIsAPortAndNotATag()
    {
        var parts = Parse("registry.example.com:5000/acme/app");

        Assert.Equal("registry.example.com:5000", parts.Registry);
        Assert.Equal("acme/app", parts.Repository);
        Assert.Equal("latest", parts.Tag);
    }

    [Fact]
    public void APortAndATagTogetherAreBothRead()
    {
        var parts = Parse("registry.example.com:5000/acme/app:v2");

        Assert.Equal("registry.example.com:5000", parts.Registry);
        Assert.Equal("acme/app", parts.Repository);
        Assert.Equal("v2", parts.Tag);
    }

    /// <summary>
    /// The second rule: a first segment with no dot and no colon is a namespace, not
    /// a host. Reading <c>acme/app</c> as the host <c>acme</c> looks for an ordinary
    /// Docker Hub image on a machine that does not exist.
    /// </summary>
    [Fact]
    public void ANamespaceIsNotAHost()
    {
        var parts = Parse("acme/app:v1");

        Assert.Equal(RegistryReference.DefaultRegistry, parts.Registry);
        Assert.Equal("acme/app", parts.Repository);
    }

    [Fact]
    public void LocalhostIsAHostEvenWithoutADot() =>
        Assert.Equal("localhost", Parse("localhost/acme/app").Registry);

    /// <summary>
    /// The third rule: an unqualified Docker Hub name is in the library namespace.
    /// Asking for <c>/v2/postgres/manifests/16</c> answers 401, which reads like a
    /// credential problem and is really a missing prefix.
    /// </summary>
    [Theory]
    [InlineData("postgres", "library/postgres")]
    [InlineData("postgres:16", "library/postgres")]
    [InlineData("docker.io/redis", "library/redis")]
    public void AnUnqualifiedDockerHubNameIsInTheLibraryNamespace(string reference, string expected) =>
        Assert.Equal(expected, Parse(reference).Repository);

    [Fact]
    public void AQualifiedDockerHubNameIsLeftAlone() =>
        Assert.Equal("acme/app", Parse("docker.io/acme/app").Repository);

    /// <summary>
    /// Docker Hub's API is not on the name docker uses for it, and nothing else has
    /// that split.
    /// </summary>
    [Fact]
    public void OnlyDockerHubsApiHostDiffersFromItsName()
    {
        Assert.Equal(RegistryReference.DefaultRegistryApi, RegistryReference.ApiHost(RegistryReference.DefaultRegistry));
        Assert.Equal("ghcr.io", RegistryReference.ApiHost("ghcr.io"));
    }

    [Fact]
    public void AReferencePinnedToADigestHasNothingToCheck()
    {
        var parts = Parse("ghcr.io/acme/app@sha256:abc123");

        Assert.True(parts.IsPinnedToADigest);
        Assert.Equal("sha256:abc123", parts.Digest);
        Assert.Null(parts.Tag);
    }

    [Fact]
    public void ATagAndADigestTogetherAreTheDigest()
    {
        // Docker resolves to the digest and ignores the tag; so does this.
        var parts = Parse("ghcr.io/acme/app:v1@sha256:abc123");

        Assert.Equal("sha256:abc123", parts.Digest);
        Assert.Null(parts.Tag);
        Assert.Equal("acme/app", parts.Repository);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-oops/app")]
    [InlineData("ghcr.io/acme/app:")]
    [InlineData("ghcr.io/acme/app@")]
    [InlineData("ghcr.io//app")]
    [InlineData("ghcr.io/Acme/App")]
    [InlineData("ghcr.io/acme/app;rm -rf /")]
    public void SomethingThatIsNotAReferenceIsNull(string reference) =>
        Assert.Null(RegistryReference.Parse(reference));

    [Fact]
    public void ANullReferenceIsNull() => Assert.Null(RegistryReference.Parse(null));
}
