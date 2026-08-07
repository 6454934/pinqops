using PinqOps;

const string DefaultComposePath = "/opt/pinqops/docker-compose.yml";

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var command = args[0];
var rest = args.Skip(1).ToArray();

try
{
    return command switch
    {
        "setup" => await RunSetupAsync(rest),
        "deploy" => await RunDeployAsync(rest),
        "rollback" => await RunRollbackAsync(rest),
        "history" => RunHistory(rest),
        "install-runner" => await RunInstallRunnerAsync(rest),
        "preview" => await RunPreviewAsync(rest),
        "update" => await RunUpdateAsync(),
        "mcp" => await PinqOps.Cli.McpServer.RunAsync(),
        "version" or "--version" or "-v" => PrintVersion(),
        "help" or "--help" or "-h" => PrintUsage(),
        _ => Unknown(command),
    };
}
catch (Exception exception)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}

async Task<int> RunSetupAsync(string[] setupArgs)
{
    var nonInteractive = HasFlag(setupArgs, "--non-interactive") || Console.IsInputRedirected;

    var options = SetupOptions.Create(
        repositoryUrl: GetOption(setupArgs, "--repo-url") ?? Environment.GetEnvironmentVariable("REPO_URL"),
        personalAccessToken: GetOption(setupArgs, "--pat") ?? Environment.GetEnvironmentVariable("GITHUB_PAT"),
        registrationToken: GetOption(setupArgs, "--token") ?? Environment.GetEnvironmentVariable("RUNNER_TOKEN"),
        composeFilePath: GetOption(setupArgs, "--compose-file") ?? Environment.GetEnvironmentVariable("APP_COMPOSE_PATH"),
        labels: GetOption(setupArgs, "--labels"),
        runnerName: GetOption(setupArgs, "--name"),
        runnerVersion: GetOption(setupArgs, "--version"),
        installDirectory: GetOption(setupArgs, "--dir"),
        serviceUser: GetOption(setupArgs, "--user"),
        nonInteractive: nonInteractive,
        skipPreflight: HasFlag(setupArgs, "--skip-preflight"),
        useGhCli: !HasFlag(setupArgs, "--no-gh"));

    var processRunner = new ProcessRunner();
    using var downloader = new HttpFileDownloader();
    using var gitHubApiClient = new GitHubApiClient();
    var prompt = new ConsolePrompt();

    var prerequisiteChecker = new PrerequisiteChecker(processRunner);
    var dockerBootstrapper = new DockerBootstrapper(processRunner, Console.WriteLine);
    var ghCli = new GhCli(processRunner, Console.WriteLine);
    var tokenResolver = new RegistrationTokenResolver(ghCli, gitHubApiClient, prompt, Console.WriteLine);
    var installer = new RunnerInstaller(processRunner, downloader, Console.WriteLine);
    var wizard = new SetupWizard(
        prerequisiteChecker, dockerBootstrapper, tokenResolver, installer, prompt, Console.WriteLine);

    var succeeded = await wizard.RunAsync(options);
    return succeeded ? 0 : 1;
}

async Task<int> RunDeployAsync(string[] deployArgs)
{
    var composeFilePath = ResolveComposePath(deployArgs);
    var pruneImages = !HasFlag(deployArgs, "--no-prune");
    var timeout = ParseTimeout(GetOption(deployArgs, "--timeout-seconds"));
    var tag = GetOption(deployArgs, "--tag");
    var healthTimeout = ParseHealthTimeout(GetOption(deployArgs, "--health-timeout-seconds"));
    var keepImages = ParseKeepImages(GetOption(deployArgs, "--keep-images"));
    var expectedImage = GetOption(deployArgs, "--image");

    var options = DeployOptions.Create(
        composeFilePath,
        pruneImages,
        timeout,
        tag: tag,
        healthCheckTimeout: healthTimeout,
        keepImages: keepImages,
        trigger: tag is null ? DeployRecordValues.TriggerManual : DeployRecordValues.TriggerCi,
        expectedImage: expectedImage);
    using var notifications = new PinqOps.Notifications.NotificationDispatcher(composeFilePath, Console.WriteLine);

    var startedAt = DateTimeOffset.UtcNow;
    var previousTag = EnvFileStore.GetValue(PinqOpsStatePaths.EnvFile(composeFilePath), Deployer.TagVariable);
    var colored = await TryDeployInColorsAsync(composeFilePath, tag, expectedImage, healthTimeout);
    if (colored is not null)
    {
        // Recorded and announced here, because the cutover sequence itself takes
        // no history store and no observer. Without this, turning a project's
        // colours on switched off its deploy history and its notifications at the
        // same time — silently, and for the one kind of release where knowing it
        // happened matters most.
        await RecordColoredDeployAsync(
            composeFilePath, notifications, colored, options.Trigger, tag, previousTag, startedAt);
        return colored.Succeeded ? 0 : 1;
    }

    var deployer = CreateDeployer(composeFilePath, notifications);

    var succeeded = await deployer.DeployAsync(options);
    return succeeded ? 0 : 1;
}

/// <summary>
/// Runs the deploy as two colours when the project is set up for it. Returns null
/// when it is not, which is every project by default and means the ordinary
/// pull-recreate deploy is what should run.
/// </summary>
async Task<PinqOps.Deploy.BlueGreenResult?> TryDeployInColorsAsync(
    string composeFilePath, string? tag, string? image, TimeSpan? healthTimeout)
{
    var settings = new PinqOps.Deploy.DeploySettingsStore(composeFilePath).Load();
    if (!PinqOps.Deploy.BlueGreenPlan.TryCreate(composeFilePath, settings, out var options, out var problem))
    {
        if (problem is not null)
        {
            // Refused rather than quietly deploying the ordinary way: an operator
            // who turned this on is expecting no gap, and finding out at the next
            // outage that it never ran is worse than a failed deploy now.
            Console.Error.WriteLine($"error: {problem}");
            return new PinqOps.Deploy.BlueGreenResult(
                Succeeded: false, Color: string.Empty, Switched: false, Error: problem);
        }

        return null;
    }

    var runner = new ProcessRunner();
    var gateway = new PinqOps.Proxy.ProxyGateway(
        runner,
        PinqOps.Proxy.ProxyPaths.DefaultDirectory,
        PinqOps.Proxy.ProxyPaths.DefaultImage,
        log: Console.WriteLine);

    var result = await new PinqOps.Deploy.BlueGreenDeployer(runner, gateway, Console.WriteLine)
        .DeployAsync(options! with
        {
            Tag = tag,
            Image = image,
            HealthCheckTimeout = healthTimeout ?? TimeSpan.FromSeconds(60),
        });

    return result;
}

/// <summary>
/// Writes a coloured deploy into the history and hands it to the notification
/// channels — the two things every ordinary deploy does through
/// <see cref="Deployer"/>, which the coloured path does not go through.
/// </summary>
async Task RecordColoredDeployAsync(
    string composeFilePath,
    PinqOps.Notifications.NotificationDispatcher notifications,
    PinqOps.Deploy.BlueGreenResult result,
    string trigger,
    string? tag,
    string? previousTag,
    DateTimeOffset startedAt)
{
    try
    {
        new DeployHistoryStore(composeFilePath).Append(
            PinqOps.Deploy.BlueGreenRecord.For(result, trigger, tag, startedAt, DateTimeOffset.UtcNow, previousTag));
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        // The deploy already happened; failing to write it down must not change
        // what this command reports, the same stance Deployer takes.
        Console.Error.WriteLine($"warning: could not record the deploy: {exception.Message}");
    }

    await notifications.OnDeployCompletedAsync(
        PinqOps.Deploy.BlueGreenRecord.OutcomeFor(result, trigger, tag, previousTag), CancellationToken.None);
}

/// <summary>
/// Rolls back by pointing the proxy at the colour that is still running the wanted
/// version. False means there is no such colour, and the ordinary redeploy is what
/// this rollback needs — which is not a failure and is not reported as one.
/// </summary>
async Task<bool> TrySwitchBackAsync(string composeFilePath, string tag)
{
    var settings = new PinqOps.Deploy.DeploySettingsStore(composeFilePath).Load();
    if (!PinqOps.Deploy.BlueGreenPlan.TryCreate(composeFilePath, settings, out var options, out _))
    {
        return false;
    }

    var runner = new ProcessRunner();
    var gateway = new PinqOps.Proxy.ProxyGateway(
        runner,
        PinqOps.Proxy.ProxyPaths.DefaultDirectory,
        PinqOps.Proxy.ProxyPaths.DefaultImage,
        log: Console.WriteLine);

    return await new PinqOps.Deploy.BlueGreenDeployer(runner, gateway, Console.WriteLine)
        .TrySwitchBackAsync(options!, tag);
}

async Task<int> RunRollbackAsync(string[] rollbackArgs)
{
    var composeFilePath = ResolveComposePath(rollbackArgs);
    var history = new DeployHistoryStore(composeFilePath);

    var currentTag = EnvFileStore.GetValue(PinqOpsStatePaths.EnvFile(composeFilePath), Deployer.TagVariable);
    var targetTag = GetOption(rollbackArgs, "--to") ?? history.LastSuccessfulTagBefore(currentTag);
    if (targetTag is null)
    {
        Console.Error.WriteLine(
            "error: no rollback target. Deploy history has no earlier successful tag; "
            + "pass one explicitly with --to <tag>.");
        return 1;
    }

    if (!ComposeUsesTagVariable(composeFilePath))
    {
        Console.Error.WriteLine(
            $"error: {composeFilePath} does not reference ${{{Deployer.TagVariable}}}. "
            + $"Change the image line to e.g. 'image: ghcr.io/<owner>/<repo>:${{{Deployer.TagVariable}:-latest}}' first.");
        return 1;
    }

    Console.WriteLine($"rolling back to {targetTag}" + (currentTag is null ? string.Empty : $" (currently {currentTag})"));

    using var notifications = new PinqOps.Notifications.NotificationDispatcher(composeFilePath, Console.WriteLine);

    // The version being rolled back to may still be running with no traffic, in
    // which case this is a proxy reload rather than a pull and a restart.
    var startedAt = DateTimeOffset.UtcNow;
    if (await TrySwitchBackAsync(composeFilePath, targetTag))
    {
        // Recorded like every other rollback: without the rolled_back record naming
        // what was escaped, the next default rollback walked straight back onto it —
        // and the switch was invisible in history and the notification channels.
        try
        {
            new DeployHistoryStore(composeFilePath).Append(
                PinqOps.Deploy.BlueGreenRecord.ForSwitchBack(
                    targetTag, currentTag, startedAt, DateTimeOffset.UtcNow));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"warning: could not record the rollback: {exception.Message}");
        }

        await notifications.OnDeployCompletedAsync(
            PinqOps.Deploy.BlueGreenRecord.OutcomeForSwitchBack(targetTag, currentTag), CancellationToken.None);
        return 0;
    }

    var healthTimeout = ParseHealthTimeout(GetOption(rollbackArgs, "--health-timeout-seconds"));

    // No shortcut, so this is a full redeploy of the earlier tag — and for a
    // coloured project that redeploy has to be coloured too. Falling through to
    // the ordinary deployer started a third compose project that nothing routed
    // to, and reported the rollback as done.
    var colored = await TryDeployInColorsAsync(composeFilePath, targetTag, image: null, healthTimeout);
    if (colored is not null)
    {
        await RecordColoredDeployAsync(
            composeFilePath, notifications, colored, DeployRecordValues.TriggerRollback, targetTag, currentTag, startedAt);
        return colored.Succeeded ? 0 : 1;
    }

    var options = DeployOptions.Create(
        composeFilePath,
        tag: targetTag,
        healthCheckTimeout: healthTimeout,
        trigger: DeployRecordValues.TriggerRollback);
    var deployer = CreateDeployer(composeFilePath, notifications);

    var succeeded = await deployer.DeployAsync(options);
    return succeeded ? 0 : 1;
}

int RunHistory(string[] historyArgs)
{
    var composeFilePath = ResolveComposePath(historyArgs);
    var records = new DeployHistoryStore(composeFilePath).Load();

    if (HasFlag(historyArgs, "--json"))
    {
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
            records,
            new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            }));
        return 0;
    }

    if (records.Count == 0)
    {
        Console.WriteLine("no deploys recorded yet");
        return 0;
    }

    Console.WriteLine($"{"WHEN (UTC)",-17} {"TAG",-45} {"RESULT",-12} {"TRIGGER",-9} HEALTH");
    foreach (var record in records.Take(15))
    {
        Console.WriteLine(
            $"{record.StartedAt.UtcDateTime:yyyy-MM-dd HH:mm}  {record.Tag,-45} {record.Result,-12} {record.Trigger,-9} {record.HealthCheck}");
    }

    return 0;
}

Deployer CreateDeployer(string composeFilePath, PinqOps.Notifications.NotificationDispatcher notifications) =>
    new(new ProcessRunner(), Console.WriteLine, history: new DeployHistoryStore(composeFilePath), observer: notifications);

static string ResolveComposePath(string[] args) =>
    GetOption(args, "--compose-file")
    ?? Environment.GetEnvironmentVariable("APP_COMPOSE_PATH")
    ?? DefaultComposePath;

static bool ComposeUsesTagVariable(string composeFilePath) =>
    File.Exists(composeFilePath)
    && File.ReadAllText(composeFilePath).Contains($"${{{Deployer.TagVariable}", StringComparison.Ordinal);

async Task<int> RunInstallRunnerAsync(string[] installArgs)
{
    var options = RunnerInstallOptions.Create(
        repositoryUrl: GetOption(installArgs, "--repo-url") ?? Environment.GetEnvironmentVariable("REPO_URL"),
        registrationToken: GetOption(installArgs, "--token") ?? Environment.GetEnvironmentVariable("RUNNER_TOKEN"),
        labels: GetOption(installArgs, "--labels"),
        runnerName: GetOption(installArgs, "--name"),
        runnerVersion: GetOption(installArgs, "--version"),
        installDirectory: GetOption(installArgs, "--dir"));

    var serviceUser = GetOption(installArgs, "--user") ?? Environment.UserName;

    using var downloader = new HttpFileDownloader();
    var installer = new RunnerInstaller(new ProcessRunner(), downloader, Console.WriteLine);

    var succeeded = await installer.InstallAsync(options, serviceUser);
    return succeeded ? 0 : 1;
}

async Task<int> RunPreviewAsync(string[] previewArgs)
{
    if (previewArgs.Length == 0)
    {
        Console.Error.WriteLine("error: preview needs a subcommand: deploy or teardown");
        return 1;
    }

    var subcommand = previewArgs[0];
    var previewRest = previewArgs.Skip(1).ToArray();
    var composeFilePath = ResolveComposePath(previewRest);
    var pr = ParsePr(GetOption(previewRest, "--pr"));

    var manager = new PreviewManager(new ProcessRunner(), log: Console.WriteLine);

    switch (subcommand)
    {
        case "deploy":
            var (owner, repo) = ResolvePreviewRepo(previewRest);
            var image = GetOption(previewRest, "--image")
                ?? throw new ArgumentException("preview deploy needs --image (e.g. ghcr.io/<owner>/<repo>).");
            var tag = GetOption(previewRest, "--tag")
                ?? throw new ArgumentException("preview deploy needs --tag (e.g. sha-<commit>).");
            var request = new PreviewDeployRequest(composeFilePath, owner, repo, pr, image, tag, DateTimeOffset.UtcNow);
            var result = await manager.DeployAsync(request);
            if (!result.Succeeded)
            {
                Console.Error.WriteLine($"error: {result.Error}");
                return 1;
            }

            return 0;

        case "teardown":
            // teardown brings the compose project down and deletes its directory
            // from the compose path + PR alone; repo is only needed to drop the
            // proxy route, so it stays optional here (the workflow passes --image;
            // a manual `preview teardown --pr N` no longer errors out). Warn when
            // it's unknown so a leftover route isn't a silent surprise.
            var teardownRepo = ResolvePreviewRepoOptional(previewRest);
            if (teardownRepo is null)
            {
                Console.WriteLine(
                    "note: no --repo/--image given, so a preview domain route (if any) can't be removed; "
                    + "pass --repo <r> to clean that up too.");
            }

            // A failed `compose down` leaves the preview running, so the workflow's
            // teardown job has to go red rather than green: exiting 0 on it made an
            // orphaned preview — containers, volumes and host port — invisible.
            if (!await manager.TeardownAsync(composeFilePath, teardownRepo ?? string.Empty, pr))
            {
                Console.Error.WriteLine(
                    $"error: preview teardown for PR #{pr} failed; the preview is still running. "
                    + "See the log above and retry.");
                return 1;
            }

            return 0;

        default:
            Console.Error.WriteLine($"error: unknown preview subcommand '{subcommand}' (expected deploy or teardown)");
            return 1;
    }
}

static int ParsePr(string? raw)
{
    if (!int.TryParse(raw, out var pr) || pr <= 0)
    {
        throw new ArgumentException($"--pr must be a positive pull request number, got '{raw}'.");
    }

    return pr;
}

// Owner/repo come from --owner/--repo when given, otherwise from the image
// reference (ghcr.io/<owner>/<repo>) — the workflow always passes --image.
static (string Owner, string Repo) ResolvePreviewRepo(string[] args)
{
    var owner = GetOption(args, "--owner");
    var repo = GetOption(args, "--repo");
    if (owner is not null && repo is not null)
    {
        return (owner, repo);
    }

    var image = GetOption(args, "--image");
    if (image is not null)
    {
        var path = image.Contains('/') ? image[(image.IndexOf('/') + 1)..] : image;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2)
        {
            return (owner ?? segments[^2], repo ?? segments[^1]);
        }
    }

    throw new ArgumentException("preview needs --owner and --repo, or an --image to derive them from.");
}

// Just the repo for teardown: from --repo, else the last segment of --image, else
// null. Owner isn't needed to tear down, so this never throws.
static string? ResolvePreviewRepoOptional(string[] args)
{
    if (GetOption(args, "--repo") is { } repo)
    {
        return repo;
    }

    var image = GetOption(args, "--image");
    if (image is not null)
    {
        var path = image.Contains('/') ? image[(image.IndexOf('/') + 1)..] : image;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 1)
        {
            return segments[^1];
        }
    }

    return null;
}

async Task<int> RunUpdateAsync()
{
    Console.WriteLine($"pinqops {PinqOpsVersion.Current} — checking for the latest release…");
    using var downloader = new HttpFileDownloader();

    // Reading the new binary's version means spawning it, and spawning anything
    // is impossible once this binary — the bundle its own assemblies are read
    // out of — has been replaced. See ProcessRunner.Preload.
    ProcessRunner.Preload();

    var updated = await new SelfUpdater(downloader, Console.WriteLine).UpdateAsync("pinqops");
    if (updated is null)
    {
        return 1;
    }

    // Report the version the freshly installed binary now prints.
    try
    {
        var version = await new ProcessRunner().RunAsync(updated, new[] { "version" });
        Console.WriteLine(version.Succeeded
            ? $"now on {version.StandardOutput.Trim()}"
            : "update complete.");
    }
    catch (Exception exception)
    {
        // The binary is installed; only the version read-back failed. That is
        // not worth failing an update over, let alone with a stack trace.
        Console.WriteLine($"update complete (could not read the new version back: {exception.Message}).");
    }

    return 0;
}

int PrintVersion()
{
    Console.WriteLine($"pinqops {PinqOpsVersion.Current}");
    return 0;
}

int Unknown(string unknownCommand)
{
    Console.Error.WriteLine($"error: unknown command '{unknownCommand}'");
    PrintUsage();
    return 1;
}

int PrintUsage()
{
    Console.WriteLine(
        """
        pinqops — minimal DevOps CLI for closed-server Docker deploys

        Usage:
          pinqops setup [--repo-url <url>] [--pat <pat>] [--token <registration-token>]
                        [--compose-file <path>] [--labels <l>] [--name <name>]
                        [--version <runner-version>] [--dir <path>] [--user <user>]
                        [--no-gh] [--skip-preflight] [--non-interactive]
              Guided onboarding for a fresh server: check prerequisites, obtain a
              runner registration token (authenticated gh CLI, a PAT via the
              GitHub API, or a pasted token), install the self-hosted runner, and
              print the remaining compose steps. Run it and answer the prompts.

          pinqops deploy [--compose-file <path>] [--tag <image-tag>] [--no-prune]
                         [--timeout-seconds <n>] [--health-timeout-seconds <n>]
                         [--keep-images <n>] [--image <registry/path>]
              Pull the new image and restart the fixed compose project. With
              --tag, pins PINQOPS_TAG in the project's .env so the exact image
              version is recorded and can be rolled back later. With --image
              (e.g. ghcr.io/<owner>/<repo>), verifies the compose file targets
              that image before pulling and fails fast with a clear message if
              the server compose file is stale (e.g. after a repo rename). After
              up -d the services are health-checked (default 60s; 0 skips). The
              newest --keep-images sha-* images (default 5) are kept for rollback.
              Defaults: --compose-file from $APP_COMPOSE_PATH or /opt/pinqops/docker-compose.yml

          pinqops rollback [--to <tag>] [--compose-file <path>] [--health-timeout-seconds <n>]
              Redeploy a previously deployed image tag. Defaults to the last
              successful tag before the current one (from deploy history). Uses
              the locally kept image, so no registry credentials are needed
              within the retention window.

          pinqops history [--compose-file <path>] [--json]
              Show recent deploys (what, when, result, health).

          pinqops install-runner --repo-url <url> --token <token>
                                 [--labels <labels>] [--name <name>]
                                 [--version <runner-version>] [--dir <path>] [--user <user>]
              Install and register a GitHub Actions self-hosted runner as a
              systemd service (outbound-only; no inbound port on the server).

          pinqops preview deploy --pr <n> --image <registry/path> --tag <image-tag>
                                 [--compose-file <path>] [--owner <o>] [--repo <r>]
          pinqops preview teardown --pr <n> [--compose-file <path>] [--repo <r>]
              Bring up (or tear down) a per-PR preview environment as its own
              compose project (<repo>-pr-<n>) on a free host port, next to
              production. deploy reuses production's .env — minus the pinned
              image/tag/host-port — pulls the PR image and starts it, routing
              pr-<n>.<domain> to it when the app has a domain. teardown removes
              the project, its volumes and its route. Both are invoked by the PR
              workflow on the runner. Owner/repo default to the --image path.

          pinqops update
              Replace this binary in place with the latest published release
              (self-contained linux-x64). Run with sudo if pinqops lives in a
              root-owned directory such as /usr/local/bin.

          pinqops mcp
              Run a Model Context Protocol server (stdio) that exposes the
              dashboard's API as agent tools — works with any MCP client
              (Claude Code/Desktop, Cursor, the OpenAI Agents SDK / Codex).
              Prefer the remote dashboard endpoint instead (no local binary):
                URL  http(s)://<host>:7467/mcp
                Header Authorization: Bearer pot_…
              Local stdio bridge still works — reads PINQOPS_URL and PINQOPS_TOKEN (a 'pot_…' token from the
              dashboard's Settings → API tokens); PINQOPS_INSECURE=1 accepts a
              self-signed cert.

          pinqops version
          pinqops help
        """);
    return 0;
}

/// <summary>
/// The value following <paramref name="name"/>, or null when the flag is absent.
///
/// A flag that IS present but has no usable value is an error rather than null.
/// Scanning to <c>args.Length - 1</c> meant a flag in last position silently read
/// as absent, so `pinqops rollback --to` rolled back to whatever the history
/// default was and exited 0 — doing something other than what was asked, with no
/// indication. No option here legitimately takes a '-'-leading value, so a
/// following token that looks like a flag is the same mistake.
/// </summary>
static string? GetOption(string[] args, string name)
{
    for (var index = 0; index < args.Length; index++)
    {
        if (args[index] != name)
        {
            continue;
        }

        if (index + 1 >= args.Length || args[index + 1].StartsWith('-'))
        {
            throw new ArgumentException($"{name} requires a value.");
        }

        return args[index + 1];
    }

    return null;
}

static bool HasFlag(string[] args, string name) => Array.IndexOf(args, name) >= 0;

static TimeSpan? ParseTimeout(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw))
    {
        return null;
    }

    if (!int.TryParse(raw, out var seconds) || seconds <= 0)
    {
        throw new ArgumentException($"--timeout-seconds must be a positive integer, got '{raw}'.");
    }

    return TimeSpan.FromSeconds(seconds);
}

static TimeSpan? ParseHealthTimeout(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw))
    {
        return null;
    }

    if (!int.TryParse(raw, out var seconds) || seconds < 0)
    {
        throw new ArgumentException($"--health-timeout-seconds must be a non-negative integer (0 skips), got '{raw}'.");
    }

    return TimeSpan.FromSeconds(seconds);
}

static int ParseKeepImages(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw))
    {
        return 5;
    }

    if (!int.TryParse(raw, out var count) || count < 1)
    {
        throw new ArgumentException($"--keep-images must be a positive integer, got '{raw}'.");
    }

    return count;
}
