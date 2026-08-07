using PinqOps.Proxy;
using PinqOps.Tests.Fakes;
using Xunit;

namespace PinqOps.Tests.Proxy;

/// <summary>
/// The one path from domains.json to a Caddy that is serving it. The dashboard and
/// the runner CLI both go through this, which is the point: they used to have a
/// copy each, and the copies had already drifted.
/// </summary>
public class ProxyGatewayTests : IDisposable
{
    private const string Image = "ghcr.io/pinqponq/pinqops-caddy:2";

    private readonly string _directory = Directory.CreateTempSubdirectory("pinqops-gateway-").FullName;
    private readonly List<string> _log = [];

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Answers the three commands the gateway issues. Written as a dispatch rather
    /// than a queue because the gateway skips the reload entirely when the proxy is
    /// not running, so the call order is not fixed.
    /// </summary>
    private sealed class Docker
    {
        public bool ValidationPasses { get; init; } = true;

        public bool ProxyRunning { get; init; }

        public bool ReloadSucceeds { get; init; } = true;

        public FakeProcessRunner Runner() => new((_, arguments) =>
        {
            if (arguments.Contains("validate"))
            {
                return ValidationPasses
                    ? new ProcessResult(0, string.Empty, string.Empty)
                    : new ProcessResult(1, string.Empty, "Error: line 3: unrecognized directive: nonsense");
            }

            if (arguments.Contains("inspect"))
            {
                return new ProcessResult(0, ProxyRunning ? "true\n" : "false\n", string.Empty);
            }

            if (arguments.Contains("reload"))
            {
                return ReloadSucceeds
                    ? new ProcessResult(0, string.Empty, string.Empty)
                    : new ProcessResult(1, string.Empty, "loading new config: cannot obtain certificate");
            }

            return new ProcessResult(0, string.Empty, string.Empty);
        });
    }

    private ProxyGateway Gateway(Docker docker, out FakeProcessRunner runner)
    {
        runner = docker.Runner();
        return new ProxyGateway(runner, _directory, Image, log: _log.Add);
    }

    private static DomainEntry Entry(string domain, string container = "app-1", int port = 3000) =>
        new() { Domain = domain, TargetContainer = container, TargetPort = port, Enabled = true };

    private string Caddyfile() => File.ReadAllText(ProxyPaths.CaddyfilePath(_directory));

    // ---- the happy path -----------------------------------------------------

    [Fact]
    public async Task UpdateStoresTheChangeAndWritesTheCaddyfile()
    {
        var gateway = Gateway(new Docker(), out _);

        var applied = await gateway.Update(config => config.Domains.Add(Entry("app.example.com")));

        Assert.False(applied.Failed);
        Assert.True(applied.Written);
        Assert.Contains("app.example.com {", Caddyfile(), StringComparison.Ordinal);
        Assert.Single(gateway.Store.Load().Domains);
    }

    /// <summary>
    /// The documented contract: with no proxy running the file is still written, so
    /// the routes are already correct the moment it starts.
    /// </summary>
    [Fact]
    public async Task WithNoProxyRunningTheFileIsWrittenAndNothingIsReloaded()
    {
        var gateway = Gateway(new Docker { ProxyRunning = false }, out var runner);

        var applied = await gateway.Update(config => config.Domains.Add(Entry("app.example.com")));

        Assert.True(applied.Written);
        Assert.False(applied.Reloaded);
        Assert.DoesNotContain(runner.Invocations, invocation => invocation.Arguments.Contains("reload"));
    }

    [Fact]
    public async Task ARunningProxyIsReloaded()
    {
        var gateway = Gateway(new Docker { ProxyRunning = true }, out var runner);

        var applied = await gateway.Update(config => config.Domains.Add(Entry("app.example.com")));

        Assert.True(applied.Reloaded);
        var reload = Assert.Single(runner.Invocations, invocation => invocation.Arguments.Contains("reload"));
        Assert.Equal(
            ["exec", "pinqops-proxy", "caddy", "reload", "--config", "/etc/caddy/Caddyfile"],
            reload.Arguments);
    }

    // ---- the validation gate ------------------------------------------------

    /// <summary>
    /// The proxy runs with --restart unless-stopped, so a Caddyfile it cannot parse
    /// is not one failed reload — it is a restart loop that takes every domain down.
    /// This is the gate that stops one being installed.
    /// </summary>
    [Fact]
    public async Task AConfigCaddyWouldRejectIsNeverWritten()
    {
        File.WriteAllText(ProxyPaths.CaddyfilePath(_directory), "# the live one\n");
        var gateway = Gateway(new Docker { ValidationPasses = false }, out _);

        var applied = await gateway.Update(config => config.Domains.Add(Entry("app.example.com")));

        Assert.True(applied.Failed);
        Assert.False(applied.Written);
        Assert.Equal("# the live one\n", Caddyfile());
    }

    /// <summary>The change is still stored — it is the file that was held back, not
    /// the edit, so the operator's next apply can retry it.</summary>
    [Fact]
    public async Task ARejectedConfigStillPersistsTheEdit()
    {
        var gateway = Gateway(new Docker { ValidationPasses = false }, out _);

        await gateway.Update(config => config.Domains.Add(Entry("app.example.com")));

        Assert.Single(gateway.Store.Load().Domains);
    }

    [Fact]
    public async Task ANeverInstalledProxyIsNotReloadedAndNotValidatedTwice()
    {
        var gateway = Gateway(new Docker { ProxyRunning = false }, out var runner);

        await gateway.Apply();

        Assert.Single(runner.Invocations, invocation => invocation.Arguments.Contains("validate"));
    }

    // ---- rollback -----------------------------------------------------------

    /// <summary>
    /// A config that parses but that Caddy then refuses to apply — a certificate it
    /// cannot obtain — leaves the file on disk disagreeing with what the proxy is
    /// running. Because the proxy restarts unless stopped, that file is a restart
    /// loop armed for whenever it next restarts, so the last accepted one goes back.
    /// </summary>
    [Fact]
    public async Task AReloadCaddyRefusesRollsBackToTheLastAcceptedFile()
    {
        var working = Gateway(new Docker { ProxyRunning = true }, out _);
        await working.Update(config => config.Domains.Add(Entry("first.example.com")));
        var accepted = Caddyfile();

        var failing = Gateway(new Docker { ProxyRunning = true, ReloadSucceeds = false }, out _);
        var applied = await failing.Update(config => config.Domains.Add(Entry("second.example.com")));

        Assert.True(applied.Failed);
        Assert.Contains("restored", applied.Error, StringComparison.Ordinal);
        Assert.Equal(accepted, Caddyfile());
    }

    [Fact]
    public async Task AFirstEverReloadFailureSaysThereWasNothingToRestore()
    {
        var gateway = Gateway(new Docker { ProxyRunning = true, ReloadSucceeds = false }, out _);

        var applied = await gateway.Update(config => config.Domains.Add(Entry("app.example.com")));

        Assert.True(applied.Failed);
        Assert.DoesNotContain("restored", applied.Error, StringComparison.Ordinal);
    }

    // ---- diagnostics --------------------------------------------------------

    /// <summary>
    /// A skipped entry is a route the dashboard lists as enabled and Caddy never
    /// serves. It used to be silent; now it comes back with the result and reaches
    /// the log.
    /// </summary>
    [Fact]
    public async Task SkippedRoutesAreReportedAndLogged()
    {
        var gateway = Gateway(new Docker(), out _);

        var applied = await gateway.Update(config => config.Domains.Add(Entry("app.example.com", "not a container")));

        Assert.Single(applied.Skipped);
        Assert.Contains(_log, line => line.Contains("left out", StringComparison.Ordinal));
    }

    // ---- empty apply guard --------------------------------------------------

    /// <summary>
    /// A header-only Caddyfile becomes <c>{}</c> inside Caddy. Reloading that
    /// cancels ACME and drops every site. When domains.json still lists emittable
    /// routes but the render is empty (every entry skipped), refuse — even when
    /// last-good is missing.
    /// </summary>
    [Fact]
    public async Task AnEmptyRenderOverPopulatedDiskIsRefusedWithoutLastGood()
    {
        var gateway = Gateway(new Docker { ProxyRunning = false }, out _);
        File.WriteAllText(ProxyPaths.CaddyfilePath(_directory), "# live\nexample.com {\n}\n");

        gateway.Store.Save(new DomainConfig
        {
            Domains = [Entry("app.example.com", "not a container")],
        });
        var applied = await gateway.Apply();

        Assert.True(applied.Failed);
        Assert.False(applied.Written);
        Assert.Contains("site-less Caddyfile", applied.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("# live\nexample.com {\n}\n", Caddyfile());
        Assert.Contains(_log, line => line.Contains("refusing site-less", StringComparison.Ordinal));
    }

    /// <summary>
    /// WaitingDns leaves every new Cloudflare domain deferred, so the render has
    /// no site blocks. Reloading that (global options alone) still shuts HTTP down
    /// — the same outage as <c>{}</c>. Refuse and leave the live file alone.
    /// </summary>
    [Fact]
    public async Task ADeferredOnlyRenderIsRefusedWhileTheDomainIsRetained()
    {
        var gateway = Gateway(new Docker { ProxyRunning = false }, out _);
        const string live = "# live\nother.example.com {\n    reverse_proxy app:80\n}\n";
        File.WriteAllText(ProxyPaths.CaddyfilePath(_directory), live);

        var deferred = Entry("pending.example.com");
        deferred.ProxyDeferred = true;
        gateway.Store.Save(new DomainConfig
        {
            AcmeEmail = "ops@example.com",
            Domains = [deferred],
        });
        var applied = await gateway.Apply();

        Assert.True(applied.Failed);
        Assert.False(applied.Written);
        Assert.Equal(live, Caddyfile());
        Assert.Contains("site-less", applied.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnEmptyRenderOverPopulatedDiskRestoresLastGoodWhenPresent()
    {
        var gateway = Gateway(new Docker { ProxyRunning = true }, out var runner);
        await gateway.Update(config =>
        {
            config.AcmeEmail = "ops@example.com";
            config.Domains.Add(Entry("app.example.com"));
        });
        var accepted = Caddyfile();

        gateway.Store.Save(new DomainConfig
        {
            Domains = [Entry("app.example.com", "not a container")],
        });
        var applied = await gateway.Apply();

        Assert.True(applied.Failed);
        Assert.False(applied.Written);
        Assert.Equal(accepted, Caddyfile());
        Assert.Contains(runner.Invocations, invocation => invocation.Arguments.Contains("reload"));
    }

    /// <summary>
    /// Removing the last route for real must still apply — otherwise unenroll
    /// and "delete every domain" cannot clear the proxy.
    /// </summary>
    [Fact]
    public async Task AnIntentionalEmptyStoreIsStillApplied()
    {
        var gateway = Gateway(new Docker { ProxyRunning = true }, out _);
        await gateway.Update(config =>
        {
            config.AcmeEmail = "ops@example.com";
            config.Domains.Add(Entry("app.example.com"));
        });

        var applied = await gateway.Update(config =>
        {
            config.AcmeEmail = string.Empty;
            config.Domains.Clear();
        });

        Assert.False(applied.Failed);
        Assert.True(applied.Written);
        Assert.True(CaddyfileGenerator.IsEffectivelyEmpty(Caddyfile()));
    }

    [Fact]
    public async Task AFirstEmptyApplyWithNoLastGoodIsStillWritten()
    {
        var gateway = Gateway(new Docker { ProxyRunning = false }, out _);

        var applied = await gateway.Apply();

        Assert.False(applied.Failed);
        Assert.True(applied.Written);
        Assert.True(CaddyfileGenerator.IsEffectivelyEmpty(Caddyfile()));
    }
}
