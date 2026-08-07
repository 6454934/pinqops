using Microsoft.Extensions.Logging.Abstractions;
using PinqOps;
using PinqOps.Web.Tests.Fakes;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// Reading the point-in-time archive, which lives in docker volumes the dashboard
/// cannot open directly — so every read is a throwaway container.
///
/// <para>A failed read used to be indistinguishable from an empty archive, and the
/// difference is the whole page. The command's own shell already swallows its errors
/// (<c>2&gt;/dev/null || true</c>), so a non-zero exit cannot mean "the volume is
/// empty" — a named volume that does not exist is created empty and still exits 0.
/// It can only mean the read itself did not happen. Reported as zero base backups,
/// that told an operator there was nothing to recover from, during the outage that
/// made them open the page, while the archive sat intact in the volume.</para>
/// </summary>
public class PitrArchiveReadTests
{
    private static PitrService Service(IProcessRunner runner, string directory) => new(
        runner,
        new DockerService(runner),
        new PitrConfigStore(Path.Combine(directory, "pitr.json")),
        new AppCredentialStore(Path.Combine(directory, "app-credentials.json")),
        NullLogger<PitrService>.Instance);

    private static string TempDirectory() => Directory.CreateTempSubdirectory("pinqops-pitr-").FullName;

    [Fact]
    public async Task AnEmptyArchiveIsNoBackupsAndNoLastSegment()
    {
        var directory = TempDirectory();
        try
        {
            var runner = new FakeProcessRunner((_, _) => new ProcessResult(0, string.Empty, string.Empty));

            var (backups, lastArchivedAt) = await Service(runner, directory).StateAsync();

            Assert.Empty(backups);
            Assert.Null(lastArchivedAt);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AnArchiveThatIsThereIsRead()
    {
        var directory = TempDirectory();
        try
        {
            var runner = new FakeProcessRunner((_, _) => new ProcessResult(
                0, "1754006400|1048576|/v/base-20260801.tar\n", string.Empty));

            var (backups, _) = await Service(runner, directory).StateAsync();

            Assert.Equal("base-20260801.tar", Assert.Single(backups).Name);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The one that matters. Reported as an empty archive, this became "there is no
    /// base backup to recover from. Take one first" — advice to destroy the window
    /// they were trying to recover into.
    /// </summary>
    [Theory]
    [InlineData("Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?")]
    [InlineData("Unable to find image 'alpine:latest' locally\ndocker: Error response from daemon: pull access denied")]
    public async Task AnArchiveThatCannotBeReadIsNotReportedAsEmpty(string standardError)
    {
        var directory = TempDirectory();
        try
        {
            var runner = new FakeProcessRunner((_, _) => new ProcessResult(125, string.Empty, standardError));

            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Service(runner, directory).StateAsync());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// And the recovery plan says the same, rather than refusing on the grounds that
    /// there is nothing to recover from.
    /// </summary>
    [Fact]
    public async Task APlanOverAnUnreadableArchiveSaysSoRatherThanRefusing()
    {
        var directory = TempDirectory();
        try
        {
            var runner = new FakeProcessRunner((_, _) => new ProcessResult(
                125, string.Empty, "Cannot connect to the Docker daemon at unix:///var/run/docker.sock."));

            var failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => Service(runner, directory).PlanAsync(DateTimeOffset.UtcNow));

            Assert.DoesNotContain("no base backup", failure.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
