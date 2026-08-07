using PinqOps.Proxy;
using PinqOps.Secrets;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// The environment the proxy container is created with.
///
/// <para>Everything here reads the vault, and every way it can fail has to fail as
/// an <em>answer</em>: the install resolves this before it removes the running
/// container precisely so a bad DNS secret refuses the install instead of leaving
/// the server with no proxy at all. An exception type that escapes this method
/// unrecognised defeats that, because the caller cannot tell a misconfiguration
/// from a crash.</para>
/// </summary>
public class ProxyEnvironmentTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("pinqops-proxy-env-").FullName;
    private readonly SecretStore _secrets;

    public ProxyEnvironmentTests() => _secrets = new SecretStore(Path.Combine(_directory, "secrets.json"));

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static DomainConfig WithDnsSecret(string secretName) => new()
    {
        Dns = new DnsChallenge
        {
            Enabled = true,
            Provider = DnsProviders.Cloudflare,
            SecretName = secretName,
        },
    };

    [Fact]
    public void NoDnsChallengeNeedsNoEnvironment() =>
        Assert.Empty(ProxyService.ProxyEnvironment(new DomainConfig(), _secrets));

    [Fact]
    public void AStoredSecretBecomesTheProvidersToken()
    {
        _secrets.Set(SecretScopes.Global, "CF_TOKEN", "cf-secret-value", null, "boss", DateTimeOffset.UtcNow);

        var environment = ProxyService.ProxyEnvironment(WithDnsSecret("CF_TOKEN"), _secrets);

        Assert.Equal("cf-secret-value", environment[DnsProviders.TokenVariable]);
    }

    [Fact]
    public void AMissingSecretIsRefusedWithSomethingToActOn()
    {
        var failure = Assert.Throws<InvalidOperationException>(
            () => ProxyService.ProxyEnvironment(WithDnsSecret("ABSENT_TOKEN"), _secrets));

        Assert.Contains("ABSENT_TOKEN", failure.Message, StringComparison.Ordinal);
        Assert.Contains("does not exist", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A dash is what an operator types, and the usability check the config carries
    /// only asks that a name was entered at all — so an unusable name reaches the
    /// vault, which refuses it with an <see cref="ArgumentException"/>. Left
    /// unrecognised that escaped as an unhandled failure, and did so after the
    /// running proxy had already been removed.
    /// </summary>
    [Theory]
    [InlineData("cf-token")]
    [InlineData("cf token")]
    [InlineData("1token")]
    public void AnUnusableSecretNameIsRefusedTheSameWay(string secretName)
    {
        var failure = Assert.Throws<InvalidOperationException>(
            () => ProxyService.ProxyEnvironment(WithDnsSecret(secretName), _secrets));

        Assert.Contains(secretName, failure.Message, StringComparison.Ordinal);
        Assert.Contains("not a usable secret name", failure.Message, StringComparison.Ordinal);
        // The original is kept rather than flattened into a message.
        Assert.IsType<ArgumentException>(failure.InnerException);
    }

    /// <summary>
    /// A challenge that is switched off, or names no secret at all, is not a
    /// misconfiguration — it is the ordinary install, and it must not be refused.
    /// </summary>
    [Theory]
    [InlineData(false, "CF_TOKEN")]
    [InlineData(true, "")]
    [InlineData(true, "   ")]
    public void AnIncompleteChallengeIsSimplyNotUsed(bool enabled, string secretName)
    {
        var config = new DomainConfig
        {
            Dns = new DnsChallenge
            {
                Enabled = enabled,
                Provider = DnsProviders.Cloudflare,
                SecretName = secretName,
            },
        };

        Assert.Empty(ProxyService.ProxyEnvironment(config, _secrets));
    }
}
