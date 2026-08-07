using Microsoft.Extensions.Logging.Abstractions;
using PinqOps;
using PinqOps.Deploy;
using PinqOps.Proxy;
using PinqOps.Secrets;
using PinqOps.Web.Tests.Fakes;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// Giving an app's host port back to its own container.
///
/// <para>There is no way to hand a listening socket from one container to another,
/// so the proxy has to let the port go before the app can bind it. That makes the
/// middle of this a window where nothing is listening — and unlike enrolling, which
/// is wrapped in a rollback for exactly this reason, unenrolling ran the fallible
/// half after the proxy had already given the port up, with nothing to undo it. A
/// failure there left the app published by neither, and said nothing.</para>
/// </summary>
public class AppPortEnrollmentTests : IDisposable
{
    private const string Enrolled = """
        name: "shop"

        services:
          app:
            image: ${PINQOPS_IMAGE:-ghcr.io/acme/shop}:${PINQOPS_TAG:-latest}
            expose:
              # pinqops: the proxy publishes this port — - "${PINQOPS_HOST_PORT:-8080}:${PINQOPS_CONTAINER_PORT:-3000}"
              - "${PINQOPS_CONTAINER_PORT:-3000}"
        """;

    private readonly string _directory;
    private readonly string _composeFile;
    private readonly string _proxyDirectory;

    public AppPortEnrollmentTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-enrollment-").FullName;
        _composeFile = Path.Combine(_directory, "docker-compose.yml");
        _proxyDirectory = Path.Combine(_directory, "proxy");
        File.WriteAllText(_composeFile, Enrolled);
        EnvFileStore.SetValue(PinqOpsStatePaths.EnvFile(_composeFile), Deployer.AliasVariable, "shop");
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private AppConnection App() => new()
    {
        Id = "shop",
        RepoUrl = "https://github.com/acme/shop",
        ComposeFile = _composeFile,
        RunnerDirectory = Path.Combine(_directory, "runner"),
    };

    /// <summary>
    /// Answers every docker call successfully except <c>compose up</c>, which is the
    /// last step of the sequence and the one that can genuinely fail — a port the
    /// app cannot bind, an image that will not start, a daemon that has gone away.
    /// </summary>
    private static FakeProcessRunner Runner(bool composeUpFails)
    {
        return new((_, arguments) =>
        {
            if (composeUpFails && arguments.Contains("up"))
            {
                return new ProcessResult(1, string.Empty, "Error response from daemon: port is already allocated");
            }

            if (arguments.Contains("inspect"))
            {
                return new ProcessResult(0, "true\n", string.Empty);
            }

            return new ProcessResult(0, string.Empty, string.Empty);
        });
    }

    private ProxyService Proxy(FakeProcessRunner runner) => new(
        new DockerService(runner),
        runner,
        new SecretStore(Path.Combine(_directory, "secrets.json")),
        NullLogger<ProxyService>.Instance,
        _proxyDirectory);

    private AppPortEnrollment Enrollment(FakeProcessRunner runner, ProxyService proxy) => new(
        proxy,
        new DeployService(runner, proxy),
        new UiConfigStore(Path.Combine(_directory, "ui.json")),
        NullLogger<AppPortEnrollment>.Instance);

    /// <summary>The state a successfully enrolled app is in: the proxy holds its port.</summary>
    private void ProxyHoldsThePort(ProxyService proxy) =>
        proxy.Store.Save(new DomainConfig
        {
            Ports =
            [
                new PortEntry
                {
                    HostPort = 8080,
                    Target = "shop",
                    TargetContainer = "shop-app-1",
                    TargetPort = 3000,
                    Enabled = true,
                },
            ],
        });

    /// <summary>
    /// An app that deploys without a gap can no more publish its own port than one
    /// running three copies can: both colours would want the same one. The copy count
    /// already comes down with the port; the colours have to as well.
    ///
    /// <para>Left on, the unenrol removes the network alias the colours are reached
    /// by and then asks compose to bring a colour up — which cannot be worked out
    /// without that alias, so it fails at the last step every time. The rollback puts
    /// everything back, so nothing breaks, but the button can never succeed and the
    /// refusal tells the operator to hand the port to the proxy: the exact opposite
    /// of what they asked for.</para>
    /// </summary>
    [Fact]
    public async Task UnenrollingAnAppThatDeploysWithoutAGapStopsUsingColours()
    {
        var runner = Runner(composeUpFails: false);
        var proxy = Proxy(runner);
        ProxyHoldsThePort(proxy);
        new DeploySettingsStore(_composeFile).Save(new DeploySettings
        {
            BlueGreen = true,
            ProxyTarget = "shop",
            ActiveColor = DeployColors.Green,
        });
        var enrollment = Enrollment(runner, proxy);

        var result = await enrollment.UnenrollAsync(App());

        Assert.False(result.Failed);
        Assert.False(enrollment.IsEnrolled(App()));
        Assert.False(new DeploySettingsStore(_composeFile).Load().BlueGreen);
    }

    /// <summary>
    /// And a failed unenrol puts the colours back with everything else — an app left
    /// on the proxy but no longer deploying without a gap would deploy the ordinary
    /// way into routes that still name a colour.
    /// </summary>
    [Fact]
    public async Task AFailedUnenrollPutsTheColoursBack()
    {
        var runner = Runner(composeUpFails: true);
        var proxy = Proxy(runner);
        ProxyHoldsThePort(proxy);
        new DeploySettingsStore(_composeFile).Save(new DeploySettings
        {
            BlueGreen = true,
            ProxyTarget = "shop",
            ActiveColor = DeployColors.Green,
        });

        var result = await Enrollment(runner, proxy).UnenrollAsync(App());

        Assert.True(result.Failed);
        Assert.True(new DeploySettingsStore(_composeFile).Load().BlueGreen);
    }

    [Fact]
    public async Task UnenrollingGivesThePortBackToTheApp()
    {
        var runner = Runner(composeUpFails: false);
        var proxy = Proxy(runner);
        ProxyHoldsThePort(proxy);
        var enrollment = Enrollment(runner, proxy);

        await enrollment.UnenrollAsync(App());

        Assert.False(enrollment.IsEnrolled(App()));
        Assert.Contains("ports:", await File.ReadAllTextAsync(_composeFile), StringComparison.Ordinal);
    }

    /// <summary>
    /// The failure this is about. The proxy has already given the port up by the
    /// time compose runs, so if compose will not take it, nothing publishes the app
    /// — and the operator is told a step failed, not that their app is gone.
    /// </summary>
    [Fact]
    public async Task AFailedUnenrollLeavesThePortWithTheProxy()
    {
        var runner = Runner(composeUpFails: true);
        var proxy = Proxy(runner);
        ProxyHoldsThePort(proxy);
        var enrollment = Enrollment(runner, proxy);

        var result = await enrollment.UnenrollAsync(App());

        Assert.True(result.Failed);
        Assert.True(enrollment.IsEnrolled(App()), "the proxy should still publish the app it never handed over");

        var entry = Assert.Single(proxy.Store.Load().Ports);
        Assert.Equal(8080, entry.HostPort);
        Assert.Equal("shop-app-1", entry.TargetContainer);
        Assert.Equal(3000, entry.TargetPort);
    }

    /// <summary>
    /// And the compose file goes back with it, or the two disagree about who
    /// publishes the port — which is the next deploy failing on a port collision.
    /// </summary>
    [Fact]
    public async Task AFailedUnenrollPutsTheComposeFileBack()
    {
        var runner = Runner(composeUpFails: true);
        var proxy = Proxy(runner);
        ProxyHoldsThePort(proxy);

        await Enrollment(runner, proxy).UnenrollAsync(App());

        Assert.Equal(Enrolled, await File.ReadAllTextAsync(_composeFile));
        Assert.Equal("shop", EnvFileStore.GetValue(PinqOpsStatePaths.EnvFile(_composeFile), Deployer.AliasVariable));
    }
}
