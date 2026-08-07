using PinqOps.Web;
using PinqOps.Web.Tests.Fakes;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// What "there is a newer image" means. The comparison is between two digests, and
/// the interesting cases are the ones where one of them is missing — because an
/// unknown digest is a check that did not complete, not an update.
/// </summary>
public class ImageUpdateTests
{
    private const string One = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string Two = "sha256:2222222222222222222222222222222222222222222222222222222222222222";

    private static ImageUpdate Update(string? running, string? available) =>
        new("ghcr.io/acme/app:v1", running, available, null, DateTimeOffset.UtcNow);

    [Fact]
    public void TwoDifferentDigestsAreAnUpdate() => Assert.True(Update(One, Two).UpdateAvailable);

    [Fact]
    public void TheSameDigestIsNot() => Assert.False(Update(One, One).UpdateAvailable);

    /// <summary>
    /// A badge that also means "something went wrong somewhere" is a badge nobody
    /// acts on. A missing digest is reported as a problem instead.
    /// </summary>
    [Theory]
    [InlineData(null, Two)]
    [InlineData(One, null)]
    [InlineData(null, null)]
    [InlineData("", Two)]
    public void AnUnknownDigestIsNotAnUpdate(string? running, string? available) =>
        Assert.False(Update(running, available).UpdateAvailable);

    /// <summary>
    /// The image id is the local config's hash and differs between architectures for
    /// the very same published image — so comparing ids would report an update on
    /// every arm64 host, forever. The repo digest is what the registry issued.
    /// </summary>
    [Fact]
    public async Task TheLocalDigestComesFromTheRepoDigestsRatherThanTheImageId()
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(
            0, $"""["ghcr.io/acme/app@{One}"]""", string.Empty));

        var digest = await new DockerService(runner).LocalRepoDigestAsync("ghcr.io/acme/app:v1");

        Assert.Equal(One, digest);
        Assert.Contains("{{json .RepoDigests}}", Assert.Single(runner.Invocations).Arguments);
    }

    /// <summary>
    /// One image tagged into two repositories carries a digest for each, and only
    /// the one for the repository being asked about is comparable.
    /// </summary>
    [Fact]
    public async Task TheDigestForTheRepositoryBeingAskedAboutIsTheOneReturned()
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(
            0, $"""["registry.example.com/mirror/app@{Two}","ghcr.io/acme/app@{One}"]""", string.Empty));

        Assert.Equal(One, await new DockerService(runner).LocalRepoDigestAsync("ghcr.io/acme/app:v1"));
    }

    [Fact]
    public async Task ALocallyBuiltImageHasNoRegistryDigestToCompare()
    {
        // `docker build` produces no RepoDigests, which is not a failure — it is an
        // image that was never pulled and can never be out of date against a tag.
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(0, "[]", string.Empty));

        Assert.Null(await new DockerService(runner).LocalRepoDigestAsync("acme/local:dev"));
    }

    [Fact]
    public async Task AnImageDockerDoesNotHaveIsNullRatherThanAThrow()
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(1, string.Empty, "No such image"));

        Assert.Null(await new DockerService(runner).LocalRepoDigestAsync("ghcr.io/acme/app:v1"));
    }
}
