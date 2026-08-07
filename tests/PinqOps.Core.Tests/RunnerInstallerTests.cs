using PinqOps.Tests.Fakes;
using Xunit;

namespace PinqOps.Tests;

public class RunnerInstallerTests : IDisposable
{
    private readonly string _tempDirectory;

    public RunnerInstallerTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "pinqops-tests-" + Path.GetRandomFileName());
    }

    [Fact]
    public async Task InstallAsync_FreshServer_DownloadsExtractsConfiguresAndInstallsService()
    {
        var runner = new FakeProcessRunner();
        var downloader = new FakeFileDownloader(createFile: true);
        var installer = new RunnerInstaller(runner, downloader);

        var options = RunnerInstallOptions.Create(
            "https://github.com/pinqponq/pinqops",
            "token-123",
            installDirectory: _tempDirectory);

        var result = await installer.InstallAsync(options, serviceUser: "deployer");

        Assert.True(result);

        // Downloaded the correct runner tarball.
        Assert.Single(downloader.Downloads);
        Assert.Equal(options.DownloadUrl, downloader.Downloads[0].Url);

        // Extract → configure → install service → start service, in order.
        var commandLines = runner.Invocations.Select(invocation => invocation.CommandLine).ToArray();
        Assert.Contains(commandLines, line => line.StartsWith("tar xzf"));
        Assert.Contains(commandLines, line => line.Contains("config.sh") && line.Contains("--token token-123"));
        Assert.Contains("sudo ./svc.sh install deployer", commandLines);
        Assert.Contains("sudo ./svc.sh start", commandLines);
    }

    [Fact]
    public async Task InstallAsync_ConfigFailure_StopsAndReturnsFalse()
    {
        var runner = new FakeProcessRunner((fileName, _) =>
            fileName.EndsWith("config.sh")
                ? new ProcessResult(1, string.Empty, "registration failed")
                : new ProcessResult(0, string.Empty, string.Empty));
        var downloader = new FakeFileDownloader(createFile: true);
        var installer = new RunnerInstaller(runner, downloader);

        var options = RunnerInstallOptions.Create(
            "https://github.com/pinqponq/pinqops",
            "token-123",
            installDirectory: _tempDirectory);

        var result = await installer.InstallAsync(options, serviceUser: "deployer");

        Assert.False(result);
        // svc.sh must not run after config.sh fails.
        Assert.DoesNotContain(runner.Invocations, invocation => invocation.CommandLine.Contains("svc.sh"));
    }

    [Fact]
    public async Task InstallAsync_ExistingRegistration_UninstallsOldServiceAndDeregistersBeforeConfiguring()
    {
        // A leftover runner registered to another repository: config.sh,
        // svc.sh, and the registration files are already in place.
        Directory.CreateDirectory(_tempDirectory);
        File.WriteAllText(Path.Combine(_tempDirectory, "config.sh"), "#!/bin/sh");
        File.WriteAllText(Path.Combine(_tempDirectory, "svc.sh"), "#!/bin/sh");
        File.WriteAllText(Path.Combine(_tempDirectory, ".runner"), """{"gitHubUrl":"https://github.com/old/repo"}""");

        var runner = new FakeProcessRunner();
        var installer = new RunnerInstaller(runner, new FakeFileDownloader(createFile: true));

        var options = RunnerInstallOptions.Create(
                "https://github.com/pinqponq/pinqops",
                "token-123",
                installDirectory: _tempDirectory)
            with { RemovalToken = "removal-456" };

        var result = await installer.InstallAsync(options, serviceUser: "deployer");

        Assert.True(result);

        var commandLines = runner.Invocations.Select(invocation => invocation.CommandLine).ToArray();

        // Old service stopped + uninstalled, old registration removed, and only
        // then the new registration configured — in that order.
        var stopIndex = Array.FindIndex(commandLines, line => line == "sudo ./svc.sh stop");
        var uninstallIndex = Array.FindIndex(commandLines, line => line == "sudo ./svc.sh uninstall");
        var removeIndex = Array.FindIndex(commandLines, line =>
            line.Contains("config.sh remove") && line.Contains("--token removal-456"));
        var configureIndex = Array.FindIndex(commandLines, line =>
            line.Contains("config.sh --url") && line.Contains("--token token-123"));

        Assert.True(stopIndex >= 0, "svc.sh stop must run");
        Assert.True(uninstallIndex > stopIndex, "svc.sh uninstall must follow stop");
        Assert.True(removeIndex > uninstallIndex, "config.sh remove must follow uninstall");
        Assert.True(configureIndex > removeIndex, "new registration must follow removal");
    }

    [Fact]
    public async Task InstallAsync_ExistingRegistrationWithoutRemovalToken_ForceDeletesRegistrationFiles()
    {
        Directory.CreateDirectory(_tempDirectory);
        File.WriteAllText(Path.Combine(_tempDirectory, "config.sh"), "#!/bin/sh");
        File.WriteAllText(Path.Combine(_tempDirectory, "svc.sh"), "#!/bin/sh");
        foreach (var name in new[] { ".runner", ".credentials", ".credentials_rsaparams", ".service" })
        {
            File.WriteAllText(Path.Combine(_tempDirectory, name), "stale");
        }

        var runner = new FakeProcessRunner();
        var installer = new RunnerInstaller(runner, new FakeFileDownloader(createFile: true));

        var options = RunnerInstallOptions.Create(
            "https://github.com/pinqponq/pinqops",
            "token-123",
            installDirectory: _tempDirectory);

        var result = await installer.InstallAsync(options, serviceUser: "deployer");

        Assert.True(result);
        Assert.DoesNotContain(runner.Invocations, invocation => invocation.CommandLine.Contains("config.sh remove"));
        foreach (var name in new[] { ".runner", ".credentials", ".credentials_rsaparams", ".service" })
        {
            Assert.False(File.Exists(Path.Combine(_tempDirectory, name)), $"{name} must be deleted");
        }
    }

    [Fact]
    public async Task InstallAsync_FreshServer_RunsNoCleanup()
    {
        var runner = new FakeProcessRunner();
        var installer = new RunnerInstaller(runner, new FakeFileDownloader(createFile: true));

        var options = RunnerInstallOptions.Create(
            "https://github.com/pinqponq/pinqops",
            "token-123",
            installDirectory: _tempDirectory);

        await installer.InstallAsync(options, serviceUser: "deployer");

        Assert.DoesNotContain(runner.Invocations, invocation =>
            invocation.CommandLine.Contains("svc.sh stop")
            || invocation.CommandLine.Contains("svc.sh uninstall")
            || invocation.CommandLine.Contains("config.sh remove"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
