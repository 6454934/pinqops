using Microsoft.Extensions.Logging.Abstractions;
using PinqOps;
using PinqOps.Deploy;
using PinqOps.Proxy;
using PinqOps.Secrets;
using PinqOps.Web.Tests.Fakes;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// Turning deploys-without-a-gap off again.
///
/// <para>Turning it on is self-correcting: the first coloured deploy points the
/// routes at the colour it started. Turning it off has no such follow-up, and the
/// component that re-derives these routes at every restart skips an app that is no
/// longer deploying in colours — so nothing ever moves them back. Every later
/// ordinary deploy then creates containers on the plain alias, reports success, and
/// is served to nobody, while the colour left over from before the toggle goes on
/// answering. The operator sees a green deploy and the release from last week.</para>
/// </summary>
public class BlueGreenToggleTests : IDisposable
{
    private const string Enrolled = """
        name: "shop"

        services:
          app:
            image: ${PINQOPS_IMAGE:-ghcr.io/acme/shop}:${PINQOPS_TAG:-latest}
            expose:
              - "${PINQOPS_CONTAINER_PORT:-3000}"
        """;

    private readonly string _directory;
    private readonly string _composeFile;
    private readonly string _proxyDirectory;

    public BlueGreenToggleTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-bluegreen-toggle-").FullName;
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

    private static FakeProcessRunner Runner() => new((_, arguments) =>
        arguments.Contains("inspect")
            ? new ProcessResult(0, "true\n", string.Empty)
            : new ProcessResult(0, string.Empty, string.Empty));

    private ProxyService Proxy(FakeProcessRunner runner) => new(
        new DockerService(runner),
        runner,
        new SecretStore(Path.Combine(_directory, "secrets.json")),
        NullLogger<ProxyService>.Instance,
        _proxyDirectory);

    /// <summary>The state the last cutover left: the route names the serving colour.</summary>
    private void RoutedToTheGreenColour(ProxyService proxy) =>
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
                    Upstream = new UpstreamOptions
                    {
                        Balancing = new LoadBalancing { Alias = "shop-green", Policy = "round_robin" },
                    },
                },
            ],
        });

    private DeploySettings Disabled()
    {
        var store = new DeploySettingsStore(_composeFile);
        store.Save(new DeploySettings
        {
            BlueGreen = true,
            ProxyTarget = "shop",
            ActiveColor = DeployColors.Green,
            Replicas = 2,
        });
        return store.Update(settings => settings.BlueGreen = false);
    }

    /// <summary>
    /// The routes have to come off the colour, or every deploy from here is a
    /// success that serves nobody.
    /// </summary>
    [Fact]
    public async Task TurningTheColoursOffTakesTheRoutesOffTheColour()
    {
        var runner = Runner();
        var proxy = Proxy(runner);
        RoutedToTheGreenColour(proxy);

        await DeployEndpoints.StopUsingColorsAsync("shop", _composeFile, Disabled(), new DeployService(runner, proxy), proxy);

        Assert.Equal("shop", proxy.Store.Load().Ports[0].Upstream?.Balancing?.Alias);
    }

    /// <summary>
    /// And something has to be answering that name before the routes arrive at it.
    /// A colour's containers answer <c>shop-green</c> and nothing else, so
    /// re-pointing on its own would take the app off the air until the next deploy —
    /// from an edit to a checkbox.
    /// </summary>
    [Fact]
    public async Task TheOrdinaryProjectIsBroughtUpBeforeTheRoutesMove()
    {
        var runner = Runner();
        var proxy = Proxy(runner);
        RoutedToTheGreenColour(proxy);

        await DeployEndpoints.StopUsingColorsAsync("shop", _composeFile, Disabled(), new DeployService(runner, proxy), proxy);

        var up = Assert.Single(runner.Invocations, invocation => invocation.Arguments.Contains("up"));
        Assert.Equal(["compose", "-f", _composeFile, "up", "-d", "--scale", "app=2"], up.Arguments);
    }
}
