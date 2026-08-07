using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// The dashboard swaps a whole page for a "Docker is not reachable" card based
/// on these classifications, so a stderr shape that stops matching means the raw
/// daemon error goes back on screen — the exact thing this replaced.
/// </summary>
public class DockerDaemonErrorTests
{
    [Theory]
    // Linux, daemon stopped — by far the most common.
    [InlineData("Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?")]
    // Docker Desktop on Windows, engine not started.
    [InlineData("failed to connect to the docker API at npipe:////./pipe/dockerDesktopLinuxEngine; check if the "
        + "path is correct and if the daemon is running: open //./pipe/dockerDesktopLinuxEngine: The system cannot "
        + "find the file specified.")]
    // Remote host over SSH.
    [InlineData("error during connect: Get \"http://docker/v1.45/containers/json\": command [ssh] exited")]
    public void ADaemonThatIsNotListeningIsReportedAsNotRunning(string standardError)
    {
        Assert.Equal(DockerDaemonError.Cause.NotRunning, DockerDaemonError.Classify(standardError));

        var described = DockerDaemonError.Describe(standardError);
        Assert.NotNull(described);
        Assert.StartsWith(DockerDaemonError.Unreachable, described, StringComparison.Ordinal);
        Assert.Contains("systemctl start docker", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// The permission failure also says "connect to the Docker daemon socket", so
    /// it has to be tested before the connectivity check — telling an operator to
    /// start a daemon that is already running sends them the wrong way entirely.
    /// </summary>
    [Fact]
    public void APermissionFailureIsNotMistakenForAStoppedDaemon()
    {
        const string standardError =
            "Got permission denied while trying to connect to the Docker daemon socket at "
            + "unix:///var/run/docker.sock: Get \"http://%2Fvar%2Frun%2Fdocker.sock/v1.45/containers/json\": "
            + "dial unix /var/run/docker.sock: connect: permission denied";

        Assert.Equal(DockerDaemonError.Cause.PermissionDenied, DockerDaemonError.Classify(standardError));
        Assert.Contains("docker' group", DockerDaemonError.Describe(standardError), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/bin/sh: 1: docker: command not found")]
    [InlineData("'docker' is not recognized as an internal or external command, operable program or batch file.")]
    [InlineData("exec: \"docker\": executable file not found in $PATH")]
    public void AMissingBinaryIsReportedAsNotInstalled(string standardError)
    {
        Assert.Equal(DockerDaemonError.Cause.NotInstalled, DockerDaemonError.Classify(standardError));
        Assert.Contains("not appear to be installed", DockerDaemonError.Describe(standardError), StringComparison.Ordinal);
    }

    /// <summary>
    /// Ordinary docker failures must pass through untouched: "no such container"
    /// is the answer the caller needs, and burying it under a connectivity story
    /// would be worse than the raw text ever was.
    /// </summary>
    [Theory]
    [InlineData("Error response from daemon: No such container: web")]
    [InlineData("Error response from daemon: driver failed programming external connectivity on endpoint web: "
        + "Bind for 0.0.0.0:8080 failed: port is already allocated")]
    [InlineData("")]
    [InlineData(null)]
    public void AnyOtherDockerErrorIsLeftAlone(string? standardError)
    {
        Assert.Equal(DockerDaemonError.Cause.None, DockerDaemonError.Classify(standardError));
        Assert.Null(DockerDaemonError.Describe(standardError));
    }
}
