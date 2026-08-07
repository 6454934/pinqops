using PinqOps.Web;
using PinqOps.Web.Tests.Fakes;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// The image lifecycle. Every one of these builds a docker argument list from a
/// value an operator typed, so every one validates it and separates it from the
/// flags — and two of them differ from the obvious implementation in a way that is
/// the whole point.
/// </summary>
public class DockerServiceImageTests
{
    private static DockerService Docker(out FakeProcessRunner runner)
    {
        runner = new FakeProcessRunner();
        return new DockerService(runner);
    }

    private static IReadOnlyList<string> Only(FakeProcessRunner runner) =>
        Assert.Single(runner.Invocations).Arguments;

    /// <summary>
    /// No <c>-f</c>. Docker refusing to delete an image a container is running from
    /// is the only thing standing between a tidy-up and an app that cannot restart,
    /// and forcing it is not a decision to make on the operator's behalf.
    /// </summary>
    [Fact]
    public async Task RemoveImageAsync_NeverForces()
    {
        var docker = Docker(out var runner);

        await docker.RemoveImageAsync("ghcr.io/acme/app:latest");

        Assert.Equal(["image", "rm", "--", "ghcr.io/acme/app:latest"], Only(runner));
    }

    /// <summary>
    /// Kept apart from the plain prune, not folded into it as a flag on the same
    /// call: this one removes the previous version of every application on the
    /// server, which is what a rollback needs and cannot get back without a pull.
    /// </summary>
    [Fact]
    public async Task PruneAllImagesAsync_IsADifferentCallFromTheDanglingPrune()
    {
        var docker = Docker(out var runner);

        await docker.PruneAllImagesAsync();
        await docker.PruneImagesAsync();

        Assert.Equal(["image", "prune", "-a", "-f"], runner.Invocations[0].Arguments);
        Assert.Equal(["image", "prune", "-f"], runner.Invocations[1].Arguments);
    }

    [Fact]
    public async Task TagImageAsync_SeparatesBothReferencesFromFlags()
    {
        var docker = Docker(out var runner);

        await docker.TagImageAsync("ghcr.io/acme/app:sha-abc", "ghcr.io/acme/app:known-good");

        Assert.Equal(
            ["tag", "--", "ghcr.io/acme/app:sha-abc", "ghcr.io/acme/app:known-good"],
            Only(runner));
    }

    [Theory]
    [InlineData("--privileged")]
    [InlineData("-v")]
    [InlineData("app; rm -rf /")]
    [InlineData("")]
    public async Task TagImageAsync_RejectsAnythingThatIsNotAReference(string bad)
    {
        var docker = Docker(out _);

        // Both ends, because either one reaching docker unchecked is the same hole.
        await Assert.ThrowsAnyAsync<ArgumentException>(() => docker.TagImageAsync(bad, "ghcr.io/acme/app:x"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => docker.TagImageAsync("ghcr.io/acme/app:x", bad));
    }

    [Theory]
    [InlineData("--force")]
    [InlineData("")]
    public async Task RemoveAndInspectRejectAnythingThatIsNotAReference(string bad)
    {
        var docker = Docker(out _);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => docker.RemoveImageAsync(bad));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => docker.InspectImageAsync(bad));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => docker.ImageHistoryAsync(bad));
    }

    [Fact]
    public async Task ImageHistoryAsync_AsksForTheWholeCommandNotATruncatedOne()
    {
        // A truncated layer command is the one thing history is for: "RUN apt-get
        // install …" tells nobody anything.
        var docker = Docker(out var runner);

        await docker.ImageHistoryAsync("alpine:3");

        Assert.Contains("--no-trunc", Only(runner));
        Assert.Equal("alpine:3", Only(runner)[^1]);
    }

    [Fact]
    public async Task ImageHistoryAsync_ReadsOneLayerPerLine()
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(
            0,
            """{"CreatedBy":"RUN apk add curl","Size":"4MB"}""" + "\n" + """{"CreatedBy":"FROM alpine","Size":"7MB"}""",
            string.Empty));

        var layers = await new DockerService(runner).ImageHistoryAsync("alpine:3");

        Assert.Equal(2, layers.Count);
        Assert.Equal("RUN apk add curl", layers[0].GetProperty("CreatedBy").GetString());
    }

    [Fact]
    public async Task InspectImageAsync_ReturnsTheOneEntryRatherThanTheArrayAroundIt()
    {
        // `docker image inspect` always answers with an array, even for one
        // reference; handing that to the page would make every field one level
        // deeper than it reads.
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(
            0, """[{"Id":"sha256:abc","RepoTags":["alpine:3"]}]""", string.Empty));

        var image = await new DockerService(runner).InspectImageAsync("alpine:3");

        Assert.Equal("sha256:abc", image!.Value.GetProperty("Id").GetString());
    }

    [Fact]
    public async Task InspectImageAsync_AnEmptyAnswerIsNullRatherThanACrash()
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(0, "[]", string.Empty));

        Assert.Null(await new DockerService(runner).InspectImageAsync("alpine:3"));
    }
}
