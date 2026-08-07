using PinqOps.Proxy;

namespace PinqOps.Deploy;

/// <summary>One project the reconciler should check.</summary>
public sealed record ColoredApp(string Target, string ComposeFilePath, string Alias);

/// <summary>
/// Makes the proxy's routes describe the colour each project actually recorded as
/// serving.
///
/// <para><b>What it is for.</b> A coloured deploy writes the active colour only
/// after the proxy has accepted the new routes, so the two agree at every point a
/// crash could land — except one: the process can die between the proxy accepting
/// the file and the file being written. Then the running proxy is on the new colour
/// and the record says the old one, and the next proxy restart would read the record
/// and go back. This closes that by making the record win, once, at startup.</para>
///
/// <para><b>It never changes which colour is active.</b> Only the routes are
/// brought into line with the record. Deciding that the other colour "looks more
/// alive" and moving to it would silently finish a cutover that a failed deploy
/// deliberately abandoned — which is how a version that never passed its health
/// check ends up serving traffic.</para>
/// </summary>
public sealed class ColorReconciler
{
    private readonly ProxyGateway _proxy;
    private readonly Action<string>? _log;

    public ColorReconciler(ProxyGateway proxy, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        _proxy = proxy;
        _log = log;
    }

    /// <summary>
    /// Returns how many routes had to be corrected. Zero is the normal answer and
    /// costs one config read.
    /// </summary>
    public async Task<int> ReconcileAsync(
        IReadOnlyList<ColoredApp> apps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apps);

        var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            var settings = new DeploySettingsStore(app.ComposeFilePath).Load();
            if (settings.BlueGreen)
            {
                wanted[app.Target] = DeployColors.Alias(app.Alias, settings.ActiveColor);
            }
        }

        if (wanted.Count == 0)
        {
            return 0;
        }

        var corrections = Corrections(_proxy.Store.Load(), wanted);
        if (corrections.Count == 0)
        {
            return 0;
        }

        foreach (var (target, was, shouldBe) in corrections)
        {
            _log?.Invoke($"{target} was routed to {was ?? "no replica set"}; its recorded colour is {shouldBe}");
        }

        // One apply for every correction: the file is regenerated whole, so writing
        // it once per app would be the same work repeated and would leave more
        // windows where the proxy holds a half-corrected set of routes.
        var applied = await _proxy.Update(
            config =>
            {
                foreach (var (target, alias) in wanted)
                {
                    foreach (var upstream in RoutesFor(config, target))
                    {
                        if (upstream.Balancing is null)
                        {
                            upstream.Balancing = new LoadBalancing { Alias = alias };
                        }
                        else
                        {
                            upstream.Balancing.Alias = alias;
                        }
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);

        if (applied.Failed)
        {
            // Said out loud rather than retried: the proxy is still serving whatever
            // it had, and a loop here would hammer a validation that is not going to
            // start passing on its own.
            _log?.Invoke($"could not put the proxy back on the recorded colours: {applied.Error}");
            return 0;
        }

        return corrections.Count;
    }

    /// <summary>The routes whose alias does not match the recorded colour.</summary>
    private static List<(string Target, string? Was, string ShouldBe)> Corrections(
        DomainConfig config, Dictionary<string, string> wanted)
    {
        var corrections = new List<(string, string?, string)>();
        foreach (var (target, alias) in wanted)
        {
            foreach (var upstream in RoutesFor(config, target))
            {
                if (!string.Equals(upstream.Balancing?.Alias, alias, StringComparison.Ordinal))
                {
                    corrections.Add((target, upstream.Balancing?.Alias, alias));
                }
            }
        }

        return corrections;
    }

    private static List<UpstreamOptions> RoutesFor(DomainConfig config, string target)
    {
        var routes = new List<UpstreamOptions>();

        foreach (var entry in config.Ports)
        {
            if (string.Equals(entry.Target, target, StringComparison.OrdinalIgnoreCase))
            {
                routes.Add(entry.Upstream ??= new UpstreamOptions());
            }
        }

        foreach (var entry in config.Domains)
        {
            if (string.Equals(entry.Target, target, StringComparison.OrdinalIgnoreCase))
            {
                routes.Add(entry.Upstream ??= new UpstreamOptions());
            }
        }

        return routes;
    }
}
