using PinqOps.Proxy;
using Xunit;

namespace PinqOps.Tests.Proxy;

/// <summary>
/// What the proxy emits when an app runs more than one copy. The replica set is
/// learned from docker's DNS rather than written out, so the file does not have to
/// be regenerated on every scale, crash or restart — a regeneration that was missed
/// would leave the proxy sending traffic to a container that is gone.
/// </summary>
public class LoadBalancingTests
{
    private static DomainConfig WithBalancing(LoadBalancing? balancing, int targetPort = 8080) => new()
    {
        Ports =
        [
            new PortEntry
            {
                HostPort = 8080,
                Target = "acme",
                TargetContainer = "acme-app-1",
                TargetPort = targetPort,
                Upstream = balancing is null ? null : new UpstreamOptions { Balancing = balancing },
            },
        ],
    };

    private static LoadBalancing Balancing(
        string alias = "acme", string policy = LoadBalancingPolicies.RoundRobin, int refreshSeconds = 5) =>
        new() { Alias = alias, Policy = policy, RefreshSeconds = refreshSeconds };

    [Fact]
    public void ARouteWithNoBalancingRendersExactlyAsItAlwaysDid()
    {
        var caddyfile = CaddyfileGenerator.Generate(WithBalancing(null));

        Assert.Contains("    reverse_proxy acme-app-1:8080\n", caddyfile, StringComparison.Ordinal);
        Assert.DoesNotContain("dynamic", caddyfile, StringComparison.Ordinal);
    }

    /// <summary>
    /// The address moves inside the block: Caddy refuses a <c>reverse_proxy</c> that
    /// has both a static upstream and a dynamic one.
    /// </summary>
    [Fact]
    public void BalancingReplacesTheStaticUpstreamRatherThanJoiningIt()
    {
        var caddyfile = CaddyfileGenerator.Generate(WithBalancing(Balancing()));

        Assert.Contains("    reverse_proxy {\n", caddyfile, StringComparison.Ordinal);
        Assert.DoesNotContain("reverse_proxy acme-app-1", caddyfile, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAliasAndTheContainerPortAreWhatIsResolved()
    {
        var caddyfile = CaddyfileGenerator.Generate(WithBalancing(Balancing(), targetPort: 3000));

        Assert.Contains("        dynamic a acme 3000 {\n", caddyfile, StringComparison.Ordinal);
        Assert.Contains("            refresh 5s\n", caddyfile, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LoadBalancingPolicies.RoundRobin, "lb_policy round_robin")]
    [InlineData(LoadBalancingPolicies.LeastConnections, "lb_policy least_conn")]
    [InlineData(LoadBalancingPolicies.ClientAddress, "lb_policy ip_hash")]
    public void EveryKnownPolicyIsEmittedAsItself(string policy, string expected) =>
        Assert.Contains(expected, CaddyfileGenerator.Generate(WithBalancing(Balancing(policy: policy))), StringComparison.Ordinal);

    /// <summary>
    /// The cookie's name is fixed. It is emitted into the Caddyfile and set on the
    /// visitor's browser; letting it be typed in buys nothing and adds a way to
    /// write something that is not a cookie name.
    /// </summary>
    [Fact]
    public void StickySessionsUseOneFixedCookieName()
    {
        var caddyfile = CaddyfileGenerator.Generate(
            WithBalancing(Balancing(policy: LoadBalancingPolicies.StickyCookie)));

        Assert.Contains("lb_policy cookie pinqops_upstream", caddyfile, StringComparison.Ordinal);
    }

    [Fact]
    public void BalancingComposesWithTheLongLivedConnectionProfile()
    {
        var config = WithBalancing(Balancing());
        config.Ports[0].Upstream!.LongLivedConnections = true;

        var caddyfile = CaddyfileGenerator.Generate(config);

        Assert.Contains("dynamic a acme 8080 {", caddyfile, StringComparison.Ordinal);
        Assert.Contains("flush_interval -1", caddyfile, StringComparison.Ordinal);
    }

    // ---- a bad setting costs balancing, never the route ----------------------

    [Theory]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("acme}\n:9999 {")]
    [InlineData("-leading-hyphen")]
    public void AnAliasThatCannotBeEmittedFallsBackToTheSingleContainer(string alias)
    {
        var render = CaddyfileGenerator.GenerateWithDiagnostics(WithBalancing(Balancing(alias: alias)));

        // The route survives. Taking the app offline to protect it from being
        // slightly slower would be the worse trade by a wide margin.
        Assert.Contains("reverse_proxy acme-app-1:8080", render.Caddyfile, StringComparison.Ordinal);
        Assert.DoesNotContain("dynamic", render.Caddyfile, StringComparison.Ordinal);
        Assert.Contains(render.Skipped, skip => skip.Reason.Contains("still forwards to a single container"));
    }

    [Theory]
    [InlineData("random")]
    [InlineData("")]
    [InlineData("round robin")]
    public void APolicyPinqopsDoesNotKnowFallsBackToTheSingleContainer(string policy)
    {
        var render = CaddyfileGenerator.GenerateWithDiagnostics(WithBalancing(Balancing(policy: policy)));

        Assert.Contains("reverse_proxy acme-app-1:8080", render.Caddyfile, StringComparison.Ordinal);
        Assert.Contains(render.Skipped, skip => skip.Reason.Contains("balancing policy"));
    }

    /// <summary>
    /// Clamped rather than skipped: too many zeroes in a refresh interval is still a
    /// request to balance, and dropping to one container over it would be a far
    /// bigger change than the one that was made.
    /// </summary>
    [Theory]
    [InlineData(0, "refresh 1s")]
    [InlineData(-30, "refresh 1s")]
    [InlineData(86_400, "refresh 300s")]
    public void AnImpossibleRefreshIntervalIsClampedNotDropped(int written, string expected)
    {
        var render = CaddyfileGenerator.GenerateWithDiagnostics(
            WithBalancing(Balancing(refreshSeconds: written)));

        Assert.Contains(expected, render.Caddyfile, StringComparison.Ordinal);
        Assert.Empty(render.Skipped);
    }

    [Fact]
    public void ADomainRouteBalancesTheSameWayAPortRouteDoes()
    {
        var caddyfile = CaddyfileGenerator.Generate(new DomainConfig
        {
            Domains =
            [
                new DomainEntry
                {
                    Domain = "acme.example.com",
                    Target = "acme",
                    TargetContainer = "acme-app-1",
                    TargetPort = 8080,
                    Upstream = new UpstreamOptions { Balancing = Balancing() },
                },
            ],
        });

        Assert.Contains("dynamic a acme 8080 {", caddyfile, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyThePoliciesPinqopsEmitsAreKnown()
    {
        Assert.Equal(
            ["round_robin", "least_conn", "ip_hash", "cookie"],
            LoadBalancingPolicies.All);
        Assert.False(LoadBalancingPolicies.IsKnown(null));
        Assert.False(LoadBalancingPolicies.IsKnown("weighted_round_robin"));
    }
}
