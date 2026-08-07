namespace PinqOps.Proxy;

/// <summary>What one apply did.</summary>
public sealed record ProxyApplyResult(
    bool Written,
    bool Reloaded,
    IReadOnlyList<CaddyfileSkip> Skipped,
    string? Error)
{
    /// <summary>True when the Caddyfile on disk does not describe the stored config.</summary>
    public bool Failed => Error is not null;
}

/// <summary>
/// The one path from <c>domains.json</c> to a Caddy that is serving it: regenerate,
/// validate, install, reload, and roll back if the reload is refused.
///
/// <para>The dashboard and the runner CLI both change proxy routes — the dashboard
/// when someone adds a domain, the CLI when a pull request opens or closes — and
/// until now each had its own copy of "save the config, re-render the Caddyfile,
/// exec a reload". They had already drifted once: the CLI's copy saved without
/// re-rendering, so a preview route was recorded and advertised but never served.
/// One implementation is what stops that recurring, and it is where the validation
/// gate belongs so that all three callers get it rather than whichever one
/// remembered.</para>
///
/// <para>Lives in Core, and talks to docker through <see cref="IProcessRunner"/>
/// rather than the dashboard's DockerService, because the CLI has to be able to use
/// it on the server with nothing else running.</para>
/// </summary>
public sealed class ProxyGateway
{
    private const string DockerExecutable = "docker";

    public const string DefaultContainerName = "pinqops-proxy";

    private readonly IProcessRunner _runner;
    private readonly string _proxyDirectory;
    private readonly string _containerName;
    private readonly CaddyfileValidator _validator;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _applyGate = new(1, 1);

    public ProxyGateway(
        IProcessRunner runner,
        string proxyDirectory,
        string image,
        string containerName = DefaultContainerName,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrWhiteSpace(proxyDirectory);
        _runner = runner;
        _proxyDirectory = proxyDirectory;
        _containerName = containerName;
        _validator = new CaddyfileValidator(runner, proxyDirectory, image);
        _log = log;
        Store = new DomainConfigStore(proxyDirectory);
    }

    public DomainConfigStore Store { get; }

    /// <summary>
    /// Applies a change to the stored config and then brings Caddy in line with it,
    /// under the store's own lock so two concurrent edits cannot lose one another.
    /// </summary>
    public async Task<ProxyApplyResult> Update(
        Action<DomainConfig> mutate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        Store.Update<object?>(config =>
        {
            mutate(config);
            return null;
        });

        // Deliberately outside the store lock: a slow reload must not block another
        // edit, and the config is already durable by this point.
        return await Apply(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Renders the stored config, refuses to install it if Caddy would not accept
    /// it, writes it, and hot-reloads a running proxy.
    ///
    /// <para>When the proxy is not running the file is still written — the routes
    /// are then already correct the moment it starts, which is the documented
    /// contract every caller relies on.</para>
    /// </summary>
    public async Task<ProxyApplyResult> Apply(CancellationToken cancellationToken = default)
    {
        await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ApplyCore(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _applyGate.Release();
        }
    }

    private async Task<ProxyApplyResult> ApplyCore(CancellationToken cancellationToken)
    {
        var config = Store.Load();
        var render = CaddyfileGenerator.GenerateWithDiagnostics(config);
        foreach (var skip in render.Skipped)
        {
            // Skips used to be silent. A skipped entry is a route the dashboard
            // lists as enabled and Caddy never serves, so it has to be said out loud
            // somewhere the operator can see it.
            _log?.Invoke($"proxy route left out — {skip}");
        }

        // A site-less Caddyfile (header-only → {}, or global options with no site
        // blocks) tears down every listener and cancels in-flight ACME. That includes
        // the WaitingDns case: every domain is ProxyDeferred, so the render has no
        // sites even though domains.json is full — HasEmittableRoutes is false and
        // the old header-only check would not fire. Refuse whenever the disk still
        // retains enabled domains (deferred count) or emittable routes that were
        // all skipped. Intentional "remove the last route" leaves neither.
        if (!config.HasEmittableRoutes()
            || CaddyfileGenerator.IsEffectivelyEmpty(render.Caddyfile))
        {
            config = Store.Load();
            render = CaddyfileGenerator.GenerateWithDiagnostics(config);
            var siteLess = !config.HasEmittableRoutes()
                || CaddyfileGenerator.IsEffectivelyEmpty(render.Caddyfile);
            if (siteLess
                && (Store.DiskHasRetainedRoutes() || Store.DiskHasEmittableRoutes()))
            {
                _log?.Invoke("refusing site-less Caddyfile — disk still has retained routes");
                var lastGood = _validator.TryReadLastGood();
                var reloaded = false;
                if (lastGood is not null && !CaddyfileGenerator.IsEffectivelyEmpty(lastGood))
                {
                    var healed = _validator.RestoreLastGood();
                    if (healed && await IsProxyRunning(cancellationToken).ConfigureAwait(false))
                    {
                        var heal = await _runner.RunAsync(
                            DockerExecutable,
                            ["exec", _containerName, "caddy", "reload", "--config", "/etc/caddy/Caddyfile"],
                            workingDirectory: null,
                            cancellationToken).ConfigureAwait(false);
                        reloaded = heal.ExitCode == 0;
                    }
                }

                return new ProxyApplyResult(
                    false,
                    reloaded,
                    render.Skipped,
                    "Refused to apply a site-less Caddyfile while domains.json still has routes.");
            }
        }

        var validation = await _validator.Validate(render.Caddyfile, cancellationToken).ConfigureAwait(false);
        if (!validation.Valid)
        {
            // The live file is untouched, so the proxy keeps serving what it has.
            return new ProxyApplyResult(false, false, render.Skipped, validation.Error);
        }

        Directory.CreateDirectory(_proxyDirectory);
        // Preserve the inode: the proxy bind-mounts this single file read-only, and
        // an atomic rename would leave the container reading the previous bytes
        // forever while the host path looked updated. First create is still atomic.
        SecureFile.WriteAllTextPreservingInode(
            ProxyPaths.CaddyfilePath(_proxyDirectory), render.Caddyfile, ownerOnly: false);

        if (!await IsProxyRunning(cancellationToken).ConfigureAwait(false))
        {
            // Nothing has accepted this file yet, so it is not a rollback target.
            return new ProxyApplyResult(true, false, render.Skipped, null);
        }

        var reload = await _runner.RunAsync(
            DockerExecutable,
            ["exec", _containerName, "caddy", "reload", "--config", "/etc/caddy/Caddyfile"],
            workingDirectory: null,
            cancellationToken).ConfigureAwait(false);

        if (reload.ExitCode == 0)
        {
            _validator.RememberGood(render.Caddyfile);
            return new ProxyApplyResult(true, true, render.Skipped, null);
        }

        // Validation passed but Caddy refused it anyway — a directive that parses
        // and cannot be applied, a certificate it cannot obtain. The file on disk
        // now disagrees with what the proxy is running, and the proxy restarts
        // unless stopped, so put back the last file it accepted rather than leaving
        // a restart loop armed for whenever it next restarts.
        var restored = _validator.RestoreLastGood();
        var detail = reload.StandardError.Length > 0 ? reload.StandardError.Trim() : reload.StandardOutput.Trim();
        _log?.Invoke($"caddy reload failed: {detail}");

        return new ProxyApplyResult(
            true,
            false,
            render.Skipped,
            restored
                ? $"Caddy refused the new configuration, so the previous one was restored: {detail}"
                : $"Caddy refused the new configuration: {detail}");
    }

    private async Task<bool> IsProxyRunning(CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            DockerExecutable,
            ["inspect", "-f", "{{.State.Running}}", "--", _containerName],
            workingDirectory: null,
            cancellationToken).ConfigureAwait(false);

        return result.ExitCode == 0
            && result.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
