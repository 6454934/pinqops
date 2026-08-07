using PinqOps.Proxy;
using PinqOps.Tests.Fakes;
using Xunit;

namespace PinqOps.Tests;

public class PreviewManagerTests : IDisposable
{
    private readonly string _root;
    private readonly string _prodCompose;
    private readonly string _proxyDirectory;

    public PreviewManagerTests()
    {
        _root = Directory.CreateTempSubdirectory("pinqops-preview-tests").FullName;
        _prodCompose = Path.Combine(_root, "docker-compose.yml");
        File.WriteAllText(_prodCompose, "services: {}\n");
        _proxyDirectory = Path.Combine(_root, "proxy");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static PreviewDeployRequest Request(string prodCompose, int pr, string owner = "Acme", string repo = "Shop") =>
        new(prodCompose, owner, repo, pr, $"ghcr.io/{owner.ToLowerInvariant()}/{repo.ToLowerInvariant()}", "sha-abc123", DateTimeOffset.UnixEpoch);

    [Fact]
    public void PreviewDirectory_And_ProjectName_AreDerivedFromPr()
    {
        Assert.Equal(Path.Combine(_root, "previews", "pr-7"), PreviewManager.PreviewDirectory(_prodCompose, 7));
        Assert.Equal(Path.Combine(_root, "previews", "pr-7", "docker-compose.yml"), PreviewManager.PreviewComposeFile(_prodCompose, 7));
        Assert.Equal("shop-pr-7", PreviewManager.PreviewProjectName("Shop", 7));
        Assert.Equal("shop-pr-7-app-1", PreviewManager.PreviewContainerName("Shop", 7));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void PreviewDirectory_RejectsNonPositivePr(int pr)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PreviewManager.PreviewDirectory(_prodCompose, pr));
    }

    [Fact]
    public async Task Deploy_RunsPullThenUpAgainstThePreviewCompose()
    {
        var runner = new FakeProcessRunner();
        var manager = new PreviewManager(runner, _proxyDirectory);

        var result = await manager.DeployAsync(Request(_prodCompose, 12));

        Assert.True(result.Succeeded);
        var composeFile = PreviewManager.PreviewComposeFile(_prodCompose, 12);
        Assert.True(File.Exists(composeFile));

        var dockerCalls = runner.Invocations.Where(i => i.FileName == "docker").ToList();
        Assert.Equal("docker compose -f " + composeFile + " pull", dockerCalls[0].CommandLine);
        Assert.Equal("docker compose -f " + composeFile + " up -d", dockerCalls[1].CommandLine);

        // Both must run from the preview's own directory so its per-PR .env
        // (pinned image/tag/host port) is loaded instead of prod's defaults.
        var previewDirectory = PreviewManager.PreviewDirectory(_prodCompose, 12);
        Assert.Equal(previewDirectory, dockerCalls[0].WorkingDirectory);
        Assert.Equal(previewDirectory, dockerCalls[1].WorkingDirectory);
    }

    [Fact]
    public async Task Deploy_WritesComposeWithPreviewProjectName()
    {
        var manager = new PreviewManager(new FakeProcessRunner(), _proxyDirectory);

        await manager.DeployAsync(Request(_prodCompose, 5));

        var yaml = File.ReadAllText(PreviewManager.PreviewComposeFile(_prodCompose, 5));
        Assert.Equal("shop-pr-5", ComposeProjectName.ReadFrom(yaml));
    }

    /// <summary>
    /// A preview deploy copies the production <c>.env</c> — every materialised
    /// secret in it — into a container built from the pull request's image. That is
    /// deliberate and is what makes a preview behave like production. It is only
    /// safe while the compose file being read belongs to the repository asking.
    ///
    /// <para>It was not checked. Every connected repository's workflow runs on the
    /// same host under the same runner label, and the compose path comes from a
    /// repository variable — which the repository's own owner sets. So one repository
    /// could name another application's compose file and be handed that
    /// application's production credentials, running in an image it controls. The
    /// production deploy path has refused exactly this from the start, on the grounds
    /// that the project name is the owning repository's and is the only durable
    /// marker; the preview path was the way around it.</para>
    /// </summary>
    [Fact]
    public async Task Deploy_RefusesAComposeProjectThatBelongsToAnotherRepository()
    {
        File.WriteAllText(_prodCompose, "name: \"shop\"\nservices: {}\n");
        var prodEnv = PinqOpsStatePaths.EnvFile(_prodCompose);
        EnvFileStore.SetValue(prodEnv, "STRIPE_KEY", "sk_live_do_not_share");

        var runner = new FakeProcessRunner();
        var result = await new PreviewManager(runner, _proxyDirectory)
            .DeployAsync(Request(_prodCompose, 3, owner: "Someone", repo: "Else"));

        Assert.False(result.Succeeded);
        Assert.Contains("shop", result.Error);
        // Nothing written and nothing started: the refusal comes before the copy.
        Assert.False(Directory.Exists(PreviewManager.PreviewDirectory(_prodCompose, 3)));
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task Deploy_AllowsTheRepositoryThatOwnsTheProject()
    {
        File.WriteAllText(_prodCompose, "name: \"shop\"\nservices: {}\n");

        var result = await new PreviewManager(new FakeProcessRunner(), _proxyDirectory)
            .DeployAsync(Request(_prodCompose, 4));

        Assert.True(result.Succeeded, result.Error);
    }

    /// <summary>
    /// A hand-written compose file that declares no project name says nothing about
    /// who owns it, so there is nothing to refuse — the same stance the production
    /// path takes.
    /// </summary>
    [Fact]
    public async Task Deploy_HasNoOpinionWhenTheProjectIsUnnamed()
    {
        var result = await new PreviewManager(new FakeProcessRunner(), _proxyDirectory)
            .DeployAsync(Request(_prodCompose, 5, owner: "Someone", repo: "Else"));

        Assert.True(result.Succeeded, result.Error);
    }

    [Fact]
    public async Task Deploy_CopiesProdEnvExceptImageTagAndHostPort()
    {
        var prodEnv = PinqOpsStatePaths.EnvFile(_prodCompose);
        EnvFileStore.SetValue(prodEnv, "PINQOPS_IMAGE", "ghcr.io/acme/shop");
        EnvFileStore.SetValue(prodEnv, "PINQOPS_TAG", "sha-prod");
        EnvFileStore.SetValue(prodEnv, "PINQOPS_HOST_PORT", "8080");
        EnvFileStore.SetValue(prodEnv, "PINQOPS_CONTAINER_PORT", "3000");
        EnvFileStore.SetValue(prodEnv, "DB_PASSWORD", "hunter2");

        var manager = new PreviewManager(new FakeProcessRunner(), _proxyDirectory);
        var result = await manager.DeployAsync(Request(_prodCompose, 9));

        var previewEnv = PinqOpsStatePaths.EnvFile(PreviewManager.PreviewComposeFile(_prodCompose, 9));

        // Secrets and the container port carry over from prod.
        Assert.Equal("hunter2", EnvFileStore.GetValue(previewEnv, "DB_PASSWORD"));
        Assert.Equal("3000", EnvFileStore.GetValue(previewEnv, "PINQOPS_CONTAINER_PORT"));

        // Image/tag are re-pinned to the PR build; host port is the freshly allocated one.
        Assert.Equal("ghcr.io/acme/shop", EnvFileStore.GetValue(previewEnv, "PINQOPS_IMAGE"));
        Assert.Equal("sha-abc123", EnvFileStore.GetValue(previewEnv, "PINQOPS_TAG"));
        Assert.Equal(result.HostPort.ToString(), EnvFileStore.GetValue(previewEnv, "PINQOPS_HOST_PORT"));
        Assert.NotEqual("8080", EnvFileStore.GetValue(previewEnv, "PINQOPS_HOST_PORT"));
    }

    /// <summary>
    /// The most expensive way this design can go wrong, and the one that looks least
    /// important. The alias is the name the proxy forwards to; a preview that
    /// inherited production's would join production's load-balancing pool and start
    /// answering production traffic from an unreviewed branch.
    /// </summary>
    [Fact]
    public async Task Deploy_NeverInheritsProductionsNetworkAlias()
    {
        var prodEnv = PinqOpsStatePaths.EnvFile(_prodCompose);
        EnvFileStore.SetValue(prodEnv, "PINQOPS_IMAGE", "ghcr.io/acme/shop");
        EnvFileStore.SetValue(prodEnv, Deployer.AliasVariable, "shop");

        var manager = new PreviewManager(new FakeProcessRunner(), _proxyDirectory);
        await manager.DeployAsync(Request(_prodCompose, 9));

        var previewEnv = PinqOpsStatePaths.EnvFile(PreviewManager.PreviewComposeFile(_prodCompose, 9));

        // Absent, not merely different: the compose template falls back to the
        // preview project's own name, so there is no second value to keep correct.
        Assert.Null(EnvFileStore.GetValue(previewEnv, Deployer.AliasVariable));
    }

    /// <summary>
    /// An admin who withdraws a leaked secret expects it gone from everything the
    /// server runs, and the dashboard says it is. The preview <c>.env</c> was merged
    /// into rather than rewritten, so the withdrawn assignment stayed on disk and the
    /// next push to the pull request recreated the preview container with the revoked
    /// credential still in it — indefinitely, because a preview directory outlives
    /// every redeploy and is only removed at teardown.
    /// </summary>
    [Fact]
    public async Task Deploy_RemovesAKeyProductionNoLongerHas()
    {
        var prodEnv = PinqOpsStatePaths.EnvFile(_prodCompose);
        EnvFileStore.SetValue(prodEnv, "STRIPE_KEY", "sk_live_do_not_share");
        EnvFileStore.SetValue(prodEnv, "DB_PASSWORD", "hunter2");

        var manager = new PreviewManager(new FakeProcessRunner(), _proxyDirectory);
        var first = await manager.DeployAsync(Request(_prodCompose, 7));
        Assert.True(first.Succeeded, first.Error);

        var previewEnv = PinqOpsStatePaths.EnvFile(PreviewManager.PreviewComposeFile(_prodCompose, 7));
        Assert.Equal("sk_live_do_not_share", EnvFileStore.GetValue(previewEnv, "STRIPE_KEY"));

        // The admin deletes the secret (or narrows its scope to another app); the
        // secret sync clears it from production's .env and from nowhere else.
        EnvFileStore.RemoveValue(prodEnv, "STRIPE_KEY");

        var second = await manager.DeployAsync(Request(_prodCompose, 7));
        Assert.True(second.Succeeded, second.Error);

        Assert.Null(EnvFileStore.GetValue(previewEnv, "STRIPE_KEY"));

        // The keys the preview pins for itself are not production's and must survive
        // the sweep — they are the only reason the preview runs the PR's image on
        // its own port.
        Assert.Equal("ghcr.io/acme/shop", EnvFileStore.GetValue(previewEnv, "PINQOPS_IMAGE"));
        Assert.Equal("sha-abc123", EnvFileStore.GetValue(previewEnv, "PINQOPS_TAG"));
        Assert.Equal(second.HostPort.ToString(), EnvFileStore.GetValue(previewEnv, "PINQOPS_HOST_PORT"));
    }

    /// <summary>
    /// The sweep only removes what production dropped: a secret production still
    /// has — including one that was rotated since the preview was created — reaches
    /// the preview on the next push, which is what makes a preview behave like
    /// production.
    /// </summary>
    [Fact]
    public async Task Deploy_StillCopiesAKeyProductionKeeps()
    {
        var prodEnv = PinqOpsStatePaths.EnvFile(_prodCompose);
        EnvFileStore.SetValue(prodEnv, "DB_PASSWORD", "hunter2");

        var manager = new PreviewManager(new FakeProcessRunner(), _proxyDirectory);
        Assert.True((await manager.DeployAsync(Request(_prodCompose, 7))).Succeeded);

        EnvFileStore.SetValue(prodEnv, "DB_PASSWORD", "rotated-hunter3");
        EnvFileStore.SetValue(prodEnv, "SMTP_HOST", "mail.example.com");

        Assert.True((await manager.DeployAsync(Request(_prodCompose, 7))).Succeeded);

        var previewEnv = PinqOpsStatePaths.EnvFile(PreviewManager.PreviewComposeFile(_prodCompose, 7));
        Assert.Equal("rotated-hunter3", EnvFileStore.GetValue(previewEnv, "DB_PASSWORD"));
        Assert.Equal("mail.example.com", EnvFileStore.GetValue(previewEnv, "SMTP_HOST"));
    }

    /// <summary>
    /// A preview runs an unreviewed branch with production's secrets in it, so it
    /// must be no better connected than production is: on the app's own network,
    /// where the app's own services are, and not on the shared one, where every
    /// catalog service, every database and every older app answer to container DNS.
    /// The preview was written with no network at all, which meant the shared one —
    /// the reach production was deliberately denied.
    /// </summary>
    [Fact]
    public async Task Deploy_JoinsTheSameNetworkProductionIsOn()
    {
        var appNetwork = AppNetwork.NameFor("shop");
        File.WriteAllText(_prodCompose, ComposeTemplate.Yaml("Acme", "Shop", "shop", 8080, 3000, appNetwork));

        var manager = new PreviewManager(new FakeProcessRunner(), _proxyDirectory);
        Assert.True((await manager.DeployAsync(Request(_prodCompose, 8))).Succeeded);

        var yaml = File.ReadAllText(PreviewManager.PreviewComposeFile(_prodCompose, 8));
        Assert.Contains($"{appNetwork}:\n        aliases:", yaml, StringComparison.Ordinal);
        Assert.Contains($"  {appNetwork}:\n    external: true", yaml, StringComparison.Ordinal);
        // Not on both — being on the shared network too would undo the isolation.
        Assert.DoesNotContain(ComposeTemplate.SharedNetwork, yaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// An app published before app networks existed is on the shared network and
    /// reaches its database through it; its preview has to be there too, or it comes
    /// up without the database production has.
    /// </summary>
    [Fact]
    public async Task Deploy_FallsBackToTheSharedNetworkWhenProductionIsOnIt()
    {
        File.WriteAllText(_prodCompose, ComposeTemplate.Yaml("Acme", "Shop", "shop", 8080, 3000));

        var manager = new PreviewManager(new FakeProcessRunner(), _proxyDirectory);
        Assert.True((await manager.DeployAsync(Request(_prodCompose, 8))).Succeeded);

        var yaml = File.ReadAllText(PreviewManager.PreviewComposeFile(_prodCompose, 8));
        Assert.Contains($"  {ComposeTemplate.SharedNetwork}:\n    external: true", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deploy_FailsWhenPreviewCapReached()
    {
        new PreviewConfigStore(_prodCompose).Save(new PreviewConfig { MaxPreviews = 2 });
        var manager = new PreviewManager(new FakeProcessRunner(), _proxyDirectory);

        Assert.True((await manager.DeployAsync(Request(_prodCompose, 1))).Succeeded);
        Assert.True((await manager.DeployAsync(Request(_prodCompose, 2))).Succeeded);

        var third = await manager.DeployAsync(Request(_prodCompose, 3));
        Assert.False(third.Succeeded);
        Assert.Contains("limit reached", third.Error);
        Assert.False(Directory.Exists(PreviewManager.PreviewDirectory(_prodCompose, 3)));
    }

    [Fact]
    public async Task Deploy_RedeployingExistingPreviewIsAllowedAtCap()
    {
        new PreviewConfigStore(_prodCompose).Save(new PreviewConfig { MaxPreviews = 1 });
        var manager = new PreviewManager(new FakeProcessRunner(), _proxyDirectory);

        Assert.True((await manager.DeployAsync(Request(_prodCompose, 1))).Succeeded);
        // Same PR again — an update, not a new preview, so the cap does not block it.
        Assert.True((await manager.DeployAsync(Request(_prodCompose, 1))).Succeeded);
    }

    [Fact]
    public async Task Deploy_PullFailure_ReportsErrorAndSkipsUp()
    {
        var runner = new FakeProcessRunner((_, args) =>
            args.Contains("pull") ? new ProcessResult(1, string.Empty, "network is unreachable") : new ProcessResult(0, string.Empty, string.Empty));
        var manager = new PreviewManager(runner, _proxyDirectory);

        var result = await manager.DeployAsync(Request(_prodCompose, 4));

        Assert.False(result.Succeeded);
        Assert.Contains("network is unreachable", result.Error);
        Assert.DoesNotContain(runner.Invocations, i => i.Arguments.Contains("up"));
    }

    [Fact]
    public async Task List_ReturnsDeployedPreviewsNewestFirst()
    {
        var manager = new PreviewManager(new FakeProcessRunner(), _proxyDirectory);
        await manager.DeployAsync(Request(_prodCompose, 3));
        await manager.DeployAsync(Request(_prodCompose, 11));

        var previews = PreviewManager.List(_prodCompose, "Shop");

        Assert.Equal(new[] { 11, 3 }, previews.Select(p => p.PullRequestNumber));
        Assert.Equal("shop-pr-11", previews[0].ProjectName);
        Assert.NotNull(previews[0].HostPort);
    }

    [Fact]
    public async Task Teardown_RunsDownAndRemovesDirectory()
    {
        var runner = new FakeProcessRunner();
        var manager = new PreviewManager(runner, _proxyDirectory);
        await manager.DeployAsync(Request(_prodCompose, 6));
        var composeFile = PreviewManager.PreviewComposeFile(_prodCompose, 6);
        runner.Invocations.Clear();

        await manager.TeardownAsync(_prodCompose, "Shop", 6);

        Assert.Contains(runner.Invocations, i => i.CommandLine == $"docker compose -f {composeFile} down -v");
        Assert.False(Directory.Exists(PreviewManager.PreviewDirectory(_prodCompose, 6)));
    }

    [Fact]
    public async Task Teardown_IsIdempotentForAMissingPreview()
    {
        var runner = new FakeProcessRunner();
        var manager = new PreviewManager(runner, _proxyDirectory);

        // Never deployed — must not throw and must not shell out to docker compose down.
        Assert.True(await manager.TeardownAsync(_prodCompose, "Shop", 99));
        Assert.DoesNotContain(runner.Invocations, i => i.Arguments.Contains("down"));
    }

    [Fact]
    public async Task Deploy_RoutesPreviewSubdomain_WhenAppHasADomain()
    {
        // The preview forwards to the same container port the app listens on in prod.
        EnvFileStore.SetValue(PinqOpsStatePaths.EnvFile(_prodCompose), "PINQOPS_CONTAINER_PORT", "3000");
        var store = new DomainConfigStore(_proxyDirectory);
        store.Save(new DomainConfig
        {
            Domains =
            [
                new DomainEntry
                {
                    Domain = "shop.example.com",
                    Target = "acme-shop",
                    TargetContainer = "shop-app-1",
                    TargetPort = 3000,
                    Enabled = true,
                },
            ],
        });

        var runner = WithProxyRunning();
        var manager = new PreviewManager(runner, _proxyDirectory);
        var result = await manager.DeployAsync(Request(_prodCompose, 8));

        Assert.Equal("https://pr-8.shop.example.com", result.Url);
        var saved = store.Load();
        var previewEntry = Assert.Single(saved.Domains, d => d.Domain == "pr-8.shop.example.com");
        Assert.Equal("shop-pr-8-app-1", previewEntry.TargetContainer);
        Assert.Equal(3000, previewEntry.TargetPort);
        Assert.Equal(PreviewManager.PreviewMarker("Shop", 8), previewEntry.Target);
        Assert.Contains(runner.Invocations, i => i.CommandLine == "docker exec pinqops-proxy caddy reload --config /etc/caddy/Caddyfile");
        // The route is in the file Caddy actually reads, not only in domains.json.
        Assert.Contains(
            "pr-8.shop.example.com {",
            File.ReadAllText(ProxyPaths.CaddyfilePath(_proxyDirectory)),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The route is still recorded and rendered when there is no proxy running —
    /// it takes effect the moment one starts. Previously the CLI issued the reload
    /// blindly, which meant a failed <c>docker exec</c> on every preview deploy on a
    /// server with no proxy; the dashboard has always checked first, and both now
    /// go through the same gateway.
    /// </summary>
    [Fact]
    public async Task Deploy_DoesNotReload_WhenTheProxyIsNotRunning()
    {
        EnvFileStore.SetValue(PinqOpsStatePaths.EnvFile(_prodCompose), "PINQOPS_CONTAINER_PORT", "3000");
        new DomainConfigStore(_proxyDirectory).Save(new DomainConfig
        {
            Domains =
            [
                new DomainEntry
                {
                    Domain = "shop.example.com",
                    Target = "acme-shop",
                    TargetContainer = "shop-app-1",
                    TargetPort = 3000,
                    Enabled = true,
                },
            ],
        });

        var runner = new FakeProcessRunner();
        var manager = new PreviewManager(runner, _proxyDirectory);

        await manager.DeployAsync(Request(_prodCompose, 8));

        Assert.DoesNotContain(runner.Invocations, i => i.Arguments.Contains("reload"));
        Assert.Contains(
            "pr-8.shop.example.com {",
            File.ReadAllText(ProxyPaths.CaddyfilePath(_proxyDirectory)),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A runner that answers the two questions the proxy gateway asks docker:
    /// whether the candidate config validates, and whether the proxy is up.
    /// </summary>
    private static FakeProcessRunner WithProxyRunning() =>
        new((_, arguments) => arguments.Contains("inspect")
            ? new ProcessResult(0, "true\n", string.Empty)
            : new ProcessResult(0, string.Empty, string.Empty));

    [Fact]
    public async Task Teardown_RemovesOnlyItsOwnPreviewRoute()
    {
        var store = new DomainConfigStore(_proxyDirectory);
        store.Save(new DomainConfig
        {
            Domains =
            [
                new DomainEntry { Domain = "shop.example.com", Target = "acme-shop", TargetContainer = "shop-app-1", TargetPort = 3000, Enabled = true },
            ],
        });

        var manager = new PreviewManager(new FakeProcessRunner(), _proxyDirectory);
        await manager.DeployAsync(Request(_prodCompose, 8));
        await manager.DeployAsync(Request(_prodCompose, 9));

        await manager.TeardownAsync(_prodCompose, "Shop", 8);

        var saved = store.Load();
        Assert.DoesNotContain(saved.Domains, d => d.Domain == "pr-8.shop.example.com");
        Assert.Contains(saved.Domains, d => d.Domain == "pr-9.shop.example.com");
        Assert.Contains(saved.Domains, d => d.Domain == "shop.example.com");
    }

    [Fact]
    public async Task Deploy_WithoutProxyConfig_StillSucceedsWithNoUrl()
    {
        var manager = new PreviewManager(new FakeProcessRunner(), _proxyDirectory);

        var result = await manager.DeployAsync(Request(_prodCompose, 2));

        Assert.True(result.Succeeded);
        Assert.Null(result.Url);
    }
}
