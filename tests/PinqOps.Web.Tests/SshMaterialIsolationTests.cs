using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Tests.Web;

/// <summary>
/// Where booting the dashboard puts the SSH config it writes.
///
/// <para>The managed block is rebuilt from the environment registry every time the
/// host starts, into the SSH config of whoever the process runs as — under test,
/// the person who typed <c>dotnet test</c>. So a run edited their own
/// <c>~/.ssh/config</c>, left the fixtures' hosts aliased in it, and left different
/// hosts there depending on which fixture booted last. These drive one boot through
/// the real <c>Program</c> and watch both files: the one the run owns, and the one
/// it has no business touching.</para>
/// </summary>
[Collection(TestServerCollection.Name)]
public sealed class SshMaterialIsolationTests : IClassFixture<SshMaterialIsolationTests.AppFixture>
{
    private readonly SshMaterialSandbox _sandbox;

    private readonly AppFixture _app;

    public SshMaterialIsolationTests(SshMaterialSandbox sandbox, AppFixture app)
    {
        _sandbox = sandbox;
        _app = app;
    }

    /// <summary>
    /// The SSH material a boot derives from the registry belongs inside the run's
    /// own scope, where it is thrown away with everything else the run created.
    /// </summary>
    [Fact]
    public void ABootWritesTheManagedBlockInsideTheRunsOwnScope()
    {
        Assert.True(
            File.Exists(_sandbox.ConfigPath),
            "booting the host wrote no SSH config inside the directory the run owns.");

        var written = File.ReadAllText(_sandbox.ConfigPath);
        Assert.Contains(SshConfigGenerator.BeginMarker, written);
        Assert.Contains(SshConfigGenerator.AliasFor(AppFixture.EnvironmentId), written);
    }

    /// <summary>
    /// And the config of whoever ran the suite is left exactly as it was found —
    /// a test run is not entitled to edit a file outside the repository, least of
    /// all the one that decides which host an <c>ssh</c> lands on.
    /// </summary>
    [Fact]
    public void ABootLeavesTheRealSshConfigAlone() =>
        Assert.Equal(_app.RealSshConfigBeforeBoot, AppFixture.FingerprintRealSshConfig());

    public sealed class AppFixture : WebApplicationFactory<Program>, IAsyncLifetime
    {
        /// <summary>
        /// A host reached over SSH, because a local one produces no managed entry to
        /// look for.
        /// </summary>
        public const string EnvironmentId = "sandbox";

        /// <summary>Stands in for the fingerprint when there is no file to take one of.</summary>
        private const string NoSshConfig = "(absent)";

        private readonly string _directory;

        public AppFixture()
        {
            _directory = Path.Combine(Path.GetTempPath(), "pinqops-sshmaterial-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Environment.SetEnvironmentVariable("PINQOPS_UI_CONFIG", Path.Combine(_directory, "ui.json"));
            Environment.SetEnvironmentVariable("PINQOPS_AUDIT_LOG", Path.Combine(_directory, "audit.jsonl"));

            new UiConfigStore(Path.Combine(_directory, "ui.json")).Update(config =>
                config.Environments.Add(new ManagedEnvironment
                {
                    Id = EnvironmentId,
                    Name = EnvironmentId,
                    Transport = ManagedEnvironment.TransportSsh,
                    Host = "10.0.0.9",
                    User = "deploy",
                }));

            RealSshConfigBeforeBoot = FingerprintRealSshConfig();
        }

        /// <summary>The real config as this fixture found it, taken before the host boots.</summary>
        public string RealSshConfigBeforeBoot { get; }

        /// <summary>
        /// A hash rather than the text. This is a real person's file: a failed
        /// comparison has to say that it changed without printing what is in it.
        /// </summary>
        public static string FingerprintRealSshConfig()
        {
            var path = EnvironmentService.DefaultSshConfigPath;
            return File.Exists(path)
                ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
                : NoSshConfig;
        }

        /// <summary>Booting the host is the whole act under test; no request follows it.</summary>
        public Task InitializeAsync()
        {
            using var client = CreateClient();
            return Task.CompletedTask;
        }

        public new async Task DisposeAsync()
        {
            await base.DisposeAsync();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
