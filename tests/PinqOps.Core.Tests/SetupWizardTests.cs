using PinqOps.Tests.Fakes;
using Xunit;

namespace PinqOps.Tests;

public class SetupWizardTests : IDisposable
{
    private const string RepoUrl = "https://github.com/pinqponq/pinqops";

    private readonly string _tempDirectory;

    public SetupWizardTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "pinqops-setup-" + Path.GetRandomFileName());
    }

    private static SetupWizard Build(FakeProcessRunner runner, FakeGitHubApiClient apiClient, FakePrompt prompt)
    {
        var downloader = new FakeFileDownloader(createFile: true);
        var checker = new PrerequisiteChecker(runner);
        // Pretend we are on Ubuntu so a missing docker triggers the installer
        // path instead of the "unsupported distribution" early exit.
        var bootstrapper = new DockerBootstrapper(
            runner,
            readOsRelease: () => "ID=ubuntu\nVERSION_CODENAME=jammy\n");
        var resolver = new RegistrationTokenResolver(new GhCli(runner), apiClient, prompt);
        var installer = new RunnerInstaller(runner, downloader);
        return new SetupWizard(checker, bootstrapper, resolver, installer, prompt);
    }

    [Fact]
    public async Task RunAsync_HappyPath_InstallsRunnerWithResolvedToken()
    {
        // All probes/install steps succeed; gh mints "reg-token-from-gh".
        var runner = new FakeProcessRunner((fileName, arguments) =>
            fileName == "gh" && arguments.Contains("api")
                ? new ProcessResult(0, "reg-token-from-gh", string.Empty)
                : new ProcessResult(0, string.Empty, string.Empty));
        var wizard = Build(runner, new FakeGitHubApiClient(), new FakePrompt());
        var options = SetupOptions.Create(
            repositoryUrl: RepoUrl,
            installDirectory: _tempDirectory,
            composeFilePath: Path.Combine(_tempDirectory, "docker-compose.yml"));

        var result = await wizard.RunAsync(options);

        Assert.True(result);
        Assert.Contains(runner.Invocations, invocation =>
            invocation.CommandLine.Contains("config.sh") && invocation.CommandLine.Contains("--token reg-token-from-gh"));
    }

    [Fact]
    public async Task RunAsync_LeftoverRunnerForAnotherRepo_MintsRemovalTokenAndDeregistersIt()
    {
        // A runner registered to another repository already lives in the
        // install directory; gh mints both tokens.
        Directory.CreateDirectory(_tempDirectory);
        File.WriteAllText(Path.Combine(_tempDirectory, "config.sh"), "#!/bin/sh");
        File.WriteAllText(Path.Combine(_tempDirectory, "svc.sh"), "#!/bin/sh");
        File.WriteAllText(Path.Combine(_tempDirectory, ".runner"), """{"gitHubUrl":"https://github.com/old/repo"}""");

        var runner = new FakeProcessRunner((fileName, arguments) =>
        {
            if (fileName == "gh" && arguments.Contains("api"))
            {
                var isRemoval = arguments.Any(argument => argument.Contains("remove-token"));
                return new ProcessResult(0, isRemoval ? "removal-from-gh" : "reg-from-gh", string.Empty);
            }

            return new ProcessResult(0, string.Empty, string.Empty);
        });
        var wizard = Build(runner, new FakeGitHubApiClient(), new FakePrompt());
        var options = SetupOptions.Create(repositoryUrl: RepoUrl, installDirectory: _tempDirectory);

        var result = await wizard.RunAsync(options);

        Assert.True(result);
        // The old registration is removed with the removal token minted for
        // the OLD repository, then the new one is configured.
        Assert.Contains(runner.Invocations, invocation =>
            invocation.CommandLine.Contains("actions/runners/remove-token") && invocation.CommandLine.Contains("old/repo"));
        Assert.Contains(runner.Invocations, invocation =>
            invocation.CommandLine.Contains("config.sh remove") && invocation.CommandLine.Contains("--token removal-from-gh"));
        Assert.Contains(runner.Invocations, invocation =>
            invocation.CommandLine.Contains("config.sh --url") && invocation.CommandLine.Contains("--token reg-from-gh"));
    }

    [Fact]
    public async Task RunAsync_MissingPrerequisite_StopsBeforeTokenAndInstall()
    {
        // Docker stays broken even after the bootstrapper's install script
        // "succeeds", so setup must stop before minting a runner token.
        var runner = new FakeProcessRunner((fileName, _) =>
            fileName == "docker"
                ? new ProcessResult(1, string.Empty, "no docker")
                : new ProcessResult(0, string.Empty, string.Empty));
        var apiClient = new FakeGitHubApiClient();
        var wizard = Build(runner, apiClient, new FakePrompt());
        var options = SetupOptions.Create(repositoryUrl: RepoUrl, installDirectory: _tempDirectory);

        var result = await wizard.RunAsync(options);

        Assert.False(result);
        Assert.Empty(apiClient.Calls);
        Assert.Contains(runner.Invocations, invocation =>
            invocation.CommandLine.Contains("sudo") && invocation.CommandLine.Contains("apt-get"));
        Assert.DoesNotContain(runner.Invocations, invocation => invocation.CommandLine.Contains("config.sh"));
    }

    [Fact]
    public async Task RunAsync_NonInteractive_NoRepoUrl_Throws()
    {
        var wizard = Build(new FakeProcessRunner(), new FakeGitHubApiClient(), new FakePrompt());
        var options = SetupOptions.Create(
            nonInteractive: true,
            skipPreflight: true,
            installDirectory: _tempDirectory);

        await Assert.ThrowsAsync<ArgumentException>(() => wizard.RunAsync(options));
    }

    [Fact]
    public async Task RunAsync_InstallStepFails_ReturnsFalse()
    {
        var runner = new FakeProcessRunner((fileName, arguments) =>
        {
            if (fileName == "gh" && arguments.Contains("api"))
            {
                return new ProcessResult(0, "reg-token", string.Empty);
            }

            return fileName.EndsWith("config.sh")
                ? new ProcessResult(1, string.Empty, "registration failed")
                : new ProcessResult(0, string.Empty, string.Empty);
        });
        var wizard = Build(runner, new FakeGitHubApiClient(), new FakePrompt());
        var options = SetupOptions.Create(repositoryUrl: RepoUrl, installDirectory: _tempDirectory);

        var result = await wizard.RunAsync(options);

        Assert.False(result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
