using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

public class ManagedEnvironmentTests
{
    private static ManagedEnvironment Ssh() => new()
    {
        Id = "prod",
        Name = "Production",
        Transport = ManagedEnvironment.TransportSsh,
        Host = "10.0.0.5",
        User = "deploy",
        Port = 22,
    };

    [Fact]
    public void LocalIsValidWithNoConnectionDetails()
    {
        var local = ManagedEnvironment.Local();

        local.Validate();
        Assert.True(local.IsLocal);
    }

    [Fact]
    public void AWellFormedSshEnvironmentValidates() => Ssh().Validate();

    // The id becomes an SSH host alias and a file name, so the character set is
    // deliberately narrow.
    [Theory]
    [InlineData("")]
    [InlineData("-leading")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("../escape")]
    [InlineData("Upper")]
    public void RejectsAnUnsafeId(string id)
    {
        var environment = Ssh();
        environment.Id = id;

        Assert.Throws<ArgumentException>(environment.Validate);
    }

    [Fact]
    public void RejectsAnEmptyName()
    {
        var environment = Ssh();
        environment.Name = "  ";

        Assert.Throws<ArgumentException>(environment.Validate);
    }

    // A half-specified host would be stored and then fail confusingly on every
    // request instead of at the point someone could fix it.
    [Theory]
    [InlineData(null, "deploy", 22)]
    [InlineData("10.0.0.5", null, 22)]
    [InlineData("10.0.0.5", "deploy", 0)]
    [InlineData("10.0.0.5", "deploy", 70000)]
    [InlineData("host with space", "deploy", 22)]
    [InlineData("10.0.0.5", "bad user", 22)]
    public void RejectsAnIncompleteSshTarget(string? host, string? user, int port)
    {
        var environment = Ssh();
        environment.Host = host;
        environment.User = user;
        environment.Port = port;

        Assert.Throws<ArgumentException>(environment.Validate);
    }

    // "A valid SSH host (name or IP address) is required" used to refuse every
    // IPv6 address, so an IPv6-only server could not be registered at all and the
    // message named the very thing the operator had just typed.
    [Theory]
    [InlineData("2001:db8::1")]
    [InlineData("2001:0db8:0000:0000:0000:0000:0000:0001")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("2001:DB8::1")]
    public void AcceptsAnIpv6Host(string host)
    {
        var environment = Ssh();
        environment.Host = host;

        environment.Validate();
        Assert.True(SshTarget.IsValidHost(host));
    }

    // The host is written into ssh_config as a HostName, where a '%' scope suffix
    // reads as an unknown token and breaks the whole generated block — not just
    // this one host.
    [Theory]
    [InlineData("fe80::1%eth0")]
    [InlineData("fe80::1%1")]
    [InlineData("[2001:db8::1]")]
    [InlineData("2001:db8::1 extra")]
    [InlineData("2001:db8::1\nHost evil")]
    [InlineData("not:an:address")]
    public void RejectsAnythingThatOnlyLooksLikeAnIpv6Host(string host)
    {
        Assert.False(SshTarget.IsValidHost(host));
    }

    [Fact]
    public void RejectsAnUnknownTransport()
    {
        var environment = Ssh();
        environment.Transport = "carrier-pigeon";

        Assert.Throws<ArgumentException>(environment.Validate);
    }
}

public class EnvironmentRegistryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pinqops-env-").FullName;

    private string ConfigPath => Path.Combine(_dir, "ui.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // Everything resolves an environment, so one always has to exist.
    [Fact]
    public void AFreshConfigAlwaysHasTheLocalEnvironment()
    {
        var environment = Assert.Single(new UiConfigStore(ConfigPath).Current.Environments);

        Assert.Equal(ManagedEnvironment.LocalId, environment.Id);
        Assert.True(environment.IsLocal);
    }

    [Fact]
    public void AnExistingConfigGainsTheLocalEnvironment()
    {
        File.WriteAllText(ConfigPath, """{ "Users": [], "Apps": [] }""");

        Assert.Contains(
            new UiConfigStore(ConfigPath).Current.Environments,
            environment => environment.Id == ManagedEnvironment.LocalId);
    }

    // Deleting it by hand must not leave a config that addresses nothing.
    [Fact]
    public void ARemovedLocalEnvironmentComesBack()
    {
        File.WriteAllText(ConfigPath, """
            { "Users": [], "Environments": [ { "Id": "prod", "Name": "prod", "Transport": "ssh" } ] }
            """);

        var environments = new UiConfigStore(ConfigPath).Current.Environments;

        Assert.Contains(environments, environment => environment.Id == ManagedEnvironment.LocalId);
        Assert.Contains(environments, environment => environment.Id == "prod");
    }

    [Fact]
    public void MigrationIsIdempotent()
    {
        var store = new UiConfigStore(ConfigPath);
        store.Update(config => config.Pat = "ghp_x");

        Assert.Single(new UiConfigStore(ConfigPath).Current.Environments);
    }

    // An SSH key is shell access to that host, so it gets the same treatment as
    // the GitHub token.
    [Fact]
    public void PrivateKeysAreNotWrittenInPlaintext()
    {
        var store = new UiConfigStore(ConfigPath);

        store.Update(config => config.Environments.Add(new ManagedEnvironment
        {
            Id = "prod",
            Name = "prod",
            Transport = ManagedEnvironment.TransportSsh,
            Host = "10.0.0.5",
            User = "deploy",
            PrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nverysecretkeymaterial\n",
        }));

        Assert.DoesNotContain("verysecretkeymaterial", File.ReadAllText(ConfigPath));

        var reloaded = new UiConfigStore(ConfigPath).Current.Environments.Single(e => e.Id == "prod");
        Assert.Contains("verysecretkeymaterial", reloaded.PrivateKey);
    }
}
