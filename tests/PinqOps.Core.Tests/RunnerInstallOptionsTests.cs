using Xunit;

namespace PinqOps.Tests;

public class RunnerInstallOptionsTests
{
    private const string RepoUrl = "https://github.com/pinqponq/pinqops";
    private const string Token = "AAAA-registration-token";

    [Fact]
    public void Create_AppliesDefaults()
    {
        var options = RunnerInstallOptions.Create(RepoUrl, Token);

        Assert.Equal(RepoUrl, options.RepositoryUrl);
        Assert.Equal(Token, options.RegistrationToken);
        Assert.Equal(RunnerInstallOptions.DefaultLabels, options.Labels);
        Assert.Equal(RunnerInstallOptions.DefaultRunnerVersion, options.RunnerVersion);
        Assert.Equal(RunnerInstallOptions.DefaultInstallDirectory, options.InstallDirectory);
        Assert.EndsWith("-pinqops", options.RunnerName);
    }

    [Theory]
    [InlineData(null, Token)]
    [InlineData("", Token)]
    [InlineData(RepoUrl, null)]
    [InlineData(RepoUrl, "")]
    public void Create_RejectsMissingRequired(string? repoUrl, string? token)
    {
        Assert.Throws<ArgumentException>(() => RunnerInstallOptions.Create(repoUrl, token));
    }

    [Fact]
    public void DownloadUrl_UsesRunnerVersion()
    {
        var options = RunnerInstallOptions.Create(RepoUrl, Token, runnerVersion: "2.320.0");

        Assert.Equal(
            "https://github.com/actions/runner/releases/download/v2.320.0/actions-runner-linux-x64-2.320.0.tar.gz",
            options.DownloadUrl);
    }

    [Fact]
    public void ConfigureArguments_ContainUrlTokenLabelsAndUnattended()
    {
        var options = RunnerInstallOptions.Create(RepoUrl, Token, labels: "pinqops-prod");
        var arguments = options.ConfigureArguments();

        AssertPair(arguments, "--url", RepoUrl);
        AssertPair(arguments, "--token", Token);
        AssertPair(arguments, "--labels", "pinqops-prod");
        Assert.Contains("--unattended", arguments);
        Assert.Contains("--replace", arguments);
    }

    private static void AssertPair(IReadOnlyList<string> arguments, string flag, string expectedValue)
    {
        var index = arguments.ToList().IndexOf(flag);
        Assert.True(index >= 0 && index < arguments.Count - 1, $"flag {flag} not found with a value");
        Assert.Equal(expectedValue, arguments[index + 1]);
    }
}
