using PinqOps.Tests.Fakes;
using Xunit;

namespace PinqOps.Tests;

public class ComposeHealthCheckerTests
{
    private const string ComposePath = "/opt/pinqops/docker-compose.yml";

    private static ComposeHealthChecker Checker(FakeProcessRunner runner) =>
        new(runner, pollInterval: TimeSpan.FromMilliseconds(1));

    private static FakeProcessRunner PsRunner(string output, int exitCode = 0) =>
        new((_, _) => new ProcessResult(exitCode, output, exitCode == 0 ? string.Empty : "ps failed"));

    [Fact]
    public async Task AllRunningAndHealthy_Passes()
    {
        var runner = PsRunner(
            """{"Name":"web","State":"running","Health":"healthy"}""" + "\n" +
            """{"Name":"db","State":"running","Health":"healthy"}""");

        var failure = await Checker(runner).WaitForHealthyAsync(ComposePath, TimeSpan.FromSeconds(1));

        Assert.Null(failure);
    }

    [Fact]
    public async Task RunningWithoutHealthcheck_Passes()
    {
        var runner = PsRunner("""{"Name":"web","State":"running","Health":""}""");

        var failure = await Checker(runner).WaitForHealthyAsync(ComposePath, TimeSpan.FromSeconds(1));

        Assert.Null(failure);
    }

    [Fact]
    public async Task ExitedService_FailsImmediately()
    {
        var runner = PsRunner("""{"Name":"web","State":"exited","Health":""}""");

        var failure = await Checker(runner).WaitForHealthyAsync(ComposePath, TimeSpan.FromSeconds(30));

        Assert.NotNull(failure);
        Assert.Contains("exited", failure);
        Assert.Single(runner.Invocations); // no polling after a fatal state
    }

    [Fact]
    public async Task UnhealthyService_FailsImmediately()
    {
        var runner = PsRunner("""{"Name":"web","State":"running","Health":"unhealthy"}""");

        var failure = await Checker(runner).WaitForHealthyAsync(ComposePath, TimeSpan.FromSeconds(30));

        Assert.NotNull(failure);
        Assert.Contains("unhealthy", failure);
    }

    [Fact]
    public async Task StartingHealth_TimesOutWithLastState()
    {
        var runner = PsRunner("""{"Name":"web","State":"running","Health":"starting"}""");

        var failure = await Checker(runner).WaitForHealthyAsync(ComposePath, TimeSpan.FromMilliseconds(30));

        Assert.NotNull(failure);
        Assert.Contains("timed out", failure);
        Assert.Contains("starting", failure);
    }

    [Fact]
    public async Task StartingThenHealthy_PassesAfterPolling()
    {
        var calls = 0;
        var runner = new FakeProcessRunner((_, _) =>
        {
            calls++;
            var health = calls < 3 ? "starting" : "healthy";
            return new ProcessResult(0, $$"""{"Name":"web","State":"running","Health":"{{health}}"}""", string.Empty);
        });

        var failure = await Checker(runner).WaitForHealthyAsync(ComposePath, TimeSpan.FromSeconds(5));

        Assert.Null(failure);
        Assert.True(calls >= 3);
    }

    [Fact]
    public async Task ArrayFormatOutput_IsAccepted()
    {
        var runner = PsRunner("""[{"Name":"web","State":"running","Health":"healthy"}]""");

        var failure = await Checker(runner).WaitForHealthyAsync(ComposePath, TimeSpan.FromSeconds(1));

        Assert.Null(failure);
    }

    [Fact]
    public async Task PsCommandFails_ReturnsFailure()
    {
        var runner = PsRunner(string.Empty, exitCode: 1);

        var failure = await Checker(runner).WaitForHealthyAsync(ComposePath, TimeSpan.FromSeconds(1));

        Assert.NotNull(failure);
        Assert.Contains("compose ps failed", failure);
    }

    [Fact]
    public async Task NoContainers_TimesOut()
    {
        var runner = PsRunner(string.Empty);

        var failure = await Checker(runner).WaitForHealthyAsync(ComposePath, TimeSpan.FromMilliseconds(30));

        Assert.NotNull(failure);
        Assert.Contains("no containers", failure);
    }
}
