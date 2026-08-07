namespace PinqOps.Proxy;

/// <summary>
/// How the proxy spreads requests across an app's replicas.
///
/// <para><b>The replica set is learned, not counted.</b> The upstream is a docker
/// network alias every replica answers on, resolved by docker's own DNS and
/// re-resolved on a timer. Writing <c>N</c> upstreams into the Caddyfile instead
/// would mean regenerating it on every scale, every crash and every restart — and
/// any regeneration that was missed leaves the proxy sending traffic to a container
/// that no longer exists, or ignoring one that does. Neither is visible from the
/// dashboard, and both look like an application fault.</para>
/// </summary>
public sealed class LoadBalancing
{
    private string _alias = string.Empty;
    private string _policy = LoadBalancingPolicies.RoundRobin;

    /// <summary>
    /// The docker network alias every replica of this app answers on — the compose
    /// project's <c>PINQOPS_ALIAS</c>. Deploy-managed, because two projects sharing
    /// an alias share traffic.
    /// </summary>
    public string Alias { get => _alias; set => _alias = value ?? string.Empty; }

    /// <summary>One of <see cref="LoadBalancingPolicies"/>.</summary>
    public string Policy { get => _policy; set => _policy = value ?? string.Empty; }

    /// <summary>
    /// How often docker's DNS is asked again. Short enough that a container that
    /// died stops receiving requests within seconds, long enough that a busy proxy
    /// is not resolving on every request.
    /// </summary>
    public int RefreshSeconds { get; set; } = DefaultRefreshSeconds;

    public const int DefaultRefreshSeconds = 5;

    public const int MinimumRefreshSeconds = 1;

    public const int MaximumRefreshSeconds = 300;
}

/// <summary>The balancing policies pinqops will emit, and nothing else.</summary>
public static class LoadBalancingPolicies
{
    public const string RoundRobin = "round_robin";

    /// <summary>Sends each request to whichever replica is handling the fewest.</summary>
    public const string LeastConnections = "least_conn";

    /// <summary>Same client address, same replica. Sticky without a cookie.</summary>
    public const string ClientAddress = "ip_hash";

    /// <summary>
    /// Sticky by cookie: the first response sets one, and every later request from
    /// that browser goes back to the same replica.
    /// </summary>
    public const string StickyCookie = "cookie";

    /// <summary>
    /// The sticky cookie's name. Fixed rather than configurable: it is emitted into
    /// the Caddyfile and set on the visitor's browser, and there is nothing to gain
    /// from letting it be typed in — only a way to write something that is not a
    /// cookie name.
    /// </summary>
    public const string CookieName = "pinqops_upstream";

    public static readonly string[] All = [RoundRobin, LeastConnections, ClientAddress, StickyCookie];

    public static bool IsKnown(string? policy) =>
        policy is not null && Array.IndexOf(All, policy) >= 0;
}
