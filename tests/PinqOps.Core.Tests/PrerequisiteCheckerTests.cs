using PinqOps.Tests.Fakes;
using Xunit;

namespace PinqOps.Tests;

public class PrerequisiteCheckerTests
{
    [Fact]
    public async Task CheckAsync_AllToolsPresent_ReportsAllPresent()
    {
        var checker = new PrerequisiteChecker(new FakeProcessRunner());

        var report = await checker.CheckAsync();

        Assert.True(report.AllPresent);
        Assert.Empty(report.Missing);
    }

    [Fact]
    public async Task CheckAsync_DockerMissing_ReportsItWithHint()
    {
        var runner = new FakeProcessRunner((fileName, _) =>
            fileName == "docker"
                ? new ProcessResult(127, string.Empty, "not found")
                : new ProcessResult(0, string.Empty, string.Empty));
        var checker = new PrerequisiteChecker(runner);

        var report = await checker.CheckAsync();

        Assert.False(report.AllPresent);
        Assert.Contains(report.Missing, result => result.Name == "Docker Engine");
        Assert.Contains(report.Missing, result => result.InstallHint.Contains("docker"));
    }

    [Fact]
    public async Task CheckAsync_ToolNotOnPath_TreatedAsMissing()
    {
        var runner = new FakeProcessRunner((fileName, _) =>
        {
            if (fileName == "tar")
            {
                throw new InvalidOperationException("binary not found");
            }

            return new ProcessResult(0, string.Empty, string.Empty);
        });
        var checker = new PrerequisiteChecker(runner);

        var report = await checker.CheckAsync();

        Assert.False(report.AllPresent);
        Assert.Contains(report.Missing, result => result.Name == "tar");
    }
}
