using PinqOps.Proxy;
using Xunit;

namespace PinqOps.Tests.Proxy;

public class UpstreamOptionsTests
{
    private static string DomainCaddyfile(UpstreamOptions? upstream) =>
        CaddyfileGenerator.Generate(new DomainConfig
        {
            Domains =
            [
                new DomainEntry
                {
                    Domain = "app.example.com",
                    TargetContainer = "app-1",
                    TargetPort = 3000,
                    Enabled = true,
                    Upstream = upstream,
                    Security = new SecurityHeaders { Enabled = false },
                },
            ],
        });

    /// <summary>
    /// A route with the defaults renders as the single line it always did — the
    /// block form appears only when there is something to put in it.
    /// </summary>
    [Fact]
    public void WithTheDefaultsTheProxyLineStaysASingleLine()
    {
        Assert.Contains(
            "app.example.com {\n    reverse_proxy app-1:3000\n}\n",
            DomainCaddyfile(null),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnUpstreamThatIsNotLongLivedRendersNothingExtra()
    {
        Assert.DoesNotContain(
            "flush_interval",
            DomainCaddyfile(new UpstreamOptions { LongLivedConnections = false }),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Caddy forwards a WebSocket upgrade without being told to. What breaks these
    /// connections is the proxy buffering a response that never ends and a read
    /// timeout firing on a connection that is idle by design.
    /// </summary>
    [Fact]
    public void LongLivedConnectionsDisableBufferingAndTheReadTimeout()
    {
        var caddyfile = DomainCaddyfile(new UpstreamOptions { LongLivedConnections = true });

        Assert.Contains("reverse_proxy app-1:3000 {", caddyfile, StringComparison.Ordinal);
        Assert.Contains("flush_interval -1", caddyfile, StringComparison.Ordinal);
        Assert.Contains("read_timeout 0", caddyfile, StringComparison.Ordinal);
        Assert.Contains("write_timeout 0", caddyfile, StringComparison.Ordinal);
    }

    /// <summary>
    /// An app reached by <c>server:8080</c> has exactly the same streaming problem
    /// as one reached by name, so the setting lives on a port entry too.
    /// </summary>
    [Fact]
    public void APortEntryCanCarryLongLivedConnectionsToo()
    {
        var caddyfile = CaddyfileGenerator.Generate(new DomainConfig
        {
            Ports =
            [
                new PortEntry
                {
                    HostPort = 8080,
                    TargetContainer = "app-1",
                    TargetPort = 3000,
                    Enabled = true,
                    Upstream = new UpstreamOptions { LongLivedConnections = true },
                },
            ],
        });

        Assert.Contains(":8080 {", caddyfile, StringComparison.Ordinal);
        Assert.Contains("flush_interval -1", caddyfile, StringComparison.Ordinal);
    }

    /// <summary>The block form composes with the rest of the site block rather than
    /// replacing any of it.</summary>
    [Fact]
    public void ItComposesWithHeadersAndARateLimit()
    {
        var caddyfile = CaddyfileGenerator.Generate(new DomainConfig
        {
            Domains =
            [
                new DomainEntry
                {
                    Domain = "app.example.com",
                    TargetContainer = "app-1",
                    TargetPort = 3000,
                    Enabled = true,
                    Upstream = new UpstreamOptions { LongLivedConnections = true },
                    RateLimit = new RateLimit { Enabled = true, BurstRequests = 20 },
                },
            ],
        });

        Assert.Contains("rate_limit {", caddyfile, StringComparison.Ordinal);
        Assert.Contains("X-Content-Type-Options nosniff", caddyfile, StringComparison.Ordinal);
        Assert.Contains("flush_interval -1", caddyfile, StringComparison.Ordinal);
    }

    /// <summary>Nothing here is caller-supplied text, so there is nothing to
    /// validate — the setting is a switch and the directives are fixed.</summary>
    [Fact]
    public void TheDirectivesAreFixedAndCarryNoCallerInput()
    {
        var render = UpstreamRenderer.Render(
            new UpstreamOptions { LongLivedConnections = true }, "web:80", 80, "example.com", []);

        Assert.Equal("web:80", render.Address);
        Assert.Equal(
            ["flush_interval -1", "transport http {", "    read_timeout 0", "    write_timeout 0", "}"],
            render.Directives);
    }
}
