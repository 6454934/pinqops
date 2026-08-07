using PinqOps.Web;
using PinqOps.Web.Tests.Fakes;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// Volumes, including the two operations that read what is inside one. Those are
/// the only place in pinqops where a caller supplies a <em>path</em> rather than a
/// name, and a path has structure — which is what has to be checked.
/// </summary>
public class DockerServiceVolumeTests
{
    private static DockerService Docker(out FakeProcessRunner runner)
    {
        runner = new FakeProcessRunner();
        return new DockerService(runner);
    }

    private static IReadOnlyList<string> Only(FakeProcessRunner runner) =>
        Assert.Single(runner.Invocations).Arguments;

    [Fact]
    public async Task CreateVolumeAsync_SeparatesTheNameFromFlags()
    {
        var docker = Docker(out var runner);

        await docker.CreateVolumeAsync("acme-uploads");

        Assert.Equal(["volume", "create", "--", "acme-uploads"], Only(runner));
    }

    /// <summary>
    /// No <c>-f</c>. Docker refusing while a container still refers to the volume is
    /// the only thing between a tidy-up and a database with no data.
    /// </summary>
    [Fact]
    public async Task RemoveVolumeAsync_NeverForces()
    {
        var docker = Docker(out var runner);

        await docker.RemoveVolumeAsync("acme-uploads");

        Assert.Equal(["volume", "rm", "--", "acme-uploads"], Only(runner));
    }

    [Theory]
    [InlineData("--force")]
    [InlineData("-v")]
    [InlineData("")]
    [InlineData("vol; rm -rf /")]
    public async Task EveryVolumeOperationRejectsAnythingThatIsNotAName(string bad)
    {
        var docker = Docker(out _);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => docker.CreateVolumeAsync(bad));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => docker.RemoveVolumeAsync(bad));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => docker.InspectVolumeAsync(bad));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => docker.ListVolumeContentsAsync(bad, ""));
    }

    /// <summary>
    /// The whole reason the path is checked: it is concatenated onto the mount point
    /// inside the container, so one that climbs out reads the host's files through
    /// the bind mount — and looks like an ordinary listing while doing it.
    /// </summary>
    [Theory]
    [InlineData("../../etc")]
    [InlineData("data/../../../etc/shadow")]
    [InlineData("data\0/../..")]
    public async Task BrowsingRefusesAPathThatLeavesTheVolume(string path)
    {
        var docker = Docker(out var runner);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => docker.ListVolumeContentsAsync("acme-uploads", path));
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task BrowsingMountsTheVolumeReadOnlyAndBindsThePathAsAnArgument()
    {
        var docker = Docker(out var runner);

        await docker.ListVolumeContentsAsync("acme-uploads", "data/logs");

        var arguments = Only(runner);
        Assert.Contains("acme-uploads:/v:ro", arguments);
        // Bound positionally, so the shell inside the container never parses it.
        Assert.Equal("/v/data/logs", arguments[^1]);
        Assert.Equal("sh", arguments[^2]);
    }

    [Fact]
    public async Task BrowsingReadsTheTypeSizeAndNameOfEachEntry()
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(
            0,
            "directory|4096|./logs\nregular file|120|./app.conf\n",
            string.Empty));

        var entries = await new DockerService(runner).ListVolumeContentsAsync("acme-uploads", "");

        Assert.Equal(2, entries.Count);
        Assert.True(entries[0].Directory);
        Assert.Equal("logs", entries[0].Name);
        Assert.False(entries[1].Directory);
        Assert.Equal(120, entries[1].Size);
    }

    /// <summary>
    /// A file name may contain any character but <c>/</c> and NUL — including the
    /// separator this format uses — so the split is bounded rather than greedy.
    /// </summary>
    [Fact]
    public async Task AFileNameContainingTheSeparatorIsStillReadWhole()
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(
            0, "regular file|9|./odd|name.txt\n", string.Empty));

        var entry = Assert.Single(await new DockerService(runner).ListVolumeContentsAsync("acme-uploads", ""));

        Assert.Equal("odd|name.txt", entry.Name);
        Assert.Equal(9, entry.Size);
    }

    [Fact]
    public async Task AnEmptyDirectoryIsNoEntriesRatherThanAFailure()
    {
        // `find … -exec` produces nothing and exits non-zero on some builds; that is
        // not a failure worth reporting as one.
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(1, string.Empty, string.Empty));

        Assert.Empty(await new DockerService(runner).ListVolumeContentsAsync("acme-uploads", ""));
    }

    /// <summary>
    /// The other half of that tolerance, and the dangerous one. Anything that fails
    /// for a reason other than the directory being empty says so on stderr — the
    /// helper image not being pullable on an air-gapped host, the daemon going away
    /// between the listing and the browse, the run being refused by SELinux. Reading
    /// those as "the volume is empty" tells an operator their data is gone, and the
    /// next thing they do about a volume they believe is stale is remove it.
    /// </summary>
    [Theory]
    [InlineData("Unable to find image 'alpine:latest' locally\ndocker: Error response from daemon: pull access denied")]
    [InlineData("Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?")]
    [InlineData("docker: Error response from daemon: authorization denied by plugin")]
    public async Task AVolumeThatCannotBeReadIsNotReportedAsEmpty(string standardError)
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(125, string.Empty, standardError));

        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => new DockerService(runner).ListVolumeContentsAsync("acme-uploads", ""));
    }

    [Fact]
    public async Task APathThatIsNotThereSaysSoRatherThanReadingAsEmpty()
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(
            1, string.Empty, "cd: can't cd to /v/nope: No such file or directory"));

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => new DockerService(runner).ListVolumeContentsAsync("acme-uploads", "nope"));
    }

    [Fact]
    public async Task DownloadingRefusesTheVolumesRootBecauseItIsNotAFile()
    {
        var docker = Docker(out var runner);

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => docker.CopyFromVolumeAsync("acme-uploads", "", "/opt/pinqops/backups"));
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task DownloadingMountsTheVolumeReadOnlyAndCopiesIntoTheScratchDirectory()
    {
        var docker = Docker(out var runner);

        await docker.CopyFromVolumeAsync("acme-uploads", "data/app.conf", "/opt/pinqops/backups");

        var arguments = Only(runner);
        Assert.Contains("acme-uploads:/v:ro", arguments);
        Assert.Contains("/opt/pinqops/backups:/out", arguments);
        Assert.Equal(["sh", "/v/data/app.conf"], arguments.TakeLast(2));
    }
}
