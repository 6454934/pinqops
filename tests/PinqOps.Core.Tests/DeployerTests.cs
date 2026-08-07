using PinqOps.Tests.Fakes;
using Xunit;

namespace PinqOps.Tests;

public class DeployerTests : IDisposable
{
    private readonly string _projectDirectory;
    private readonly string _composePath;

    private const string HealthyPs = """{"Name":"app","State":"running","Health":"healthy"}""";

    public DeployerTests()
    {
        _projectDirectory = Directory.CreateTempSubdirectory("pinqops-deployer-tests").FullName;
        _composePath = Path.Combine(_projectDirectory, "docker-compose.yml");
        File.WriteAllText(_composePath, "services: {}\n");
    }

    public void Dispose() => Directory.Delete(_projectDirectory, recursive: true);

    private static FakeProcessRunner ScriptedRunner(string psOutput = HealthyPs, int pullExit = 0, int upExit = 0) =>
        new((_, arguments) =>
        {
            if (arguments.Contains("ps"))
            {
                return new ProcessResult(0, psOutput, string.Empty);
            }

            if (arguments.Contains("pull"))
            {
                return new ProcessResult(pullExit, string.Empty, pullExit == 0 ? string.Empty : "boom");
            }

            if (arguments.Contains("up"))
            {
                return new ProcessResult(upExit, string.Empty, upExit == 0 ? string.Empty : "boom");
            }

            return new ProcessResult(0, string.Empty, string.Empty);
        });

    /// <summary>
    /// A project deployed as two colours must never go through this deployer.
    ///
    /// <para>It runs <c>docker compose up</c> with no <c>-p</c> and no colour
    /// environment file, so against a coloured project it starts a <em>third</em>
    /// compose project — a second copy of the app that nothing routes to, holding
    /// the same external volumes — and then reports the deploy as done. The
    /// dashboard's own compose editor documents exactly this danger; the rollback
    /// paths in both the CLI and the dashboard reached here anyway, whenever the
    /// fast colour switch declined.</para>
    ///
    /// <para>Refused here rather than at each caller, because there is no caller
    /// for which the colour-blind path is the right one.</para>
    /// </summary>
    [Fact]
    public async Task AColouredProjectIsRefusedRatherThanStartedAsAThirdOne()
    {
        new PinqOps.Deploy.DeploySettingsStore(_composePath).Save(
            new PinqOps.Deploy.DeploySettings { BlueGreen = true });

        var runner = ScriptedRunner();

        var succeeded = await new Deployer(runner, _ => { }).DeployAsync(Options(tag: "sha-abc"));

        Assert.False(succeeded);
        Assert.DoesNotContain(runner.Invocations, invocation => invocation.Arguments.Contains("up"));
    }

    [Fact]
    public async Task AnOrdinaryProjectIsStillDeployed()
    {
        var runner = ScriptedRunner();

        Assert.True(await new Deployer(runner, _ => { }).DeployAsync(Options(tag: "sha-abc")));
        Assert.Contains(runner.Invocations, invocation => invocation.Arguments.Contains("up"));
    }

    private DeployOptions Options(
        string? tag = null,
        bool pruneImages = true,
        TimeSpan? healthCheckTimeout = null,
        string trigger = DeployRecordValues.TriggerManual,
        string? expectedImage = null,
        TimeSpan? timeout = null) =>
        DeployOptions.Create(
            _composePath,
            pruneImages: pruneImages,
            tag: tag,
            timeout: timeout,
            healthCheckTimeout: healthCheckTimeout ?? TimeSpan.FromSeconds(5),
            trigger: trigger,
            expectedImage: expectedImage);

    [Fact]
    public async Task DeployAsync_HappyPath_RunsPullUpHealthRetention_InOrder()
    {
        var runner = ScriptedRunner();
        var deployer = new Deployer(runner);

        var result = await deployer.DeployAsync(Options());

        Assert.True(result);
        var commands = runner.Invocations.Select(invocation => invocation.CommandLine).ToList();
        Assert.Equal($"docker compose -f {_composePath} pull", commands[0]);
        Assert.Equal($"docker compose -f {_composePath} up -d", commands[1]);
        Assert.Equal($"docker compose -f {_composePath} ps -a --format json", commands[2]);
        Assert.Equal($"docker compose -f {_composePath} config --images", commands[3]);
        Assert.Equal("docker image prune -f", commands[^1]);
    }

    [Fact]
    public async Task DeployAsync_RunsComposeCommands_FromTheComposeFilesDirectory()
    {
        var runner = ScriptedRunner();
        var deployer = new Deployer(runner);

        var result = await deployer.DeployAsync(Options());

        Assert.True(result);
        // Every `docker compose` invocation must run from the project directory so
        // the project's .env is interpolated — the chosen host/container ports
        // live there, and a stale CWD silently drops them to the YAML defaults.
        var composeCalls = runner.Invocations
            .Where(invocation => invocation.Arguments.Contains("compose"))
            .ToList();
        Assert.NotEmpty(composeCalls);
        Assert.All(composeCalls, invocation => Assert.Equal(_projectDirectory, invocation.WorkingDirectory));
    }

    [Fact]
    public async Task DeployAsync_PullFails_SkipsUp_ReturnsFalse()
    {
        var runner = ScriptedRunner(pullExit: 1);
        var deployer = new Deployer(runner);

        var result = await deployer.DeployAsync(Options());

        Assert.False(result);
        Assert.Single(runner.Invocations);
        Assert.Contains("pull", runner.Invocations[0].Arguments);
    }

    [Fact]
    public async Task DeployAsync_UpFails_SkipsHealthAndPrune_ReturnsFalse()
    {
        var runner = ScriptedRunner(upExit: 1);
        var deployer = new Deployer(runner);

        var result = await deployer.DeployAsync(Options());

        Assert.False(result);
        Assert.Equal(2, runner.Invocations.Count); // pull + up only
        Assert.DoesNotContain(runner.Invocations, invocation => invocation.Arguments.Contains("prune"));
    }

    [Fact]
    public async Task DeployAsync_UpFails_RecordsDockersOwnReason()
    {
        // A port collision only ever announces itself through docker's stderr;
        // a bare "compose up failed" in the notification hides why.
        const string PortConflict = "Error response from daemon: driver failed programming external "
            + "connectivity on endpoint app-1: Bind for 0.0.0.0:8080 failed: port is already allocated";
        var runner = new FakeProcessRunner((_, arguments) =>
        {
            if (arguments.Contains("up"))
            {
                return new ProcessResult(1, string.Empty, PortConflict);
            }

            return arguments.Contains("ps")
                ? new ProcessResult(0, HealthyPs, string.Empty)
                : new ProcessResult(0, string.Empty, string.Empty);
        });
        var history = new DeployHistoryStore(_composePath);
        var deployer = new Deployer(runner, history: history);

        var result = await deployer.DeployAsync(Options(tag: "sha-abc123"));

        Assert.False(result);
        var record = Assert.Single(history.Load());
        Assert.Equal(DeployRecordValues.ResultFailed, record.Result);
        Assert.Contains("port is already allocated", record.Error);
    }

    [Fact]
    public async Task DeployAsync_PullFails_RecordsDockersOwnReason()
    {
        var runner = new FakeProcessRunner((_, arguments) => arguments.Contains("pull")
            ? new ProcessResult(1, string.Empty, "denied: permission_denied: The token provided is invalid")
            : new ProcessResult(0, HealthyPs, string.Empty));
        var history = new DeployHistoryStore(_composePath);
        var deployer = new Deployer(runner, history: history);

        var result = await deployer.DeployAsync(Options(tag: "sha-abc123"));

        Assert.False(result);
        Assert.Contains("permission_denied", Assert.Single(history.Load()).Error);
    }

    [Fact]
    public async Task DeployAsync_StepFailsWithNoStderr_StillRecordsTheExitCode()
    {
        var runner = ScriptedRunner(upExit: 1);
        var history = new DeployHistoryStore(_composePath);
        var deployer = new Deployer(runner, history: history);

        await deployer.DeployAsync(Options());

        Assert.Contains("compose up failed", Assert.Single(history.Load()).Error);
    }

    // compose ps reporting a published mapping, plus an image that exposes a
    // DIFFERENT port — the "deployed green but the site is dead" shape.
    private static FakeProcessRunner RunnerWithPorts(string exposedPortsJson, int targetPort)
    {
        var ps = $$"""{"Name":"app-1","State":"running","Health":"","Publishers":[{"URL":"0.0.0.0","TargetPort":{{targetPort}},"PublishedPort":8080,"Protocol":"tcp"}]}""";
        return new FakeProcessRunner((_, arguments) =>
        {
            if (arguments.Contains("ps"))
            {
                return new ProcessResult(0, ps, string.Empty);
            }

            if (arguments.Contains("config"))
            {
                return new ProcessResult(0, "ghcr.io/acme/app:latest\n", string.Empty);
            }

            if (arguments.Contains("inspect"))
            {
                return new ProcessResult(0, exposedPortsJson, string.Empty);
            }

            return new ProcessResult(0, string.Empty, string.Empty);
        });
    }

    [Fact]
    public async Task DeployAsync_PublishesAPortTheImageDoesNotExpose_WarnsButSucceeds()
    {
        var log = new List<string>();
        var deployer = new Deployer(RunnerWithPorts("""{"8083/tcp":{}}""", targetPort: 80), log.Add);

        var result = await deployer.DeployAsync(Options());

        // Advisory only: EXPOSE is documentation, so this must never fail a deploy.
        Assert.True(result);
        var warning = Assert.Single(log, line => line.StartsWith("warning: traffic is aimed at container port"));
        Assert.Contains("8083", warning);
        Assert.Contains(Deployer.ContainerPortVariable, warning);
    }

    [Fact]
    public async Task DeployAsync_PublishedPortIsExposed_DoesNotWarn()
    {
        var log = new List<string>();
        var deployer = new Deployer(RunnerWithPorts("""{"8083/tcp":{}}""", targetPort: 8083), log.Add);

        Assert.True(await deployer.DeployAsync(Options()));
        Assert.DoesNotContain(log, line => line.StartsWith("warning: traffic is aimed at container port"));
    }

    [Fact]
    public async Task DeployAsync_ImageExposesNothing_DoesNotWarn()
    {
        // No EXPOSE at all is no basis for an opinion.
        var log = new List<string>();
        var deployer = new Deployer(RunnerWithPorts("null", targetPort: 80), log.Add);

        Assert.True(await deployer.DeployAsync(Options()));
        Assert.DoesNotContain(log, line => line.StartsWith("warning: traffic is aimed at container port"));
    }

    // An app whose host port the proxy publishes has no Publishers entry at all.
    private FakeProcessRunner RunnerWithNoPublishers(string exposedPortsJson, string service = "app") =>
        new((_, arguments) =>
        {
            if (arguments.Contains("ps"))
            {
                return new ProcessResult(
                    0,
                    $$"""{"Name":"acme-{{service}}-1","Service":"{{service}}","State":"running","Health":"","Publishers":[]}""",
                    string.Empty);
            }

            if (arguments.Contains("config"))
            {
                return new ProcessResult(0, "ghcr.io/acme/app:latest\n", string.Empty);
            }

            if (arguments.Contains("inspect"))
            {
                return new ProcessResult(0, exposedPortsJson, string.Empty);
            }

            return new ProcessResult(0, string.Empty, string.Empty);
        });

    [Fact]
    public async Task DeployAsync_ProxyPublishesThePort_StillWarnsFromTheDeclaredContainerPort()
    {
        // The whole point: reading the answer off the published ports made this
        // diagnostic go silent exactly when it matters most, because an enrolled
        // app publishes nothing of its own. A proxy route aimed at a port nothing
        // listens on reads as a proxy fault, not an app fault.
        EnvFileStore.SetValue(Path.Combine(_projectDirectory, ".env"), Deployer.ContainerPortVariable, "80");
        var log = new List<string>();
        var deployer = new Deployer(RunnerWithNoPublishers("""{"8083/tcp":{}}"""), log.Add);

        Assert.True(await deployer.DeployAsync(Options()));
        var warning = Assert.Single(log, line => line.StartsWith("warning: traffic is aimed at container port"));
        Assert.Contains("80", warning);
        Assert.Contains("8083", warning);
    }

    [Fact]
    public async Task DeployAsync_ProxyPublishesAPortTheImageExposes_DoesNotWarn()
    {
        EnvFileStore.SetValue(Path.Combine(_projectDirectory, ".env"), Deployer.ContainerPortVariable, "8083");
        var log = new List<string>();
        var deployer = new Deployer(RunnerWithNoPublishers("""{"8083/tcp":{}}"""), log.Add);

        Assert.True(await deployer.DeployAsync(Options()));
        Assert.DoesNotContain(log, line => line.StartsWith("warning: traffic is aimed at container port"));
    }

    [Fact]
    public async Task DeployAsync_ServiceThatPublishesNothingAndIsNotTheApp_DoesNotWarn()
    {
        // PINQOPS_CONTAINER_PORT describes the app service and nothing else. A
        // database that deliberately publishes nothing would otherwise be judged
        // against the app's port and warned about on every single deploy.
        EnvFileStore.SetValue(Path.Combine(_projectDirectory, ".env"), Deployer.ContainerPortVariable, "80");
        var log = new List<string>();
        var deployer = new Deployer(RunnerWithNoPublishers("""{"5432/tcp":{}}""", service: "db"), log.Add);

        Assert.True(await deployer.DeployAsync(Options()));
        Assert.DoesNotContain(log, line => line.StartsWith("warning: traffic is aimed at container port"));
    }

    [Fact]
    public async Task DeployAsync_NoDeclaredContainerPortAndNothingPublished_DoesNotWarn()
    {
        // Nothing published and nothing declared is no basis for an opinion —
        // the same stance as an image that exposes nothing.
        var log = new List<string>();
        var deployer = new Deployer(RunnerWithNoPublishers("""{"8083/tcp":{}}"""), log.Add);

        Assert.True(await deployer.DeployAsync(Options()));
        Assert.DoesNotContain(log, line => line.StartsWith("warning: traffic is aimed at container port"));
    }

    // --- readiness probe -------------------------------------------------------

    private const string NamedHealthyPs =
        """{"Name":"acme-app-1","Service":"app","State":"running","Health":"healthy"}""";

    private static FakeProcessRunner RunnerWithNetworks() =>
        new((_, arguments) =>
        {
            if (arguments.Contains("ps"))
            {
                return new ProcessResult(0, NamedHealthyPs, string.Empty);
            }

            // `docker inspect <container>`, not `docker image inspect <ref>`.
            if (arguments.Contains("inspect") && !arguments.Contains("image"))
            {
                return new ProcessResult(0, """{"pinqops-app-acme":{"IPAddress":"172.20.0.3"}}""", string.Empty);
            }

            return new ProcessResult(0, string.Empty, string.Empty);
        });

    private void EnableReadiness(int timeoutSeconds = 1) =>
        new PinqOps.Deploy.DeploySettingsStore(_composePath).Save(new PinqOps.Deploy.DeploySettings
        {
            Readiness = new PinqOps.Deploy.ReadinessSettings
            {
                Enabled = true,
                IntervalSeconds = 1,
                TimeoutSeconds = timeoutSeconds,
                RequestTimeoutSeconds = 1,
                ConsecutiveSuccesses = 1,
            },
        });

    private static PinqOps.Deploy.ReadinessProbe Probe(
        FakeProcessRunner runner, System.Net.HttpStatusCode status, Action<string>? log = null) =>
        new(runner, new ScriptedHttpMessageHandler((_, _) => new HttpResponseMessage(status)), log);

    [Fact]
    public async Task DeployAsync_ProbeIsOffByDefault_AnUnansweringAppStillDeploys()
    {
        // The gate must not switch on underneath an app that has been deploying
        // fine for a year and happens to have no route at "/".
        var runner = RunnerWithNetworks();

        Assert.True(await new Deployer(
            runner, readinessProbe: Probe(runner, System.Net.HttpStatusCode.NotFound)).DeployAsync(Options()));
    }

    [Fact]
    public async Task DeployAsync_ProbeAnswers_TheDeploySucceeds()
    {
        EnableReadiness();
        var runner = RunnerWithNetworks();

        Assert.True(await new Deployer(
            runner, readinessProbe: Probe(runner, System.Net.HttpStatusCode.OK)).DeployAsync(Options()));
    }

    [Fact]
    public async Task DeployAsync_ProbeNeverAnswers_TheDeployFails()
    {
        EnableReadiness();
        var runner = RunnerWithNetworks();
        var log = new List<string>();

        var result = await new Deployer(
                runner, log.Add, readinessProbe: Probe(runner, System.Net.HttpStatusCode.InternalServerError))
            .DeployAsync(Options());

        Assert.False(result);
        Assert.Contains(log, line => line.Contains("readiness probe") && line.Contains("500"));
    }

    [Fact]
    public async Task DeployAsync_ProbeFails_TheHistoryRecordsItAsAFailedHealthCheck()
    {
        EnableReadiness();
        var runner = RunnerWithNetworks();
        var history = new DeployHistoryStore(_composePath);

        await new Deployer(runner, history: history, readinessProbe: Probe(runner, System.Net.HttpStatusCode.BadGateway))
            .DeployAsync(Options());

        var record = Assert.Single(history.Load());
        Assert.Equal(DeployRecordValues.ResultFailed, record.Result);
        Assert.Equal(DeployRecordValues.HealthFailed, record.HealthCheck);
        Assert.Contains("readiness probe", record.Error);
    }

    [Fact]
    public async Task DeployAsync_ProbeThatCannotBeRun_FailsTheDeployRatherThanPassing()
    {
        // Fail-closed. A gate that quietly stops gating reports the same green as
        // one that passed, which is worse than having no gate at all.
        EnableReadiness();
        var runner = new FakeProcessRunner((_, arguments) => arguments.Contains("ps")
            ? new ProcessResult(0, NamedHealthyPs, string.Empty)
            : new ProcessResult(arguments.Contains("inspect") && !arguments.Contains("image") ? 1 : 0, string.Empty, "No such object"));

        Assert.False(await new Deployer(
            runner, readinessProbe: Probe(runner, System.Net.HttpStatusCode.OK)).DeployAsync(Options()));
    }

    [Fact]
    public async Task DeployAsync_ProbeRunsAfterTheComposeHealthCheck_NotInsteadOfIt()
    {
        // Asking an application for a page while its container is still starting
        // answers nothing; the compose check is what knows when that is over.
        EnableReadiness();
        var runner = new FakeProcessRunner((_, arguments) => arguments.Contains("ps")
            ? new ProcessResult(0, """{"Name":"acme-app-1","Service":"app","State":"exited"}""", string.Empty)
            : new ProcessResult(0, string.Empty, string.Empty));
        var log = new List<string>();

        Assert.False(await new Deployer(
                runner, log.Add, readinessProbe: Probe(runner, System.Net.HttpStatusCode.OK))
            .DeployAsync(Options()));
        Assert.Contains(log, line => line.StartsWith("health check failed"));
        Assert.DoesNotContain(log, line => line.Contains("readiness probe"));
    }

    [Fact]
    public async Task DeployAsync_ProbeBudgetLargerThanTheDeploysIsCappedRatherThanKillingTheDeploy()
    {
        // A probe budget the deploy timeout cannot cover is never spent: the deploy
        // is killed mid-probe and reports "docker was killed mid-flight, so the
        // compose project may be partly updated" about a deploy where nothing is
        // wrong except an application that took too long.
        EnableReadiness(timeoutSeconds: 600);
        var runner = RunnerWithNetworks();
        var log = new List<string>();

        var result = await new Deployer(
                runner, log.Add, readinessProbe: Probe(runner, System.Net.HttpStatusCode.BadGateway))
            .DeployAsync(Options(healthCheckTimeout: TimeSpan.FromSeconds(3), timeout: TimeSpan.FromSeconds(8)));

        Assert.False(result);
        Assert.Contains(log, line => line.Contains("does not fit in what is left of the deploy timeout"));
        Assert.DoesNotContain(log, line => line.Contains("killed mid-flight"));
    }

    // --- replicas --------------------------------------------------------------

    /// <summary>The shape a project has once the proxy publishes its port.</summary>
    private const string EnrolledCompose = """
        services:
          app:
            image: "ghcr.io/acme/app:latest"
            expose:
              # pinqops: the proxy publishes this port — - "${PINQOPS_HOST_PORT:-8080}:${PINQOPS_CONTAINER_PORT:-80}"
              # expose: the container port, so compose still records what the app listens on.
        """;

    private void SetReplicas(int replicas) =>
        new PinqOps.Deploy.DeploySettingsStore(_composePath).Save(
            new PinqOps.Deploy.DeploySettings { Replicas = replicas });

    private static IReadOnlyList<string>? UpArguments(FakeProcessRunner runner) =>
        runner.Invocations.FirstOrDefault(invocation => invocation.Arguments.Contains("up"))?.Arguments;

    [Fact]
    public async Task DeployAsync_OneCopy_RunsTheSameUpItAlwaysDid()
    {
        // Not "--scale app=1": that is the argument list every existing deploy
        // produces, and compose converges to one container without being told.
        var runner = ScriptedRunner();

        Assert.True(await new Deployer(runner).DeployAsync(Options()));
        Assert.Equal($"compose -f {_composePath} up -d", string.Join(' ', UpArguments(runner)!));
    }

    [Fact]
    public async Task DeployAsync_MoreThanOneCopy_ScalesTheAppService()
    {
        File.WriteAllText(_composePath, EnrolledCompose);
        SetReplicas(3);
        var runner = ScriptedRunner();

        Assert.True(await new Deployer(runner).DeployAsync(Options()));
        Assert.Equal(
            $"compose -f {_composePath} up -d --scale app=3", string.Join(' ', UpArguments(runner)!));
    }

    [Fact]
    public async Task DeployAsync_MoreThanOneCopyWhileTheAppPublishesItsOwnPort_FailsBeforeUp()
    {
        // Docker's own answer here is "Bind for 0.0.0.0:8080 failed: port is already
        // allocated", which reads as somebody else taking the port rather than the
        // app taking it from itself — and sends the operator looking for the wrong
        // thing entirely.
        File.WriteAllText(_composePath, "services:\n  app:\n    ports:\n      - \"8080:80\"\n");
        SetReplicas(3);
        var runner = ScriptedRunner();
        var history = new DeployHistoryStore(_composePath);

        Assert.False(await new Deployer(runner, history: history).DeployAsync(Options()));
        Assert.Null(UpArguments(runner));
        Assert.Contains("publishes its own host port", Assert.Single(history.Load()).Error);
    }

    [Fact]
    public async Task DeployAsync_AReplicaCountNoServerCouldRun_IsClamped()
    {
        File.WriteAllText(_composePath, EnrolledCompose);
        SetReplicas(500);
        var runner = ScriptedRunner();

        await new Deployer(runner).DeployAsync(Options());

        Assert.Equal(
            $"compose -f {_composePath} up -d --scale app=20", string.Join(' ', UpArguments(runner)!));
    }

    [Fact]
    public async Task DeployAsync_ANonsenseReplicaCount_IsOneCopy()
    {
        File.WriteAllText(_composePath, EnrolledCompose);
        SetReplicas(0);
        var runner = ScriptedRunner();

        await new Deployer(runner).DeployAsync(Options());

        Assert.Equal($"compose -f {_composePath} up -d", string.Join(' ', UpArguments(runner)!));
    }

    [Fact]
    public async Task DeployAsync_ComposeProjectBelongsToAnotherApp_FailsBeforeTouchingAnything()
    {
        // Exactly the real failure: a second repository pointed at the first
        // one's compose file. Without this guard its image and tag get pinned
        // over the other application's and the wrong image runs.
        File.WriteAllText(_composePath, "name: \"ikv-board\"\nservices:\n  app: {}\n");
        var envFile = Path.Combine(_projectDirectory, ".env");
        EnvFileStore.SetValue(envFile, Deployer.ImageVariable, "ghcr.io/acme/ikv-board");
        EnvFileStore.SetValue(envFile, Deployer.TagVariable, "sha-original");

        var runner = ScriptedRunner();
        var history = new DeployHistoryStore(_composePath);
        var deployer = new Deployer(runner, history: history);

        var result = await deployer.DeployAsync(
            Options(tag: "sha-intruder", expectedImage: "ghcr.io/acme/peramice"));

        Assert.False(result);
        Assert.Empty(runner.Invocations);
        // The other application's pins must be untouched.
        Assert.Equal("ghcr.io/acme/ikv-board", EnvFileStore.GetValue(envFile, Deployer.ImageVariable));
        Assert.Equal("sha-original", EnvFileStore.GetValue(envFile, Deployer.TagVariable));

        var error = Assert.Single(history.Load()).Error;
        Assert.Contains("ikv-board", error);
        Assert.Contains("peramice", error);
        Assert.Contains("APP_COMPOSE_PATH", error);
    }

    [Fact]
    public async Task DeployAsync_ComposeProjectIsOurs_Proceeds()
    {
        File.WriteAllText(_composePath, "name: \"peramice\"\nservices:\n  app: {}\n");
        var runner = RunnerWithConfigImages("ghcr.io/acme/peramice:sha-abc123\n");

        var result = await new Deployer(runner).DeployAsync(
            Options(tag: "sha-abc123", expectedImage: "ghcr.io/acme/peramice"));

        Assert.True(result);
    }

    [Fact]
    public async Task DeployAsync_ComposeDeclaresNoProjectName_SkipsTheOwnerCheck()
    {
        // A hand-written compose file gives no ownership signal; do not invent one.
        File.WriteAllText(_composePath, "services:\n  app: {}\n");
        var runner = RunnerWithConfigImages("ghcr.io/acme/peramice:sha-abc123\n");

        Assert.True(await new Deployer(runner).DeployAsync(
            Options(tag: "sha-abc123", expectedImage: "ghcr.io/acme/peramice")));
    }

    [Fact]
    public async Task DeployAsync_PruneDisabled_DoesNotPrune()
    {
        var runner = ScriptedRunner();
        var deployer = new Deployer(runner);

        var result = await deployer.DeployAsync(Options(pruneImages: false));

        Assert.True(result);
        Assert.DoesNotContain(runner.Invocations, invocation => invocation.Arguments.Contains("prune"));
    }

    [Fact]
    public async Task DeployAsync_HealthCheckDisabled_SkipsComposePs()
    {
        var runner = ScriptedRunner();
        var deployer = new Deployer(runner);

        var result = await deployer.DeployAsync(Options(healthCheckTimeout: TimeSpan.Zero));

        Assert.True(result);
        // `compose ps` specifically: image retention runs a plain `docker ps` to
        // find images a running container still needs, which is unrelated to the
        // health check this test is about.
        Assert.DoesNotContain(
            runner.Invocations,
            invocation => invocation.Arguments.Contains("compose") && invocation.Arguments.Contains("ps"));
    }

    [Fact]
    public async Task DeployAsync_TagProvided_PinsEnvBeforePull()
    {
        string? tagAtPullTime = null;
        var envFile = Path.Combine(_projectDirectory, ".env");
        var runner = new FakeProcessRunner((_, arguments) =>
        {
            if (arguments.Contains("pull"))
            {
                tagAtPullTime = EnvFileStore.GetValue(envFile, Deployer.TagVariable);
            }

            return arguments.Contains("ps")
                ? new ProcessResult(0, HealthyPs, string.Empty)
                : new ProcessResult(0, string.Empty, string.Empty);
        });
        var deployer = new Deployer(runner);

        var result = await deployer.DeployAsync(Options(tag: "sha-abc123"));

        Assert.True(result);
        Assert.Equal("sha-abc123", tagAtPullTime);
        Assert.Equal("sha-abc123", EnvFileStore.GetValue(envFile, Deployer.TagVariable));
    }

    [Fact]
    public async Task DeployAsync_PullFails_RestoresPreviousTag()
    {
        var envFile = Path.Combine(_projectDirectory, ".env");
        EnvFileStore.SetValue(envFile, Deployer.TagVariable, "sha-old");
        var runner = ScriptedRunner(pullExit: 1);
        var deployer = new Deployer(runner);

        var result = await deployer.DeployAsync(Options(tag: "sha-new"));

        Assert.False(result);
        Assert.Equal("sha-old", EnvFileStore.GetValue(envFile, Deployer.TagVariable));
    }

    [Fact]
    public async Task DeployAsync_HealthCheckFails_RecordsFailed_NoPrune_KeepsNewTag()
    {
        var envFile = Path.Combine(_projectDirectory, ".env");
        EnvFileStore.SetValue(envFile, Deployer.TagVariable, "sha-old");
        var runner = ScriptedRunner(psOutput: """{"Name":"app","State":"exited","Health":""}""");
        var history = new DeployHistoryStore(_composePath);
        var deployer = new Deployer(runner, history: history);

        var result = await deployer.DeployAsync(Options(tag: "sha-new"));

        Assert.False(result);
        Assert.DoesNotContain(runner.Invocations, invocation => invocation.Arguments.Contains("prune"));
        // The new (unhealthy) version IS what is running; .env must reflect it.
        Assert.Equal("sha-new", EnvFileStore.GetValue(envFile, Deployer.TagVariable));

        var record = Assert.Single(history.Load());
        Assert.Equal(DeployRecordValues.ResultFailed, record.Result);
        Assert.Equal(DeployRecordValues.HealthFailed, record.HealthCheck);
        Assert.Equal("sha-new", record.Tag);
        Assert.Equal("sha-old", record.PreviousTag);
    }

    [Fact]
    public async Task DeployAsync_Success_RecordsHistoryAndNotifiesObserver()
    {
        var runner = ScriptedRunner();
        var history = new DeployHistoryStore(_composePath);
        var observer = new RecordingObserver();
        var deployer = new Deployer(runner, history: history, observer: observer);

        var result = await deployer.DeployAsync(Options(tag: "sha-abc123", trigger: DeployRecordValues.TriggerCi));

        Assert.True(result);
        var record = Assert.Single(history.Load());
        Assert.Equal(DeployRecordValues.ResultSucceeded, record.Result);
        Assert.Equal(DeployRecordValues.TriggerCi, record.Trigger);
        Assert.Equal(DeployRecordValues.HealthPassed, record.HealthCheck);

        var outcome = Assert.Single(observer.Outcomes);
        Assert.Equal(DeployRecordValues.ResultSucceeded, outcome.Result);
        Assert.Equal("sha-abc123", outcome.Tag);
    }

    [Fact]
    public async Task DeployAsync_ObserverThrows_DoesNotFailDeploy()
    {
        var runner = ScriptedRunner();
        var deployer = new Deployer(runner, observer: new ThrowingObserver());

        var result = await deployer.DeployAsync(Options());

        Assert.True(result);
    }

    [Fact]
    public async Task DeployAsync_Rollback_ImageLocal_SkipsPull()
    {
        var runner = new FakeProcessRunner((_, arguments) =>
        {
            if (arguments.Contains("config"))
            {
                return new ProcessResult(0, "ghcr.io/o/r:sha-old\n", string.Empty);
            }

            return arguments.Contains("ps")
                ? new ProcessResult(0, HealthyPs, string.Empty)
                : new ProcessResult(0, string.Empty, string.Empty);
        });
        var history = new DeployHistoryStore(_composePath);
        var deployer = new Deployer(runner, history: history);

        var result = await deployer.DeployAsync(Options(tag: "sha-old", trigger: DeployRecordValues.TriggerRollback));

        Assert.True(result);
        Assert.DoesNotContain(runner.Invocations, invocation => invocation.Arguments.Contains("pull"));
        Assert.Contains(runner.Invocations, invocation =>
            invocation.Arguments.Contains("inspect") && invocation.Arguments.Contains("ghcr.io/o/r:sha-old"));
        Assert.Equal(DeployRecordValues.ResultRolledBack, Assert.Single(history.Load()).Result);
    }

    [Fact]
    public async Task DeployAsync_Rollback_ImageMissing_FallsBackToPull()
    {
        var runner = new FakeProcessRunner((_, arguments) =>
        {
            if (arguments.Contains("config"))
            {
                return new ProcessResult(0, "ghcr.io/o/r:sha-old\n", string.Empty);
            }

            if (arguments.Contains("inspect"))
            {
                return new ProcessResult(1, string.Empty, "No such image");
            }

            return arguments.Contains("ps")
                ? new ProcessResult(0, HealthyPs, string.Empty)
                : new ProcessResult(0, string.Empty, string.Empty);
        });
        var deployer = new Deployer(runner);

        var result = await deployer.DeployAsync(Options(tag: "sha-old", trigger: DeployRecordValues.TriggerRollback));

        Assert.True(result);
        Assert.Contains(runner.Invocations, invocation => invocation.Arguments.Contains("pull"));
    }

    // Runner that answers `config --images` with a fixed reference set and
    // otherwise behaves like a healthy deploy.
    private static FakeProcessRunner RunnerWithConfigImages(string configImagesOutput, int configExit = 0) =>
        new((_, arguments) =>
        {
            if (arguments.Contains("config"))
            {
                return new ProcessResult(configExit, configExit == 0 ? configImagesOutput : string.Empty, configExit == 0 ? string.Empty : "boom");
            }

            return arguments.Contains("ps")
                ? new ProcessResult(0, HealthyPs, string.Empty)
                : new ProcessResult(0, string.Empty, string.Empty);
        });

    [Fact]
    public async Task DeployAsync_ExpectedImageMatches_VerifiesBeforePull_Proceeds()
    {
        var runner = RunnerWithConfigImages("ghcr.io/acme/app:sha-abc123\n");
        var deployer = new Deployer(runner);

        var result = await deployer.DeployAsync(Options(tag: "sha-abc123", expectedImage: "ghcr.io/acme/app"));

        Assert.True(result);
        var commands = runner.Invocations.Select(invocation => invocation.CommandLine).ToList();
        // The verification config call runs before the pull.
        Assert.Equal($"docker compose -f {_composePath} config --images", commands[0]);
        Assert.Equal($"docker compose -f {_composePath} pull", commands[1]);
    }

    [Fact]
    public async Task DeployAsync_ExpectedImage_PinsImageInEnvBeforePull()
    {
        var envFile = Path.Combine(_projectDirectory, ".env");
        string? imageAtPullTime = null;
        var runner = new FakeProcessRunner((_, arguments) =>
        {
            if (arguments.Contains("config"))
            {
                return new ProcessResult(0, "ghcr.io/acme/app:sha-abc123\n", string.Empty);
            }

            if (arguments.Contains("pull"))
            {
                imageAtPullTime = EnvFileStore.GetValue(envFile, Deployer.ImageVariable);
            }

            return arguments.Contains("ps")
                ? new ProcessResult(0, HealthyPs, string.Empty)
                : new ProcessResult(0, string.Empty, string.Empty);
        });
        var deployer = new Deployer(runner);

        var result = await deployer.DeployAsync(Options(tag: "sha-abc123", expectedImage: "ghcr.io/acme/app"));

        Assert.True(result);
        Assert.Equal("ghcr.io/acme/app", imageAtPullTime);
        Assert.Equal("ghcr.io/acme/app", EnvFileStore.GetValue(envFile, Deployer.ImageVariable));
    }

    [Fact]
    public async Task DeployAsync_ExpectedImageMismatch_RestoresPreviousImage()
    {
        var envFile = Path.Combine(_projectDirectory, ".env");
        EnvFileStore.SetValue(envFile, Deployer.ImageVariable, "ghcr.io/acme/old-name");
        var runner = RunnerWithConfigImages("ghcr.io/acme/old-name:sha-abc123\n");
        var deployer = new Deployer(runner);

        var result = await deployer.DeployAsync(Options(tag: "sha-abc123", expectedImage: "ghcr.io/acme/new-name"));

        Assert.False(result);
        // The mismatch aborted before any change was applied; the pin is restored.
        Assert.Equal("ghcr.io/acme/old-name", EnvFileStore.GetValue(envFile, Deployer.ImageVariable));
    }

    [Fact]
    public async Task DeployAsync_ExpectedImageMatches_CaseInsensitive_Proceeds()
    {
        var runner = RunnerWithConfigImages("ghcr.io/Acme/App:sha-abc123\n");
        var deployer = new Deployer(runner);

        var result = await deployer.DeployAsync(Options(tag: "sha-abc123", expectedImage: "ghcr.io/acme/app"));

        Assert.True(result);
    }

    [Fact]
    public async Task DeployAsync_ExpectedImageMismatch_FailsBeforePull_RecordsFailed()
    {
        var runner = RunnerWithConfigImages("ghcr.io/acme/old-name:sha-abc123\n");
        var history = new DeployHistoryStore(_composePath);
        var deployer = new Deployer(runner, history: history);

        var result = await deployer.DeployAsync(Options(tag: "sha-abc123", expectedImage: "ghcr.io/acme/new-name"));

        Assert.False(result);
        Assert.DoesNotContain(runner.Invocations, invocation => invocation.Arguments.Contains("pull"));
        var record = Assert.Single(history.Load());
        Assert.Equal(DeployRecordValues.ResultFailed, record.Result);
        Assert.Contains("ghcr.io/acme/new-name", record.Error);
        Assert.Contains("ghcr.io/acme/old-name", record.Error);
    }

    [Fact]
    public async Task DeployAsync_ExpectedImageMismatch_DoesNotPinTag()
    {
        var envFile = Path.Combine(_projectDirectory, ".env");
        EnvFileStore.SetValue(envFile, Deployer.TagVariable, "sha-old");
        var runner = RunnerWithConfigImages("ghcr.io/acme/old-name:sha-old\n");
        var deployer = new Deployer(runner);

        var result = await deployer.DeployAsync(Options(tag: "sha-new", expectedImage: "ghcr.io/acme/new-name"));

        Assert.False(result);
        // Nothing was applied, so the pinned tag must be left untouched.
        Assert.Equal("sha-old", EnvFileStore.GetValue(envFile, Deployer.TagVariable));
    }

    [Fact]
    public async Task DeployAsync_ExpectedImageSet_ConfigUnreadable_SkipsCheck_Proceeds()
    {
        var runner = RunnerWithConfigImages(string.Empty, configExit: 1);
        var deployer = new Deployer(runner);

        var result = await deployer.DeployAsync(Options(tag: "sha-abc123", expectedImage: "ghcr.io/acme/app"));

        Assert.True(result);
        Assert.Contains(runner.Invocations, invocation => invocation.Arguments.Contains("pull"));
    }

    private sealed class RecordingObserver : IDeployObserver
    {
        public List<DeployOutcome> Outcomes { get; } = new();

        public Task OnDeployCompletedAsync(DeployOutcome outcome, CancellationToken cancellationToken)
        {
            Outcomes.Add(outcome);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingObserver : IDeployObserver
    {
        public Task OnDeployCompletedAsync(DeployOutcome outcome, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("notifier down");
    }
}
