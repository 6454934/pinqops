using System.Globalization;
using PinqOps.Proxy;

namespace PinqOps;

/// <summary>
/// Manages per-PR preview environments on the server. A preview is a second,
/// throwaway compose project (<c>&lt;repo&gt;-pr-&lt;n&gt;</c>) that runs the image built
/// for a pull request on its own free host port, next to production. It reuses
/// production's <c>.env</c> — minus the pinned image/tag/host-port — so previews
/// behave like prod without a separate setup.
/// </summary>
/// <remarks>
/// Runs on the runner (invoked by <c>pinqops preview deploy|teardown</c> from the
/// PR workflow), so it talks to Docker and the proxy directly and never depends
/// on the dashboard being reachable — the "no inbound port" model holds.
/// </remarks>
public sealed class PreviewManager
{
    private const string DockerExecutable = "docker";

    /// <summary>Preview host ports start here; the PR number spreads them out before the free-port scan.</summary>
    public const int HostPortBase = 9100;

    /// <summary>
    /// The keys the preview writes for itself, so they survive the sweep that drops
    /// everything production no longer has. The alias is deliberately not among
    /// them: it is dropped and never re-pinned.
    /// </summary>
    private static readonly string[] PinnedPerPreview =
    [
        Deployer.TagVariable,
        Deployer.ImageVariable,
        Deployer.HostPortVariable,
    ];

    /// <summary>
    /// Keys re-pinned per environment — never copied from prod's <c>.env</c> into a
    /// preview.
    ///
    /// <para><see cref="Deployer.AliasVariable"/> is the one that matters most here
    /// and the one that looks least important. The alias is the name the proxy
    /// forwards to; a preview that inherited production's would join production's
    /// pool and start answering production's traffic, from an unreviewed branch.
    /// Everything else on this list produces a preview that is merely wrong.</para>
    /// </summary>
    private static readonly string[] NotCopiedFromProd =
    [
        .. PinnedPerPreview,
        Deployer.AliasVariable,
    ];

    /// <summary>The unindented key opening a compose file's network declarations.</summary>
    private const string NetworksKey = "networks:";

    /// <summary>The key marking a declared network as one docker already has.</summary>
    private const string ExternalKey = "external:";

    /// <summary>The only value of <see cref="ExternalKey"/> that names a network pinqops created.</summary>
    private const string ExternalTrue = "true";

    private readonly IProcessRunner _runner;
    private readonly string _proxyDirectory;
    private readonly Action<string>? _log;

    public PreviewManager(IProcessRunner runner, string? proxyDirectory = null, Action<string>? log = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _proxyDirectory = proxyDirectory ?? ProxyPaths.DefaultDirectory;
        _log = log;
    }

    /// <summary>The directory holding all of an app's previews, e.g. <c>/opt/pinqops/apps/x/previews</c>.</summary>
    public static string PreviewsRoot(string prodComposeFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prodComposeFilePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(prodComposeFilePath));
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("Compose file path has no parent directory.", nameof(prodComposeFilePath));
        }

        return Path.Combine(directory, "previews");
    }

    /// <summary>One preview's directory, <c>&lt;previews&gt;/pr-&lt;n&gt;</c>.</summary>
    public static string PreviewDirectory(string prodComposeFilePath, int pullRequestNumber)
    {
        RequireValidPr(pullRequestNumber);
        return Path.Combine(PreviewsRoot(prodComposeFilePath), $"pr-{pullRequestNumber}");
    }

    /// <summary>The preview's compose file path.</summary>
    public static string PreviewComposeFile(string prodComposeFilePath, int pullRequestNumber) =>
        Path.Combine(PreviewDirectory(prodComposeFilePath, pullRequestNumber), "docker-compose.yml");

    /// <summary>The preview's compose project name, <c>&lt;repo&gt;-pr-&lt;n&gt;</c>.</summary>
    public static string PreviewProjectName(string repo, int pullRequestNumber)
    {
        RequireValidPr(pullRequestNumber);
        return $"{ComposeProjectName.FromRepository(repo)}-pr-{pullRequestNumber}";
    }

    /// <summary>The container name compose creates for the preview, <c>&lt;repo&gt;-pr-&lt;n&gt;-app-1</c>.</summary>
    public static string PreviewContainerName(string repo, int pullRequestNumber) =>
        $"{PreviewProjectName(repo, pullRequestNumber)}-app-1";

    /// <summary>
    /// The host ports this app's other previews have recorded — taken whether or
    /// not anything is bound to them right now, because a stopped preview still
    /// owns its port for the next push.
    /// </summary>
    private static HashSet<int> RecordedPreviewPorts(string prodComposeFilePath, int excludePullRequestNumber)
    {
        var ports = new HashSet<int>();
        var root = PreviewsRoot(prodComposeFilePath);
        if (!Directory.Exists(root))
        {
            return ports;
        }

        foreach (var directory in Directory.GetDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (!name.StartsWith("pr-", StringComparison.Ordinal)
                || !int.TryParse(name["pr-".Length..], out var pr)
                || pr <= 0
                || pr == excludePullRequestNumber)
            {
                continue;
            }

            if (ParsePort(EnvFileStore.GetValue(Path.Combine(directory, ".env"), Deployer.HostPortVariable)) is { } port)
            {
                ports.Add(port);
            }
        }

        return ports;
    }

    /// <summary>Every preview currently on disk for this app, newest PR first.</summary>
    public static IReadOnlyList<PreviewInfo> List(string prodComposeFilePath, string repo)
    {
        var root = PreviewsRoot(prodComposeFilePath);
        if (!Directory.Exists(root))
        {
            return [];
        }

        var previews = new List<PreviewInfo>();
        foreach (var directory in Directory.GetDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (!name.StartsWith("pr-", StringComparison.Ordinal)
                || !int.TryParse(name["pr-".Length..], out var pr)
                || pr <= 0)
            {
                continue;
            }

            var composeFile = Path.Combine(directory, "docker-compose.yml");
            var envFile = Path.Combine(directory, ".env");
            var hostPort = ParsePort(EnvFileStore.GetValue(envFile, Deployer.HostPortVariable));
            previews.Add(new PreviewInfo(pr, PreviewProjectName(repo, pr), composeFile, hostPort));
        }

        return previews.OrderByDescending(preview => preview.PullRequestNumber).ToList();
    }

    /// <summary>
    /// Creates or updates the preview for a PR: writes its compose file and a
    /// prod-derived <c>.env</c>, pulls the built image, brings it up, and (when the
    /// app has a domain) routes <c>pr-&lt;n&gt;.&lt;domain&gt;</c> to it. Idempotent — a second
    /// deploy of the same PR just re-pins the new image and restarts.
    /// </summary>
    public async Task<PreviewDeployResult> DeployAsync(PreviewDeployRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireValidPr(request.PullRequestNumber);

        // Before anything is read, and above all before prod's .env is: does this
        // project belong to the repository asking? The preview is built from the pull
        // request's image and handed prod's secrets deliberately — that is what makes
        // it behave like production — so the file being read has to be this
        // repository's own. The compose path arrives from a repository variable, which
        // the repository's owner sets, and every connected repository's workflow runs
        // on the same host; without this, naming another application's file was a way
        // to be handed that application's production credentials. The deploy path has
        // refused this from the start.
        if (Deployer.ProjectOwnerMismatch(request.Repo, request.ProdComposeFilePath) is { } mismatch)
        {
            _log?.Invoke(mismatch);
            return new PreviewDeployResult(false, 0, null, mismatch);
        }

        var alreadyDeployed = Directory.Exists(PreviewDirectory(request.ProdComposeFilePath, request.PullRequestNumber));
        if (!alreadyDeployed)
        {
            var max = new PreviewConfigStore(request.ProdComposeFilePath).Load().MaxPreviews;
            var running = List(request.ProdComposeFilePath, request.Repo).Count;
            if (running >= max)
            {
                var error = $"preview limit reached ({running}/{max}). Tear down an old preview or raise MaxPreviews.";
                _log?.Invoke(error);
                return new PreviewDeployResult(false, 0, null, error);
            }
        }

        // Prod's container port carries over (the app listens on the same port in
        // every environment); everything else about ports/images is per-preview.
        // The fallback is the one everything else uses (DockerfileInspector.DefaultPort)
        // — a private 8080 here meant an app with no recorded container port was
        // served on 80 in production and mapped to 8080 in its previews, which
        // answered on neither address for a reason no log mentioned.
        var prodEnv = PinqOpsStatePaths.EnvFile(request.ProdComposeFilePath);
        var containerPort = ParsePort(EnvFileStore.GetValue(prodEnv, Deployer.ContainerPortVariable))
            ?? DockerfileInspector.DefaultPort;

        // A redeploy reuses the port already recorded for this preview. Probing for
        // a free one binds 0.0.0.0, which the preview's OWN running container holds,
        // so the probe always reported it taken and FindAvailable handed back the
        // next port up — moving the advertised URL on alternating pushes. HostPort's
        // own remark says the probe is only meaningful before the app owns the port.
        var previewEnvFile = PinqOpsStatePaths.EnvFile(
            PreviewComposeFile(request.ProdComposeFilePath, request.PullRequestNumber));
        var recordedPort = alreadyDeployed
            ? ParsePort(EnvFileStore.GetValue(previewEnvFile, Deployer.HostPortVariable))
            : null;

        var directory = PreviewDirectory(request.ProdComposeFilePath, request.PullRequestNumber);
        var composeFile = PreviewComposeFile(request.ProdComposeFilePath, request.PullRequestNumber);
        var projectName = PreviewProjectName(request.Repo, request.PullRequestNumber);

        // The preview joins whatever network prod is on. Left unsaid, the template
        // fills in the shared network — so a preview running an unreviewed branch
        // with prod's secrets landed next to every catalog service, database and
        // older app and could reach all of them by container DNS, which prod itself
        // deliberately cannot. On a host with no proxy and no catalog app the shared
        // network does not even exist, and `compose up` died declaring it external.
        var network = ReadProdNetwork(request.ProdComposeFilePath);

        void WritePreviewFiles(int chosenPort)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                composeFile,
                ComposeTemplate.Yaml(request.Owner, request.Repo, projectName, chosenPort, containerPort, network));
            WritePreviewEnv(prodEnv, PinqOpsStatePaths.EnvFile(composeFile), request.Image, request.Tag, chosenPort);
        }

        int hostPort;
        if (recordedPort is { } recorded)
        {
            hostPort = recorded;
            WritePreviewFiles(hostPort);
        }
        else
        {
            // Chosen under a lock, against the recorded owners as well as a bind
            // probe. The probe alone raced: PR N and PR N+400 prefer the same slot,
            // the workflow's concurrency group is per-PR, and the probe releases the
            // socket long before `compose up` binds it — so two simultaneous deploys
            // both saw the port free and the loser died on "port is already
            // allocated". The lock serialises choosing-and-recording; a port already
            // written into a sibling preview's .env (or prod's own) is taken even
            // when nothing is bound to it right now.
            try
            {
                using var allocation = await Deploy.DeployGate.AcquireFileAsync(
                    Path.Combine(PreviewsRoot(request.ProdComposeFilePath), ".port-allocation.lock"),
                    TimeSpan.FromSeconds(30),
                    _log,
                    cancellationToken).ConfigureAwait(false);

                var reserved = RecordedPreviewPorts(request.ProdComposeFilePath, request.PullRequestNumber);
                if (ParsePort(EnvFileStore.GetValue(prodEnv, Deployer.HostPortVariable)) is { } prodPort)
                {
                    reserved.Add(prodPort);
                }

                var chosen = HostPort.FindAvailable(
                    HostPortBase + request.PullRequestNumber % 400,
                    port => !reserved.Contains(port) && HostPort.IsAvailable(port));
                if (chosen is null)
                {
                    var error = "no free host port for the preview — every candidate port is taken.";
                    _log?.Invoke(error);
                    return new PreviewDeployResult(false, 0, null, error);
                }

                hostPort = chosen.Value;

                // Written before the lock releases, so the next allocation sees
                // this port as recorded.
                WritePreviewFiles(hostPort);
            }
            catch (InvalidOperationException exception)
            {
                _log?.Invoke(exception.Message);
                return new PreviewDeployResult(false, 0, null, exception.Message);
            }
        }

        var composeDirectory = PinqOpsStatePaths.ComposeWorkingDirectory(composeFile);
        var pullFailure = await RunStepAsync(DockerComposeCommandBuilder.Pull(composeFile), composeDirectory, cancellationToken).ConfigureAwait(false);
        if (pullFailure is not null)
        {
            return new PreviewDeployResult(false, hostPort, null, $"image pull failed: {pullFailure}");
        }

        var upFailure = await RunStepAsync(DockerComposeCommandBuilder.Up(composeFile), composeDirectory, cancellationToken).ConfigureAwait(false);
        if (upFailure is not null)
        {
            return new PreviewDeployResult(false, hostPort, null, $"compose up failed: {upFailure}");
        }

        var url = await RegisterPreviewDomainAsync(request, containerPort, cancellationToken).ConfigureAwait(false);
        _log?.Invoke(url is not null
            ? $"preview for PR #{request.PullRequestNumber} is up at {url} (and http://<server>:{hostPort})"
            : $"preview for PR #{request.PullRequestNumber} is up at http://<server>:{hostPort}");
        return new PreviewDeployResult(true, hostPort, url, null);
    }

    /// <summary>
    /// Removes a PR's preview: brings the project down with its volumes, deletes
    /// the directory, and drops its proxy route. Idempotent — tearing down a
    /// preview that was never created (or was already removed) is a no-op success.
    /// </summary>
    /// <returns>
    /// False when <c>compose down</c> failed. The directory is then left in place:
    /// its compose file is the only thing that names the project, so deleting it
    /// after a failed <c>down</c> orphaned the containers, volumes and host port
    /// with no way to retry — and reported success while doing it.
    /// </returns>
    public async Task<bool> TeardownAsync(string prodComposeFilePath, string repo, int pullRequestNumber, CancellationToken cancellationToken = default)
    {
        RequireValidPr(pullRequestNumber);

        var composeFile = PreviewComposeFile(prodComposeFilePath, pullRequestNumber);
        if (File.Exists(composeFile))
        {
            // down -v: a preview's data is throwaway, so its volumes go with it.
            var downFailure = await RunStepAsync(
                new[] { "compose", "-f", composeFile, "down", "-v" },
                PinqOpsStatePaths.ComposeWorkingDirectory(composeFile),
                cancellationToken).ConfigureAwait(false);
            if (downFailure is not null)
            {
                _log?.Invoke(
                    $"compose down failed for PR #{pullRequestNumber}: {downFailure}. Leaving "
                    + $"{PreviewDirectory(prodComposeFilePath, pullRequestNumber)} in place so teardown can be retried.");
                return false;
            }
        }

        var directory = PreviewDirectory(prodComposeFilePath, pullRequestNumber);
        if (Directory.Exists(directory))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"could not delete {directory}: {exception.Message}");
            }
        }

        await RemovePreviewDomainAsync(repo, pullRequestNumber, cancellationToken).ConfigureAwait(false);
        _log?.Invoke($"preview for PR #{pullRequestNumber} torn down");
        return true;
    }

    /// <summary>
    /// Builds the preview's <c>.env</c>: every prod assignment except the
    /// per-environment keys in <see cref="NotCopiedFromProd"/>, then the preview's
    /// own image, tag and host port. App secrets are copied deliberately so the
    /// preview behaves like prod.
    ///
    /// <para>The alias is dropped and never re-pinned, so compose falls back to the
    /// preview project's own name — which is what keeps it out of production's
    /// traffic pool.</para>
    ///
    /// <para>It is a replacement, not a merge. A preview directory outlives every
    /// redeploy — only teardown removes it — so a key that was copied here once and
    /// has since been withdrawn from prod (a leaked secret an admin deleted, or one
    /// re-scoped to another app) stayed in this file and went back into the preview
    /// container on the next push, with the dashboard reporting the secret as
    /// gone.</para>
    /// </summary>
    private static void WritePreviewEnv(string prodEnv, string previewEnv, string image, string tag, int hostPort)
    {
        var copiedFromProd = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, value) in EnvFileStore.GetAll(prodEnv))
        {
            if (NotCopiedFromProd.Contains(key))
            {
                continue;
            }

            copiedFromProd.Add(key);
            EnvFileStore.SetValue(previewEnv, key, value);
        }

        EnvFileStore.SetValue(previewEnv, Deployer.ImageVariable, image);
        EnvFileStore.SetValue(previewEnv, Deployer.TagVariable, tag);
        EnvFileStore.SetValue(previewEnv, Deployer.HostPortVariable, hostPort.ToString(CultureInfo.InvariantCulture));

        // Anything left is neither prod's any more nor the preview's own — including
        // an alias a previous version copied, which must go rather than be kept
        // because prod still has it.
        foreach (var (key, _) in EnvFileStore.GetAll(previewEnv))
        {
            if (!copiedFromProd.Contains(key) && !PinnedPerPreview.Contains(key))
            {
                EnvFileStore.RemoveValue(previewEnv, key);
            }
        }
    }

    /// <summary>
    /// The external network production's compose file joins, or null when it names
    /// none — the template then falls back to the shared network, which is where
    /// every project created before app networks existed still is.
    /// </summary>
    private string? ReadProdNetwork(string prodComposeFilePath)
    {
        if (!File.Exists(prodComposeFilePath))
        {
            return null;
        }

        try
        {
            return ReadDeclaredNetwork(File.ReadAllText(prodComposeFilePath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Reported rather than swallowed: the preview falls back to the shared
            // network, and an operator seeing this is the only way to learn that the
            // preview is not on the network its production project is.
            _log?.Invoke(
                $"could not read {prodComposeFilePath} to find the app's network ({exception.Message}); "
                + $"the preview will use {ComposeTemplate.SharedNetwork}");
            return null;
        }
    }

    /// <summary>
    /// The first network declared <c>external: true</c> under a compose file's
    /// top-level <c>networks:</c> — the shape both the dashboard's template and this
    /// one write. Null when the file declares no such network.
    /// </summary>
    private static string? ReadDeclaredNetwork(string composeYaml)
    {
        var insideNetworks = false;
        string? declared = null;
        var declaredIndent = -1;

        foreach (var rawLine in composeYaml.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var content = line.Trim();
            if (content.Length == 0 || content.StartsWith('#'))
            {
                continue;
            }

            var indent = line.Length - line.TrimStart().Length;
            if (indent == 0)
            {
                // Only the unindented key opens the declarations; a service's own
                // `networks:` list is indented and names networks it joins, not ones
                // the file declares. Any other top-level key closes the block.
                insideNetworks = content == NetworksKey;
                declared = null;
                declaredIndent = -1;
                continue;
            }

            if (!insideNetworks)
            {
                continue;
            }

            if (declaredIndent < 0 || indent <= declaredIndent)
            {
                declaredIndent = indent;
                declared = content.EndsWith(':') ? content[..^1].Trim().Trim('"', '\'') : null;
                continue;
            }

            if (declared is { Length: > 0 } && IsExternalTrue(content))
            {
                return declared;
            }
        }

        return null;
    }

    /// <summary>Whether one of a declared network's own lines is <c>external: true</c>.</summary>
    private static bool IsExternalTrue(string content)
    {
        if (!content.StartsWith(ExternalKey, StringComparison.Ordinal))
        {
            return false;
        }

        var value = content[ExternalKey.Length..].Trim().Trim('"', '\'');
        return string.Equals(value, ExternalTrue, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// When the app has a domain in the shared proxy config, adds
    /// <c>pr-&lt;n&gt;.&lt;domain&gt;</c> pointing at the preview container and reloads Caddy.
    /// Best-effort: a missing proxy, or a reload failure, never fails the deploy —
    /// the preview is still reachable on its host port. Returns the preview URL, or
    /// null when no domain was routed.
    /// </summary>
    private async Task<string?> RegisterPreviewDomainAsync(PreviewDeployRequest request, int containerPort, CancellationToken cancellationToken)
    {
        try
        {
            var gateway = Proxy();
            if (!File.Exists(gateway.Store.Path_))
            {
                return null;
            }

            var prodContainer = $"{ComposeProjectName.FromRepository(request.Repo)}-app-1";
            var config = gateway.Store.Load();
            var baseDomains = config.Domains
                .Where(entry => entry.Enabled
                    && !IsPreviewMarker(entry.Target)
                    && string.Equals(entry.TargetContainer, prodContainer, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Domain)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (baseDomains.Count == 0)
            {
                return null;
            }

            var previewContainer = PreviewContainerName(request.Repo, request.PullRequestNumber);
            var marker = PreviewMarker(request.Repo, request.PullRequestNumber);
            var previewDomains = baseDomains.Select(baseDomain => $"pr-{request.PullRequestNumber}.{baseDomain}").ToList();

            // The routes are added inside the store's own lock, so a dashboard edit
            // landing at the same moment cannot lose either change.
            var applied = await Proxy().Update(
                locked =>
                {
                    foreach (var previewDomain in previewDomains)
                    {
                        locked.Domains.RemoveAll(entry =>
                            string.Equals(entry.Domain, previewDomain, StringComparison.OrdinalIgnoreCase));
                        locked.Domains.Add(new DomainEntry
                        {
                            Domain = previewDomain,
                            Target = marker,
                            TargetContainer = previewContainer,
                            TargetPort = containerPort,
                            Enabled = true,
                            CreatedAt = request.Now,
                        });
                    }
                },
                cancellationToken).ConfigureAwait(false);

            if (applied.Failed)
            {
                // Best-effort as before: the preview is still reachable on its host
                // port, so a proxy problem is reported and not made fatal.
                _log?.Invoke($"could not route the preview domain: {applied.Error}");
                return null;
            }

            return previewDomains.Count > 0 ? $"https://{previewDomains[0]}" : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"could not register preview domain: {exception.Message}");
            return null;
        }
    }

    private async Task RemovePreviewDomainAsync(string repo, int pullRequestNumber, CancellationToken cancellationToken)
    {
        // `pinqops preview teardown --pr N` without --repo/--image passes an empty
        // repo deliberately (the CLI warns about it), but PreviewMarker goes through
        // ComposeProjectName.FromRepository, which throws on empty — and
        // ArgumentException is not in the catch filter below, so it escaped a
        // best-effort cleanup AFTER the project had already been brought down and
        // the directory deleted, reporting failure for a teardown that succeeded.
        if (string.IsNullOrWhiteSpace(repo))
        {
            _log?.Invoke("no repository given, so the preview's proxy route cannot be identified; leaving it in place");
            return;
        }

        try
        {
            var gateway = Proxy();
            if (!File.Exists(gateway.Store.Path_))
            {
                return;
            }

            var marker = PreviewMarker(repo, pullRequestNumber);
            if (!gateway.Store.Load().Domains.Exists(entry =>
                string.Equals(entry.Target, marker, StringComparison.OrdinalIgnoreCase)))
            {
                // Nothing of ours is routed, so there is no reason to regenerate and
                // revalidate the whole file.
                return;
            }

            var applied = await gateway.Update(
                locked => locked.Domains.RemoveAll(entry =>
                    string.Equals(entry.Target, marker, StringComparison.OrdinalIgnoreCase)),
                cancellationToken).ConfigureAwait(false);

            if (applied.Failed)
            {
                _log?.Invoke($"could not remove the preview's proxy route: {applied.Error}");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"could not remove preview domain: {exception.Message}");
        }
    }

    /// <summary>
    /// The shared path from domains.json to a Caddy that is serving it.
    ///
    /// <para>This used to be two private methods here — save the config, re-render
    /// the Caddyfile, exec a reload — duplicating the dashboard's. They had already
    /// drifted once: this copy saved without re-rendering, so a preview route was
    /// recorded and advertised but never served, and teardown left a dead route
    /// behind. One implementation is what stops that recurring, and it is where the
    /// validation gate lives, so the CLI gets it too.</para>
    /// </summary>
    private ProxyGateway Proxy() => new(_runner, _proxyDirectory, ProxyImage, log: _log);

    /// <summary>
    /// Only used to validate a candidate Caddyfile, never to run anything — but it
    /// has to be the same build the dashboard installed, because a Caddy without the
    /// rate-limit or DNS modules would refuse a file the running one accepts.
    /// </summary>
    private const string ProxyImage = ProxyPaths.DefaultImage;

    /// <summary>The <see cref="DomainEntry.Target"/> value marking a preview route, so teardown can find its own.</summary>
    public static string PreviewMarker(string repo, int pullRequestNumber) =>
        $"preview:{ComposeProjectName.FromRepository(repo)}:{pullRequestNumber}";

    /// <summary>
    /// Whether a <see cref="DomainEntry.Target"/> names a preview route. Public so
    /// the dashboard shares this predicate instead of duplicating the literal — it
    /// has to skip these entries when it computes route drift, because their
    /// container and port come from the preview lifecycle, not the app config.
    /// </summary>
    public static bool IsPreviewMarker(string? target) =>
        target is not null && target.StartsWith("preview:", StringComparison.Ordinal);

    private async Task<string?> RunStepAsync(IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken cancellationToken)
    {
        _log?.Invoke($"$ {DockerExecutable} {string.Join(' ', arguments)}");
        var result = await _runner.RunAsync(DockerExecutable, arguments, workingDirectory, cancellationToken).ConfigureAwait(false);
        if (result.StandardOutput.Length > 0)
        {
            _log?.Invoke(result.StandardOutput.TrimEnd());
        }

        if (result.Succeeded)
        {
            return null;
        }

        var reason = result.StandardError
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? $"exit {result.ExitCode}";
        _log?.Invoke($"command failed (exit {result.ExitCode}): {reason}");
        return reason;
    }

    private static int? ParsePort(string? raw) =>
        int.TryParse(raw, out var port) && HostPort.IsValid(port) ? port : null;

    private static void RequireValidPr(int pullRequestNumber)
    {
        if (pullRequestNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pullRequestNumber), "Pull request number must be positive.");
        }
    }
}

/// <summary>Everything needed to bring up a PR preview.</summary>
public sealed record PreviewDeployRequest(
    string ProdComposeFilePath,
    string Owner,
    string Repo,
    int PullRequestNumber,
    string Image,
    string Tag,
    DateTimeOffset Now);

/// <summary>Outcome of a preview deploy.</summary>
public sealed record PreviewDeployResult(bool Succeeded, int HostPort, string? Url, string? Error);

/// <summary>A preview that exists on disk.</summary>
public sealed record PreviewInfo(int PullRequestNumber, string ProjectName, string ComposeFilePath, int? HostPort);
