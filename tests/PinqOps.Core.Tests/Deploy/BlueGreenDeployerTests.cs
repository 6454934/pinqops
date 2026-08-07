using PinqOps.Deploy;
using PinqOps.Proxy;
using PinqOps.Tests.Fakes;
using Xunit;

namespace PinqOps.Tests.Deploy;

/// <summary>
/// The cutover: start the other colour, prove it, then give it the traffic. Every
/// test here is about the ordering — which is the whole design, because every step
/// before the switch has to be undoable by doing nothing at all.
/// </summary>
public class BlueGreenDeployerTests : IDisposable
{
    private const string Eligible = """
        name: "shop"

        services:
          app:
            image: ${PINQOPS_IMAGE:-ghcr.io/acme/shop}:${PINQOPS_TAG:-latest}
            expose:
              # pinqops: the proxy publishes this port — - "${PINQOPS_HOST_PORT:-8080}:${PINQOPS_CONTAINER_PORT:-80}"
        """;

    private const string HealthyPs =
        """{"Name":"shop-green-app-1","Service":"app","State":"running","Health":"healthy"}""";

    private readonly string _directory;
    private readonly string _composePath;
    private readonly string _proxyDirectory;

    public BlueGreenDeployerTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-bluegreen-tests").FullName;
        _composePath = Path.Combine(_directory, "docker-compose.yml");
        _proxyDirectory = Path.Combine(_directory, "proxy");
        File.WriteAllText(_composePath, Eligible);
        EnvFileStore.SetValue(PinqOpsStatePaths.EnvFile(_composePath), Deployer.AliasVariable, "shop");
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Answers ps healthy, reports the proxy container as running so the gateway
    /// reaches its reload, and lets everything else — including `caddy validate` —
    /// succeed.
    /// </summary>
    private static FakeProcessRunner Runner(string ps = HealthyPs) =>
        new((_, arguments) =>
        {
            if (arguments.Contains("ps"))
            {
                return new ProcessResult(0, ps, string.Empty);
            }

            if (arguments.Contains("inspect"))
            {
                return new ProcessResult(0, "true\n", string.Empty);
            }

            return new ProcessResult(0, string.Empty, string.Empty);
        });

    private ProxyGateway Gateway(FakeProcessRunner runner)
    {
        var gateway = new ProxyGateway(runner, _proxyDirectory, "caddy:test");
        gateway.Store.Save(new DomainConfig
        {
            Ports =
            [
                new PortEntry
                {
                    HostPort = 8080,
                    Target = "shop",
                    TargetContainer = "shop-app-1",
                    TargetPort = 80,
                },
            ],
        });
        return gateway;
    }

    private BlueGreenOptions Options() => new()
    {
        ComposeFilePath = _composePath,
        Target = "shop",
        Project = "shop",
        Alias = "shop",
        ContainerPort = 80,
        Tag = "sha-abc123",
        HealthCheckTimeout = TimeSpan.FromSeconds(5),
        DrainSeconds = 0,
    };

    private void Settings(Action<DeploySettings> configure)
    {
        var store = new DeploySettingsStore(_composePath);
        var settings = store.Load();
        settings.BlueGreen = true;
        configure(settings);
        store.Save(settings);
    }

    private static List<string> CommandLines(FakeProcessRunner runner) =>
        [.. runner.Invocations.Select(invocation => invocation.CommandLine)];

    private string ActiveColor() => new DeploySettingsStore(_composePath).Load().ActiveColor;

    [Fact]
    public async Task AFirstDeployGoesToTheOtherColourAndTakesTheTraffic()
    {
        Settings(_ => { });
        var runner = Runner();

        var result = await new BlueGreenDeployer(runner, Gateway(runner)).DeployAsync(Options());

        Assert.True(result.Succeeded);
        Assert.Equal(DeployColors.Green, result.Color);
        Assert.Equal(DeployColors.Green, ActiveColor());
    }

    /// <summary>
    /// The argument order is not cosmetic: <c>-p</c> and <c>--env-file</c> are
    /// compose's own options and have to come before the subcommand, or compose
    /// reads them as arguments to it.
    /// </summary>
    [Fact]
    public async Task TheColourIsAProjectAndAnEnvironmentFileOverTheSameComposeFile()
    {
        Settings(_ => { });
        var runner = Runner();

        await new BlueGreenDeployer(runner, Gateway(runner)).DeployAsync(Options());

        var green = ColorEnvironment.FileFor(_composePath, DeployColors.Green);
        Assert.Contains(
            $"docker compose -p shop-green --env-file {green} -f {_composePath} up -d --scale app=1",
            CommandLines(runner));
    }

    [Fact]
    public async Task TheNewColourIsPulledStartedAndProvedBeforeTheProxyIsTouched()
    {
        Settings(_ => { });
        var runner = Runner();

        await new BlueGreenDeployer(runner, Gateway(runner)).DeployAsync(Options());

        var commands = CommandLines(runner);
        var pull = commands.FindIndex(line => line.Contains(" pull"));
        var up = commands.FindIndex(line => line.Contains(" up -d"));
        var health = commands.FindIndex(line => line.Contains(" ps -a"));
        var reload = commands.FindIndex(line => line.Contains("caddy reload"));

        Assert.True(pull >= 0 && pull < up, "the image is pulled before the containers are started");
        Assert.True(up < health, "the containers are started before they are asked whether they are healthy");
        Assert.True(health < reload, "nothing is given traffic before it has been proved");
    }

    /// <summary>
    /// The one rule that makes every crash recoverable. Written first, a record of a
    /// switch that never happened would have the next proxy restart quietly finish a
    /// deploy that was abandoned.
    /// </summary>
    [Fact]
    public async Task AnUnhealthyColourNeverBecomesTheActiveOne()
    {
        Settings(_ => { });
        var runner = Runner("""{"Name":"shop-green-app-1","Service":"app","State":"exited"}""");

        var result = await new BlueGreenDeployer(runner, Gateway(runner)).DeployAsync(Options());

        Assert.False(result.Succeeded);
        Assert.Equal(DeployColors.Blue, ActiveColor());
        Assert.DoesNotContain(CommandLines(runner), line => line.Contains("caddy reload"));
    }

    [Fact]
    public async Task AProjectThatCannotRunTwiceIsRefusedBeforeAnythingIsStarted()
    {
        File.WriteAllText(_composePath, Eligible + "\n\nvolumes:\n  appdata:\n");
        Settings(_ => { });
        var runner = Runner();

        var result = await new BlueGreenDeployer(runner, Gateway(runner)).DeployAsync(Options());

        Assert.False(result.Succeeded);
        Assert.Contains("data would appear to vanish", result.Error);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task AnAppTheProxyHasNoRouteForIsNotCalledDeployed()
    {
        // Switching traffic to a colour nothing forwards to is a deploy that reports
        // green while the new version receives nothing.
        Settings(_ => { });
        var runner = Runner();
        var gateway = new ProxyGateway(runner, _proxyDirectory, "caddy:test");

        var result = await new BlueGreenDeployer(runner, gateway).DeployAsync(Options());

        Assert.False(result.Succeeded);
        Assert.Contains("no route for 'shop'", result.Error);
        Assert.Equal(DeployColors.Blue, ActiveColor());
    }

    [Fact]
    public async Task TheRetiringColourIsKeptRunningByDefault()
    {
        Settings(_ => { });
        var runner = Runner();

        await new BlueGreenDeployer(runner, Gateway(runner)).DeployAsync(Options());

        Assert.DoesNotContain(CommandLines(runner), line => line.Contains(" down"));
    }

    [Fact]
    public async Task TurningOffTheKeptColourStopsItAfterTheSwitch()
    {
        Settings(settings => settings.KeepPreviousColor = false);
        var runner = Runner();

        await new BlueGreenDeployer(runner, Gateway(runner)).DeployAsync(Options());

        var commands = CommandLines(runner);
        var reload = commands.FindIndex(line => line.Contains("caddy reload"));
        var down = commands.FindIndex(line => line.Contains("-p shop-blue") && line.Contains(" down"));
        Assert.True(down > reload, "the old colour is only stopped once traffic has moved off it");
    }

    /// <summary>
    /// A coloured deploy reads the whole settings file before it pulls anything and
    /// writes it back at the cutover, minutes later. Anything the operator changes in
    /// between — the copy count, autoscaling, the colour settings themselves — is
    /// inside that stale snapshot, and saving it whole puts every one of those
    /// settings back the way they were with nothing said.
    ///
    /// <para>The store's lock read as if it prevented this. It could not: it is an
    /// instance field on a class every caller news up, so no two callers ever share
    /// it.</para>
    /// </summary>
    [Fact]
    public async Task AnEditMadeDuringADeploySurvivesTheCutover()
    {
        Settings(settings => settings.Replicas = 1);
        var store = new DeploySettingsStore(_composePath);

        // The moment traffic moves is the moment the cutover writes the file, so
        // this is the edit that has the least chance of surviving.
        var runner = new FakeProcessRunner((_, arguments) =>
        {
            if (arguments.Contains("reload"))
            {
                var edited = store.Load();
                edited.Replicas = 4;
                store.Save(edited);
            }

            if (arguments.Contains("ps"))
            {
                return new ProcessResult(0, HealthyPs, string.Empty);
            }

            if (arguments.Contains("inspect"))
            {
                return new ProcessResult(0, "true\n", string.Empty);
            }

            return new ProcessResult(0, string.Empty, string.Empty);
        });

        var result = await new BlueGreenDeployer(runner, Gateway(runner)).DeployAsync(Options());

        Assert.True(result.Succeeded);
        Assert.Equal(DeployColors.Green, ActiveColor());
        Assert.Equal(4, store.Load().Replicas);
    }

    /// <summary>The same for the fast rollback, which writes the colour back too.</summary>
    [Fact]
    public async Task AnEditMadeDuringAFastRollbackSurvivesIt()
    {
        Settings(settings =>
        {
            settings.ActiveColor = DeployColors.Green;
            settings.Replicas = 1;
        });
        EnvFileStore.SetValue(
            ColorEnvironment.FileFor(_composePath, DeployColors.Blue), Deployer.TagVariable, "sha-previous");
        var store = new DeploySettingsStore(_composePath);

        var runner = new FakeProcessRunner((_, arguments) =>
        {
            if (arguments.Contains("reload"))
            {
                var edited = store.Load();
                edited.Replicas = 4;
                store.Save(edited);
            }

            if (arguments.Contains("ps"))
            {
                return new ProcessResult(
                    0, """{"Name":"shop-blue-app-1","Service":"app","State":"running"}""", string.Empty);
            }

            if (arguments.Contains("inspect"))
            {
                return new ProcessResult(0, "true\n", string.Empty);
            }

            return new ProcessResult(0, string.Empty, string.Empty);
        });

        Assert.True(await new BlueGreenDeployer(runner, Gateway(runner))
            .TrySwitchBackAsync(Options(), "sha-previous"));
        Assert.Equal(DeployColors.Blue, ActiveColor());
        Assert.Equal(4, store.Load().Replicas);
    }

    // ---- the version pinned in the shared .env -------------------------------

    private const string PreviousTag = "sha-previous";
    private const string PreviousImage = "ghcr.io/acme/shop";

    /// <summary>Pins the version that is actually running, as a finished deploy leaves it.</summary>
    private void RunningVersion()
    {
        var envFile = PinqOpsStatePaths.EnvFile(_composePath);
        EnvFileStore.SetValue(envFile, Deployer.TagVariable, PreviousTag);
        EnvFileStore.SetValue(envFile, Deployer.ImageVariable, PreviousImage);
    }

    private string? PinnedTag() =>
        EnvFileStore.GetValue(PinqOpsStatePaths.EnvFile(_composePath), Deployer.TagVariable);

    private string? PinnedImage() =>
        EnvFileStore.GetValue(PinqOpsStatePaths.EnvFile(_composePath), Deployer.ImageVariable);

    /// <summary>A runner that fails one compose subcommand and succeeds at everything else.</summary>
    private static FakeProcessRunner RunnerFailing(string subcommand, string ps = HealthyPs) =>
        new((_, arguments) =>
        {
            if (arguments.Contains(subcommand))
            {
                return new ProcessResult(1, string.Empty, $"docker: {subcommand} failed");
            }

            if (arguments.Contains("ps"))
            {
                return new ProcessResult(0, ps, string.Empty);
            }

            if (arguments.Contains("inspect"))
            {
                return new ProcessResult(0, "true\n", string.Empty);
            }

            return new ProcessResult(0, string.Empty, string.Empty);
        });

    /// <summary>
    /// The version is pinned into the project's shared <c>.env</c> before the pull,
    /// and every step from there to the switch can fail. Left pinned, that file
    /// describes a version that never served a request while the containers go on
    /// running the old one — so anything asked "what is deployed" answers wrongly,
    /// and the next ordinary <c>compose up</c> starts the version that failed.
    ///
    /// <para>The colour-blind <see cref="Deployer"/> puts it back; this path has to
    /// as well, on each of the four ways it can end early.</para>
    /// </summary>
    [Fact]
    public async Task AFailedPullLeavesTheRunningVersionPinned()
    {
        Settings(_ => { });
        RunningVersion();
        var runner = RunnerFailing("pull");

        var result = await new BlueGreenDeployer(runner, Gateway(runner)).DeployAsync(Options());

        Assert.False(result.Succeeded);
        Assert.Equal(PreviousTag, PinnedTag());
    }

    [Fact]
    public async Task AColourThatWillNotStartLeavesTheRunningVersionPinned()
    {
        Settings(_ => { });
        RunningVersion();
        var runner = RunnerFailing("up");

        var result = await new BlueGreenDeployer(runner, Gateway(runner)).DeployAsync(Options());

        Assert.False(result.Succeeded);
        Assert.Equal(PreviousTag, PinnedTag());
    }

    [Fact]
    public async Task AColourThatNeverBecomesHealthyLeavesTheRunningVersionPinned()
    {
        Settings(_ => { });
        RunningVersion();
        var runner = Runner("""{"Name":"shop-green-app-1","Service":"app","State":"exited"}""");

        var result = await new BlueGreenDeployer(runner, Gateway(runner)).DeployAsync(Options());

        Assert.False(result.Succeeded);
        Assert.Equal(PreviousTag, PinnedTag());
    }

    [Fact]
    public async Task AProxyThatWillNotSwitchLeavesTheRunningVersionPinned()
    {
        Settings(_ => { });
        RunningVersion();
        var runner = Runner();
        // No route for the app, so the switch cannot happen — the last step before
        // the deploy would have been called done.
        var gateway = new ProxyGateway(runner, _proxyDirectory, "caddy:test");

        var result = await new BlueGreenDeployer(runner, gateway).DeployAsync(Options());

        Assert.False(result.Succeeded);
        Assert.Equal(PreviousTag, PinnedTag());
    }

    [Fact]
    public async Task TheImageIsPutBackWithTheTag()
    {
        Settings(_ => { });
        RunningVersion();
        var runner = RunnerFailing("pull");

        var result = await new BlueGreenDeployer(runner, Gateway(runner))
            .DeployAsync(Options() with { Image = "ghcr.io/acme/shop-rebuilt" });

        Assert.False(result.Succeeded);
        Assert.Equal(PreviousImage, PinnedImage());
    }

    /// <summary>
    /// A first deploy pins into a file that held nothing, so putting it back means
    /// taking the key out again — leaving it would have the compose default silently
    /// replaced by a version that never ran.
    /// </summary>
    [Fact]
    public async Task AFailedFirstDeployLeavesNoVersionPinnedAtAll()
    {
        Settings(_ => { });
        var runner = RunnerFailing("pull");

        var result = await new BlueGreenDeployer(runner, Gateway(runner)).DeployAsync(Options());

        Assert.False(result.Succeeded);
        Assert.Null(PinnedTag());
    }

    /// <summary>The other half: a deploy that works pins the version it deployed.</summary>
    [Fact]
    public async Task ASuccessfulDeployPinsTheVersionItDeployed()
    {
        Settings(_ => { });
        RunningVersion();
        var runner = Runner();

        var result = await new BlueGreenDeployer(runner, Gateway(runner)).DeployAsync(Options());

        Assert.True(result.Succeeded);
        Assert.Equal("sha-abc123", PinnedTag());
    }

    // ---- instant rollback ----------------------------------------------------

    [Fact]
    public async Task RollingBackToTheKeptColoursVersionIsAProxyReload()
    {
        Settings(settings => settings.ActiveColor = DeployColors.Green);
        EnvFileStore.SetValue(
            ColorEnvironment.FileFor(_composePath, DeployColors.Blue), Deployer.TagVariable, "sha-previous");
        var runner = Runner("""{"Name":"shop-blue-app-1","Service":"app","State":"running"}""");

        var switched = await new BlueGreenDeployer(runner, Gateway(runner))
            .TrySwitchBackAsync(Options(), "sha-previous");

        Assert.True(switched);
        Assert.Equal(DeployColors.Blue, ActiveColor());
        // No pull and no up: the containers never stopped.
        Assert.DoesNotContain(CommandLines(runner), line => line.Contains(" pull") || line.Contains(" up -d"));
    }

    /// <summary>
    /// A switch the proxy refuses must leave no trace of itself in the stored
    /// configuration.
    ///
    /// <para>The record is written before Caddy is asked to accept it, and the
    /// Caddyfile — but not the record — is put back when the reload is refused. Every
    /// later regeneration reads the record: adding a domain to any app, a preview
    /// opening from the runner, an apply from the Proxy page. The first of those
    /// would install the switch this deploy abandoned and reported as failed, moving
    /// production onto it with no deploy, no log line and no history entry.</para>
    /// </summary>
    [Fact]
    public async Task AProxyThatRefusesTheReloadLeavesNoRecordOfTheSwitch()
    {
        Settings(settings => settings.ActiveColor = DeployColors.Green);
        EnvFileStore.SetValue(
            ColorEnvironment.FileFor(_composePath, DeployColors.Blue), Deployer.TagVariable, "sha-previous");
        var runner = RunnerFailing("exec", """{"Name":"shop-blue-app-1","Service":"app","State":"running"}""");
        var gateway = Gateway(runner);

        Assert.False(await new BlueGreenDeployer(runner, gateway).TrySwitchBackAsync(Options(), "sha-previous"));

        Assert.Equal(DeployColors.Green, ActiveColor());
        Assert.Null(gateway.Store.Load().Ports[0].Upstream?.Balancing);
    }

    /// <summary>
    /// The rollback moves the traffic, and the shared <c>.env</c> has to move with
    /// it. That file is not a record of the deploy — it is the input to every ordinary
    /// compose action on the project — so leaving it naming the release that was just
    /// rolled back from means the next variable change or autoscale tick recreates the
    /// live containers on it, and the dashboard calls it "live now" the whole time.
    /// </summary>
    [Fact]
    public async Task RollingBackPinsTheVersionItRolledBackTo()
    {
        Settings(settings => settings.ActiveColor = DeployColors.Green);
        var envFile = PinqOpsStatePaths.EnvFile(_composePath);
        EnvFileStore.SetValue(envFile, Deployer.TagVariable, "sha-bad");
        var keptEnv = ColorEnvironment.FileFor(_composePath, DeployColors.Blue);
        EnvFileStore.SetValue(keptEnv, Deployer.TagVariable, "sha-previous");
        EnvFileStore.SetValue(keptEnv, Deployer.ImageVariable, "ghcr.io/acme/shop");
        var runner = Runner("""{"Name":"shop-blue-app-1","Service":"app","State":"running"}""");

        Assert.True(await new BlueGreenDeployer(runner, Gateway(runner))
            .TrySwitchBackAsync(Options(), "sha-previous"));

        Assert.Equal("sha-previous", PinnedTag());
        Assert.Equal("ghcr.io/acme/shop", PinnedImage());
    }

    /// <summary>
    /// And a rollback that decides it cannot take the fast path leaves the pin alone:
    /// the ordinary redeploy it falls through to is what pins the version, and moving
    /// it here would name a release nothing is running.
    /// </summary>
    [Fact]
    public async Task ARollbackThatFallsThroughLeavesTheVersionPinnedWhereItWas()
    {
        Settings(settings => settings.ActiveColor = DeployColors.Green);
        EnvFileStore.SetValue(PinqOpsStatePaths.EnvFile(_composePath), Deployer.TagVariable, "sha-bad");
        EnvFileStore.SetValue(
            ColorEnvironment.FileFor(_composePath, DeployColors.Blue), Deployer.TagVariable, "sha-previous");
        var runner = Runner("""{"Name":"shop-blue-app-1","Service":"app","State":"exited"}""");

        Assert.False(await new BlueGreenDeployer(runner, Gateway(runner))
            .TrySwitchBackAsync(Options(), "sha-previous"));

        Assert.Equal("sha-bad", PinnedTag());
    }

    [Fact]
    public async Task RollingBackToAnyOtherVersionFallsThroughToTheOrdinaryRedeploy()
    {
        Settings(settings => settings.ActiveColor = DeployColors.Green);
        EnvFileStore.SetValue(
            ColorEnvironment.FileFor(_composePath, DeployColors.Blue), Deployer.TagVariable, "sha-previous");
        var runner = Runner();

        Assert.False(await new BlueGreenDeployer(runner, Gateway(runner))
            .TrySwitchBackAsync(Options(), "sha-much-older"));
        Assert.Equal(DeployColors.Green, ActiveColor());
    }

    /// <summary>
    /// Running, not merely recorded. A container that died overnight would have this
    /// switch traffic to nothing, and the ordinary redeploy is right there.
    /// </summary>
    [Fact]
    public async Task AKeptColourThatIsNoLongerRunningIsNotSwitchedTo()
    {
        Settings(settings => settings.ActiveColor = DeployColors.Green);
        EnvFileStore.SetValue(
            ColorEnvironment.FileFor(_composePath, DeployColors.Blue), Deployer.TagVariable, "sha-previous");
        var runner = Runner("""{"Name":"shop-blue-app-1","Service":"app","State":"exited"}""");

        Assert.False(await new BlueGreenDeployer(runner, Gateway(runner))
            .TrySwitchBackAsync(Options(), "sha-previous"));
        Assert.Equal(DeployColors.Green, ActiveColor());
    }

    [Fact]
    public async Task AProjectThatDoesNotKeepItsPreviousColourHasNoFastRollback()
    {
        Settings(settings =>
        {
            settings.ActiveColor = DeployColors.Green;
            settings.KeepPreviousColor = false;
        });
        EnvFileStore.SetValue(
            ColorEnvironment.FileFor(_composePath, DeployColors.Blue), Deployer.TagVariable, "sha-previous");
        var runner = Runner();

        Assert.False(await new BlueGreenDeployer(runner, Gateway(runner))
            .TrySwitchBackAsync(Options(), "sha-previous"));
    }

    // ---- gathering what a coloured deploy needs ------------------------------

    private DeploySettings Stored(Action<DeploySettings>? configure = null)
    {
        var store = new DeploySettingsStore(_composePath);
        var settings = store.Load();
        settings.BlueGreen = true;
        settings.ProxyTarget = "shop";
        configure?.Invoke(settings);
        store.Save(settings);
        return settings;
    }

    [Fact]
    public void APlanGathersTheProjectFromTheFileAndTheAliasFromTheEnvironment()
    {
        Assert.True(BlueGreenPlan.TryCreate(_composePath, Stored(), out var options, out var problem));

        Assert.Null(problem);
        Assert.Equal("shop", options!.Project);
        Assert.Equal("shop", options.Alias);
        Assert.Equal("shop", options.Target);
    }

    [Fact]
    public void AProjectThatIsNotColouredHasNoPlanAndNothingToReport()
    {
        // Not a failure — the ordinary deploy is correct and there is nothing to say.
        Assert.False(BlueGreenPlan.TryCreate(_composePath, new DeploySettings(), out var options, out var problem));
        Assert.Null(options);
        Assert.Null(problem);
    }

    [Fact]
    public void AProjectWithNoAliasCannotBeSwitchedAndSaysWhy()
    {
        EnvFileStore.RemoveValue(PinqOpsStatePaths.EnvFile(_composePath), Deployer.AliasVariable);

        Assert.False(BlueGreenPlan.TryCreate(_composePath, Stored(), out _, out var problem));
        Assert.Contains("Hand its host port to the proxy first", problem);
    }

    [Fact]
    public void AProjectWithNoRecordedRouteCannotBeSwitchedAndSaysWhy()
    {
        // Derived instead of recorded, this would be right until the day it was not
        // — and the symptom is a cutover reporting success while switching a route
        // that belongs to nothing.
        Assert.False(
            BlueGreenPlan.TryCreate(_composePath, Stored(settings => settings.ProxyTarget = ""), out _, out var problem));
        Assert.Contains("does not record which proxy route", problem);
    }

    [Fact]
    public void AComposeFileWithNoProjectNameCannotBeToldApartByColour()
    {
        File.WriteAllText(_composePath, Eligible.Replace("name: \"shop\"\n", "", StringComparison.Ordinal));

        Assert.False(BlueGreenPlan.TryCreate(_composePath, Stored(), out _, out var problem));
        Assert.Contains("declares no project name", problem);
    }

    // ---- reconciliation ------------------------------------------------------

    [Fact]
    public async Task TheReconcilerPutsTheRoutesBackOnTheRecordedColour()
    {
        Settings(settings => settings.ActiveColor = DeployColors.Green);
        var runner = Runner();
        var gateway = Gateway(runner);
        gateway.Store.Update<object?>(config =>
        {
            config.Ports[0].Upstream = new UpstreamOptions
            {
                Balancing = new LoadBalancing { Alias = "shop-blue" },
            };
            return null;
        });

        var corrected = await new ColorReconciler(gateway)
            .ReconcileAsync([new ColoredApp("shop", _composePath, "shop")]);

        Assert.Equal(1, corrected);
        Assert.Equal("shop-green", gateway.Store.Load().Ports[0].Upstream!.Balancing!.Alias);
    }

    [Fact]
    public async Task TheReconcilerDoesNothingWhenTheRoutesAlreadyAgree()
    {
        Settings(settings => settings.ActiveColor = DeployColors.Green);
        var runner = Runner();
        var gateway = Gateway(runner);
        gateway.Store.Update<object?>(config =>
        {
            config.Ports[0].Upstream = new UpstreamOptions
            {
                Balancing = new LoadBalancing { Alias = "shop-green" },
            };
            return null;
        });

        Assert.Equal(0, await new ColorReconciler(gateway)
            .ReconcileAsync([new ColoredApp("shop", _composePath, "shop")]));
        Assert.DoesNotContain(CommandLines(runner), line => line.Contains("caddy reload"));
    }

    /// <summary>
    /// It never decides that the other colour "looks more alive" and moves to it:
    /// that would silently finish a cutover a failed deploy deliberately abandoned,
    /// and a version that never passed its health check would end up serving.
    /// </summary>
    [Fact]
    public async Task TheReconcilerNeverChangesWhichColourIsActive()
    {
        Settings(settings => settings.ActiveColor = DeployColors.Blue);
        var runner = Runner();

        await new ColorReconciler(Gateway(runner)).ReconcileAsync([new ColoredApp("shop", _composePath, "shop")]);

        Assert.Equal(DeployColors.Blue, ActiveColor());
    }

    [Fact]
    public async Task AProjectThatIsNotDeployedInColoursIsLeftAlone()
    {
        var runner = Runner();
        var gateway = Gateway(runner);

        Assert.Equal(0, await new ColorReconciler(gateway)
            .ReconcileAsync([new ColoredApp("shop", _composePath, "shop")]));
        Assert.Null(gateway.Store.Load().Ports[0].Upstream?.Balancing);
    }
}
