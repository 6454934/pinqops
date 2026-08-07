using PinqOps.Web;
using PinqOps.Web.Tests.Fakes;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// These methods build docker arguments and bind-mount host paths through a root
/// daemon, so they validate their own inputs rather than trusting the caller to
/// have done it. Each of these was previously safe only because the one caller
/// happened to validate first.
/// </summary>
public class DockerServiceValidationTests
{
    private static DockerService Docker(out FakeProcessRunner runner)
    {
        runner = new FakeProcessRunner();
        return new DockerService(runner);
    }

    private const string ValidSnapshot = "20260101-000000.tgz";
    private const string ValidDir = "/opt/pinqops/backups/db";

    [Theory]
    [InlineData("--platform=linux/arm64")]
    [InlineData("-v")]
    [InlineData("redis; rm -rf /")]
    [InlineData("")]
    public async Task PullImageAsync_RejectsAnythingThatIsNotAnImageReference(string image)
    {
        var docker = Docker(out _);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => docker.PullImageAsync(image));
    }

    [Fact]
    public async Task PullImageAsync_SeparatesTheReferenceFromFlags()
    {
        var docker = Docker(out var runner);

        await docker.PullImageAsync("postgres:16-alpine");

        Assert.Equal(["pull", "--", "postgres:16-alpine"], runner.Invocations.Single().Arguments);
    }

    // The snapshot name reaches a shell in RestoreVolumeAsync, so it is validated
    // here too rather than only by BackupService.
    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("x.tgz; rm -rf /")]
    [InlineData("$(id).tgz")]
    [InlineData("notatimestamp.tgz")]
    public async Task VolumeBackupAndRestore_RejectAnInvalidSnapshotName(string snapshot)
    {
        var docker = Docker(out _);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => docker.BackupVolumeAsync("data", ValidDir, snapshot));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => docker.RestoreVolumeAsync("data", ValidDir, snapshot));
    }

    // The directory is bind-mounted by a root daemon; a relative or traversing
    // path would mount an arbitrary part of the filesystem.
    [Theory]
    [InlineData("relative/path")]
    [InlineData("/opt/../etc")]
    [InlineData("")]
    public async Task VolumeBackupAndRestore_RejectAnUnsafeHostDirectory(string directory)
    {
        var docker = Docker(out _);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => docker.BackupVolumeAsync("data", directory, ValidSnapshot));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => docker.RestoreVolumeAsync("data", directory, ValidSnapshot));
    }

    /// <summary>
    /// The restore needs a shell (delete then extract), so the snapshot name is
    /// passed as a positional argument bound to $1 instead of being interpolated
    /// into the script — it must never be text the shell parses.
    /// </summary>
    [Fact]
    public async Task RestoreVolumeAsync_PassesTheSnapshotNameAsAShellArgument()
    {
        var docker = Docker(out var runner);

        await docker.RestoreVolumeAsync("data", ValidDir, ValidSnapshot);

        var arguments = runner.Invocations.Single().Arguments;
        var script = arguments[arguments.ToList().IndexOf("-c") + 1];

        Assert.DoesNotContain(ValidSnapshot, script);
        Assert.Contains("\"/src/$1\"", script);
        Assert.Equal(ValidSnapshot, arguments[^1]);
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("/data/../../etc/shadow")]
    public async Task ContainerCopy_RejectsAPathThatIsNotAbsoluteAndClean(string path)
    {
        var docker = Docker(out _);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => docker.CopyFromContainerAsync("db", path, "/tmp/x"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => docker.CopyToContainerAsync("/tmp/x", "db", path));
    }
}
