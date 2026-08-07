using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PinqOps;
using PinqOps.Web.Tests.Fakes;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// What a catalog install leaves behind when <c>docker run</c> creates the
/// container and then fails to start it — the port is already taken, the daemon
/// exits 125, and the container stays in Created state.
///
/// <para>Nothing wrote an ownership record for it (the install threw before that
/// line), yet it carries the app label and <c>docker ps -a</c> lists it, so the
/// dashboard offered a Remove the ownership gate then refused, and a retry hit
/// "container name is already in use" for ever. The failing install has to take
/// its own wreckage with it.</para>
/// </summary>
public sealed class CatalogInstallFailureTests
{
    /// <summary>A catalog app whose single published port is the one already taken.</summary>
    private const string CatalogAppId = "grafana";

    private const string ContainerName = AppCatalog.ContainerPrefix + CatalogAppId;

    /// <summary>What docker exits with when it created the container but could not start it.</summary>
    private const int PortBindExitCode = 125;

    private const string PortBindFailure =
        "docker: Error response from daemon: driver failed programming external connectivity on endpoint "
        + ContainerName + ": Bind for 127.0.0.1:3000 failed: bind: address already in use.";

    private const string NoSuchContainerFailure = "Error response from daemon: No such container: " + ContainerName;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    private const int MaxPollAttempts = 250;

    [Fact]
    public async Task AFailingInstallRemovesTheContainerItCreated()
    {
        await using var host = await InstallHost.StartAsync(
            FailingInstallRunner(containerExistedBefore: false, cleanupSucceeds: true));

        var job = await host.InstallAsync(CatalogAppId);

        Assert.Equal("error", job.GetProperty("phase").GetString());
        // The original failure still reaches the operator: the cleanup is not
        // allowed to replace the reason the install failed.
        Assert.Contains("address already in use", job.GetProperty("error").GetString());
        Assert.Contains(host.Runner.Invocations, invocation =>
            invocation.Arguments.SequenceEqual<string>(["rm", "-f", "--", ContainerName]));
    }

    /// <summary>
    /// The guard that keeps the cleanup honest. When the run failed on a name
    /// conflict the container was already there — someone else's, with someone
    /// else's data — and removing it would be the worse bug.
    /// </summary>
    [Fact]
    public async Task AFailingInstallLeavesAContainerThatAlreadyExistedAlone()
    {
        await using var host = await InstallHost.StartAsync(
            FailingInstallRunner(containerExistedBefore: true, cleanupSucceeds: true));

        var job = await host.InstallAsync(CatalogAppId);

        Assert.Equal("error", job.GetProperty("phase").GetString());
        Assert.DoesNotContain(host.Runner.Invocations, invocation => invocation.Arguments.Contains("rm"));
    }

    /// <summary>
    /// A cleanup that cannot find the container is the ordinary case for a run
    /// that failed before creating anything, so it must not become the error the
    /// operator reads instead of the real one.
    /// </summary>
    [Fact]
    public async Task AFailedCleanupDoesNotReplaceTheInstallError()
    {
        await using var host = await InstallHost.StartAsync(
            FailingInstallRunner(containerExistedBefore: false, cleanupSucceeds: false));

        var job = await host.InstallAsync(CatalogAppId);

        Assert.Equal("error", job.GetProperty("phase").GetString());
        Assert.Contains("address already in use", job.GetProperty("error").GetString());
    }

    /// <summary>
    /// A daemon where <c>docker run</c> creates the container and then fails to
    /// start it. Container existence is answered by <c>docker inspect</c>, which
    /// is how <see cref="DockerService.ContainerStateAsync"/> asks.
    /// </summary>
    private static FakeProcessRunner FailingInstallRunner(bool containerExistedBefore, bool cleanupSucceeds) =>
        new((_, arguments) => arguments.Count == 0
            ? new ProcessResult(0, string.Empty, string.Empty)
            : arguments[0] switch
            {
                "run" => new ProcessResult(PortBindExitCode, string.Empty, PortBindFailure),
                "inspect" => containerExistedBefore
                    ? new ProcessResult(0, "false", string.Empty)
                    : new ProcessResult(1, string.Empty, NoSuchContainerFailure),
                "rm" => cleanupSucceeds
                    ? new ProcessResult(0, ContainerName, string.Empty)
                    : new ProcessResult(1, string.Empty, NoSuchContainerFailure),
                _ => new ProcessResult(0, string.Empty, string.Empty),
            });

    /// <summary>
    /// The app endpoints on a test server, with docker replaced by a fake process
    /// runner and the credential/ownership stores pointed at a temporary directory.
    /// </summary>
    private sealed class InstallHost : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly string _directory;

        private InstallHost(WebApplication application, string directory, FakeProcessRunner runner)
        {
            _application = application;
            _directory = directory;
            Runner = runner;
            Client = application.GetTestClient();
        }

        public FakeProcessRunner Runner { get; }

        public HttpClient Client { get; }

        public static async Task<InstallHost> StartAsync(FakeProcessRunner runner)
        {
            var directory = Directory.CreateTempSubdirectory("pinqops-install-failure-").FullName;

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<IProcessRunner>(runner);
            builder.Services.AddSingleton(new DockerService(runner));
            builder.Services.AddSingleton<AppInstallJobs>();
            builder.Services.AddSingleton(new AppCredentialStore(Path.Combine(directory, "app-credentials.json")));
            builder.Services.AddSingleton(new ContainerOwnershipStore(Path.Combine(directory, "container-owners.json")));

            var application = builder.Build();
            application.MapAppEndpoints(NullLogger.Instance);
            await application.StartAsync();

            return new InstallHost(application, directory, runner);
        }

        /// <summary>Starts an install and returns its job once it reaches a terminal phase.</summary>
        public async Task<JsonElement> InstallAsync(string appId)
        {
            var started = await Client.PostAsJsonAsync("/api/apps/install", new { id = appId });
            started.EnsureSuccessStatusCode();
            var jobId = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString();

            for (var attempt = 0; attempt < MaxPollAttempts; attempt++)
            {
                var response = await Client.GetAsync($"/api/apps/install/{jobId}");
                response.EnsureSuccessStatusCode();
                var job = await response.Content.ReadFromJsonAsync<JsonElement>();
                if (job.GetProperty("done").GetBoolean())
                {
                    return job;
                }

                await Task.Delay(PollInterval);
            }

            throw new TimeoutException($"The install job for '{appId}' never finished.");
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _application.StopAsync();
            await _application.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
