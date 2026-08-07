namespace PinqOps.Proxy;

/// <summary>
/// How the proxy talks to one upstream, beyond where to send the request.
///
/// <para>Separate from the routing fields because these describe the
/// <em>connection</em> rather than the destination, and because the
/// <c>reverse_proxy</c> block is where load balancing will land too — one place
/// that composes its sub-directives beats each feature inventing its own.</para>
/// </summary>
public sealed class UpstreamOptions
{
    /// <summary>
    /// Whether this upstream carries connections that stay open: WebSockets,
    /// server-sent events, long polling, a streaming API.
    ///
    /// <para>Caddy already forwards a WebSocket upgrade without being told to. What
    /// breaks these connections is everything around it — the proxy buffering a
    /// response that is never going to end, and a read timeout firing on a
    /// connection that is idle by design rather than stuck. Both are off here.</para>
    /// </summary>
    public bool LongLivedConnections { get; set; }

    /// <summary>
    /// How requests are spread across this app's replicas. Null means one upstream,
    /// which is every existing route and changes nothing.
    /// </summary>
    public LoadBalancing? Balancing { get; set; }
}

/// <summary>
/// What goes on and inside the <c>reverse_proxy</c> line: the address, when there is
/// a fixed one, and the sub-directives.
/// </summary>
/// <param name="Address">
/// The <c>container:port</c> to forward to, or null when the upstreams are resolved
/// at request time from a <c>dynamic</c> block instead.
/// </param>
public sealed record UpstreamRender(string? Address, IReadOnlyList<string> Directives);

/// <summary>Renders the <c>reverse_proxy</c> line and its block.</summary>
public static class UpstreamRenderer
{
    /// <summary>
    /// One upstream's address and directives.
    ///
    /// <para><b>A bad balancing setting costs balancing, not the route.</b> It is
    /// skipped and reported, and the route falls back to the single container it
    /// always had. Dropping the whole site block because someone typed a policy name
    /// wrong would take the app offline to protect it from being slightly slower.</para>
    /// </summary>
    public static UpstreamRender Render(
        UpstreamOptions? options, string staticUpstream, int containerPort, string label, List<CaddyfileSkip> skipped)
    {
        ArgumentNullException.ThrowIfNull(skipped);

        var directives = new List<string>();
        string? address = staticUpstream;

        if (Balancing(options?.Balancing, containerPort, label, skipped) is { } balancing)
        {
            // The address moves inside the block: `dynamic` is what supplies the
            // upstreams, and Caddy refuses a reverse_proxy that has both.
            address = null;
            directives.AddRange(balancing);
        }

        directives.AddRange(Connection(options));
        return new UpstreamRender(address, directives);
    }

    /// <summary>
    /// The <c>dynamic a</c> and <c>lb_policy</c> lines, or null when this route has
    /// one upstream — which keeps every existing route rendering as it always did.
    /// </summary>
    private static IReadOnlyList<string>? Balancing(
        LoadBalancing? balancing, int containerPort, string label, List<CaddyfileSkip> skipped)
    {
        if (balancing is null)
        {
            return null;
        }

        // The same shape a container name has to satisfy, and for the same reason:
        // this is emitted into the Caddyfile, and whitespace or a brace would open a
        // directive nobody wrote.
        if (!CaddyfileGenerator.IsEmittableName(balancing.Alias))
        {
            skipped.Add(new CaddyfileSkip(
                label,
                $"'{balancing.Alias}' is not a network alias pinqops will emit, so this route still "
                + "forwards to a single container"));
            return null;
        }

        if (!LoadBalancingPolicies.IsKnown(balancing.Policy))
        {
            skipped.Add(new CaddyfileSkip(
                label,
                $"'{balancing.Policy}' is not a balancing policy pinqops knows, so this route still "
                + "forwards to a single container"));
            return null;
        }

        // Clamped rather than skipped: a refresh interval someone typed too many
        // zeroes into is still a request to balance, and falling back to one
        // container over it would be a far bigger change than the one they made.
        var refresh = Math.Clamp(
            balancing.RefreshSeconds, LoadBalancing.MinimumRefreshSeconds, LoadBalancing.MaximumRefreshSeconds);

        var policy = balancing.Policy == LoadBalancingPolicies.StickyCookie
            ? $"lb_policy cookie {LoadBalancingPolicies.CookieName}"
            : $"lb_policy {balancing.Policy}";

        return
        [
            // The port every replica listens on inside the network — the same for
            // all of them. It is the host port that could not be shared, and the
            // proxy owns that now.
            $"dynamic a {balancing.Alias} {containerPort} {{",
            $"    refresh {refresh}s",
            "}",
            policy,
        ];
    }

    /// <summary>
    /// The lines for the connection itself, or empty when the defaults are right.
    /// </summary>
    private static IReadOnlyList<string> Connection(UpstreamOptions? options)
    {
        if (options is null || !options.LongLivedConnections)
        {
            return [];
        }

        return
        [
            // Send each write straight through instead of collecting it. Without
            // this an event stream arrives in batches, or not until it ends.
            "flush_interval -1",
            "transport http {",
            //  A connection that says nothing for a while is the normal state of a
            //  WebSocket, not a stuck one. Zero means "do not decide that for me".
            "    read_timeout 0",
            "    write_timeout 0",
            "}",
        ];
    }
}
