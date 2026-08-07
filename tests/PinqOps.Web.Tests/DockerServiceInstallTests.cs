using PinqOps;
using PinqOps.Proxy;
using PinqOps.Web.Tests.Fakes;
using Xunit;

namespace PinqOps.Web.Tests;

public class DockerServiceInstallTests
{
    [Fact]
    public async Task InstallAppAsync_UsesResolvedEnv_NotTheRawSpec()
    {
        var runner = new FakeProcessRunner();
        var docker = new DockerService(runner);
        var spec = AppCatalog.Find("postgres")!;
        var (env, _) = AppCatalog.ResolveEnv(spec, _ => "s3cret");

        await docker.InstallAppAsync(spec, hostPorts: null, env);

        var run = runner.Invocations.Single(invocation => invocation.Arguments.Contains("run"));
        Assert.Contains("POSTGRES_PASSWORD=s3cret", run.Arguments);
        Assert.DoesNotContain(run.Arguments, argument => argument.Contains("{{password", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallAppAsync_WithoutOverride_UsesSpecEnv()
    {
        var runner = new FakeProcessRunner();
        var docker = new DockerService(runner);
        var spec = AppCatalog.Find("elasticsearch")!;

        await docker.InstallAppAsync(spec, hostPorts: null);

        var run = runner.Invocations.Single(invocation => invocation.Arguments.Contains("run"));
        Assert.Contains("discovery.type=single-node", run.Arguments);
    }

    // A bare `-p host:container` binds 0.0.0.0, which put every catalog service —
    // several with no authentication at all — on the internet the moment it was
    // installed on an unfirewalled host.
    [Fact]
    public async Task InstallAppAsync_BindsPublishedPortsToLoopbackByDefault()
    {
        var runner = new FakeProcessRunner();
        var docker = new DockerService(runner);

        await docker.InstallAppAsync(AppCatalog.Find("redis")!, hostPorts: null);

        var run = runner.Invocations.Single(invocation => invocation.Arguments.Contains("run"));
        Assert.Contains("127.0.0.1:6379:6379", run.Arguments);
        Assert.DoesNotContain("6379:6379", run.Arguments);
    }

    [Fact]
    public async Task InstallAppAsync_PublishesOnAllInterfacesOnlyWhenAsked()
    {
        var runner = new FakeProcessRunner();
        var docker = new DockerService(runner);

        await docker.InstallAppAsync(AppCatalog.Find("redis")!, hostPorts: null, publishPublicly: true);

        var run = runner.Invocations.Single(invocation => invocation.Arguments.Contains("run"));
        Assert.Contains("6379:6379", run.Arguments);
        Assert.DoesNotContain("127.0.0.1:6379:6379", run.Arguments);
    }

    // Images that take their password as a flag need the command substituted the
    // same way the env is, or they would start unauthenticated.
    [Fact]
    public async Task InstallAppAsync_UsesTheResolvedCommand()
    {
        var runner = new FakeProcessRunner();
        var docker = new DockerService(runner);
        var spec = AppCatalog.Find("redis")!;

        await docker.InstallAppAsync(
            spec, hostPorts: null, envOverride: null, cmdOverride: AppCatalog.ResolveCmd(spec, _ => "s3cret"));

        var run = runner.Invocations.Single(invocation => invocation.Arguments.Contains("run"));
        Assert.Contains("s3cret", run.Arguments);
        Assert.DoesNotContain(run.Arguments, argument => argument.Contains("{{password", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallProxyAsync_PublishesTheWebPortsAndMountsTheCaddyfile()
    {
        var runner = new FakeProcessRunner();
        var docker = new DockerService(runner);

        await docker.InstallProxyAsync("pinqops-proxy", "caddy:2-alpine", "/opt/pinqops/proxy/Caddyfile");

        var run = runner.Invocations.Single(invocation => invocation.Arguments.Contains("run"));
        Assert.Equal("docker", run.FileName);
        Assert.Contains("80:80", run.Arguments);
        Assert.Contains("443:443", run.Arguments);
        Assert.Contains("443:443/udp", run.Arguments);
        Assert.Contains("/opt/pinqops/proxy/Caddyfile:/etc/caddy/Caddyfile:ro", run.Arguments);
        Assert.Contains("pinqops-proxy-data:/data", run.Arguments);
        Assert.Contains("caddy:2-alpine", run.Arguments);
    }

    /// <summary>
    /// The proxy mounted the Caddyfile as a single file and nothing else of its
    /// directory, so the access log Caddy was told to write landed in the
    /// container's own writable layer: the traffic summary reads
    /// <c>/opt/pinqops/proxy/access.log</c> on the host, found nothing, and reported
    /// zero requests after any amount of real traffic — while every recreate of the
    /// container destroyed the log that did exist.
    /// </summary>
    [Fact]
    public async Task InstallProxyAsync_MountsTheProxyDirectorySoTheAccessLogReachesTheHost()
    {
        var runner = new FakeProcessRunner();
        var docker = new DockerService(runner);

        await docker.InstallProxyAsync("pinqops-proxy", "caddy:2-alpine", "/opt/pinqops/proxy/Caddyfile");

        var run = runner.Invocations.Single(invocation => invocation.Arguments.Contains("run"));

        // Discrete argv, so the order matters: each mount has to sit immediately
        // after its own -v, and the image has to stay last.
        Assert.Equal(
            [
                "-v", "/opt/pinqops/proxy/Caddyfile:/etc/caddy/Caddyfile:ro",
                "-v", $"/opt/pinqops/proxy:{ProxyPaths.LogDirectory}",
                "-v", "pinqops-proxy-data:/data",
                "-v", "pinqops-proxy-config:/config",
                "caddy:2-alpine",
            ],
            run.Arguments.ToArray()[^9..]);

        // Writable: the whole point is that Caddy appends to it.
        Assert.DoesNotContain(run.Arguments, argument => argument.EndsWith($"{ProxyPaths.LogDirectory}:ro", StringComparison.Ordinal));

        // And the path Caddy is told to write to has to be inside what was mounted,
        // or the mount buys nothing.
        Assert.StartsWith($"{ProxyPaths.LogDirectory}/", ProxyPaths.AccessLogPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecAsync_RunsTheArgvInsideTheContainer()
    {
        var runner = new FakeProcessRunner();
        var docker = new DockerService(runner);

        await docker.ExecAsync("pinqops-proxy", "caddy", "reload", "--config", "/etc/caddy/Caddyfile");

        var exec = runner.Invocations.Single(invocation => invocation.Arguments.Contains("exec"));
        Assert.Equal(["exec", "--", "pinqops-proxy", "caddy", "reload", "--config", "/etc/caddy/Caddyfile"], exec.Arguments);
    }

    [Fact]
    public async Task ExecAsync_RejectsAFlagLikeContainerName()
    {
        var docker = new DockerService(new FakeProcessRunner());

        await Assert.ThrowsAsync<ArgumentException>(() => docker.ExecAsync("--rm", "sh"));
    }

    [Fact]
    public async Task BackupVolumeAsync_TarsTheVolumeReadOnlyIntoTheBackupDir()
    {
        var runner = new FakeProcessRunner();
        var docker = new DockerService(runner);

        await docker.BackupVolumeAsync("pinqops-postgres-data", "/opt/pinqops/backups/db", "20260722-030405.tgz");

        var run = runner.Invocations.Single(invocation => invocation.Arguments.Contains("run"));
        Assert.Contains("pinqops-postgres-data:/src:ro", run.Arguments);
        Assert.Contains("/opt/pinqops/backups/db:/dst", run.Arguments);
        // Written to .part and renamed on success, so a failed tar cannot leave a
        // truncated archive that looks like an ordinary snapshot. The name is a
        // positional argument, not text in the script.
        Assert.Contains(run.Arguments, a => a.Contains("tar czf \"/dst/$1.part\"") && a.Contains("mv \"/dst/$1.part\" \"/dst/$1\""));
        Assert.Equal("20260722-030405.tgz", run.Arguments[^1]);
    }

    [Fact]
    public async Task RestoreVolumeAsync_VerifiesTheArchiveBeforeClearingTheVolume()
    {
        var runner = new FakeProcessRunner();
        var docker = new DockerService(runner);

        await docker.RestoreVolumeAsync("vol", "/opt/pinqops/backups/vol", "20260722-030405.tgz");

        var run = runner.Invocations.Single(invocation => invocation.Arguments.Contains("run"));
        Assert.Contains("vol:/dst", run.Arguments);

        // The listing has to come first and be chained with && — the destructive
        // delete must be unreachable for an archive that cannot even be read.
        var script = run.Arguments.Single(a => a.Contains("tar xzf"));
        var verify = script.IndexOf("tar tzf", StringComparison.Ordinal);
        var delete = script.IndexOf("find /dst -mindepth 1 -delete", StringComparison.Ordinal);
        Assert.True(verify >= 0 && delete > verify, script);
        Assert.Contains("&&", script[verify..delete]);
    }

    [Fact]
    public async Task CopyFromContainerAsync_UsesDockerCp()
    {
        var runner = new FakeProcessRunner();
        var docker = new DockerService(runner);

        await docker.CopyFromContainerAsync("pinqops-redis", "/data/dump.rdb", "/opt/pinqops/backups/redis/x.rdb");

        var cp = runner.Invocations.Single(invocation => invocation.Arguments.Contains("cp"));
        Assert.Equal(["cp", "pinqops-redis:/data/dump.rdb", "/opt/pinqops/backups/redis/x.rdb"], cp.Arguments);
    }
}
