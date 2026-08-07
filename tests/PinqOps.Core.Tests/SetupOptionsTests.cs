using Xunit;

namespace PinqOps.Tests;

public class SetupOptionsTests
{
    [Fact]
    public void Create_AppliesRunnerDefaults()
    {
        var options = SetupOptions.Create();

        Assert.Equal(RunnerInstallOptions.DefaultLabels, options.Labels);
        Assert.Equal(RunnerInstallOptions.DefaultRunnerVersion, options.RunnerVersion);
        Assert.Equal(RunnerInstallOptions.DefaultInstallDirectory, options.InstallDirectory);
        Assert.Equal(SetupOptions.DefaultComposeFilePath, options.ComposeFilePath);
        Assert.True(options.UseGhCli);
        Assert.EndsWith("-pinqops", options.RunnerName);
    }

    [Fact]
    public void Create_BlankOptionalsBecomeNull()
    {
        var options = SetupOptions.Create(repositoryUrl: "  ", personalAccessToken: "", registrationToken: null);

        Assert.Null(options.RepositoryUrl);
        Assert.Null(options.PersonalAccessToken);
        Assert.Null(options.RegistrationToken);
    }

    [Fact]
    public void ToRunnerInstallOptions_CarriesTokenAndKnobs()
    {
        var options = SetupOptions.Create(labels: "custom-label", installDirectory: "/opt/x");

        var runnerOptions = options.ToRunnerInstallOptions("https://github.com/o/r", "reg-token");

        Assert.Equal("https://github.com/o/r", runnerOptions.RepositoryUrl);
        Assert.Equal("reg-token", runnerOptions.RegistrationToken);
        Assert.Equal("custom-label", runnerOptions.Labels);
        Assert.Equal("/opt/x", runnerOptions.InstallDirectory);
    }
}
