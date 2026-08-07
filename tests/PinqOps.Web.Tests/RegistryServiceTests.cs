using Microsoft.Extensions.Logging.Abstractions;
using PinqOps.Registries;
using PinqOps.Secrets;
using PinqOps.Web;
using PinqOps.Web.Tests.Fakes;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// Signing the daemon in to a private registry. The one thing that matters here is
/// where the password goes: an argument list is visible to every user on the host
/// through <c>ps</c>, so it has to travel on stdin or not at all.
/// </summary>
public class RegistryServiceTests : IDisposable
{
    private const string Password = "correct-horse-battery-staple";

    private readonly string _directory;
    private readonly RegistryStore _registries;
    private readonly SecretStore _secrets;

    public RegistryServiceTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-registry-service-tests").FullName;
        _registries = new RegistryStore(Path.Combine(_directory, "registries.json"));
        _secrets = new SecretStore(Path.Combine(_directory, "secrets.json"));
        _secrets.Set(SecretScopes.Global, "GHCR_TOKEN", Password, null, "tester", DateTimeOffset.UtcNow);
        _registries.Save([new Registry
        {
            Id = "a1",
            Host = "ghcr.io",
            Username = "deploy",
            SecretName = "GHCR_TOKEN",
        }]);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private RegistryService Service(FakeProcessRunner runner) =>
        new(runner, _registries, _secrets, NullLogger<RegistryService>.Instance);

    [Fact]
    public async Task ThePasswordTravelsOnStandardInputAndNeverAsAnArgument()
    {
        var runner = new FakeProcessRunner();

        Assert.Null(await Service(runner).LoginAsync("a1"));

        var login = Assert.Single(runner.Invocations);
        Assert.Equal(Password, login.StandardInput);
        Assert.DoesNotContain(Password, login.Arguments);
        Assert.Contains("--password-stdin", login.Arguments);
    }

    [Fact]
    public async Task TheUsernameIsAnArgumentBecauseItIsANameRatherThanASecret()
    {
        var runner = new FakeProcessRunner();

        await Service(runner).LoginAsync("a1");

        Assert.Equal(
            ["login", "--username", "deploy", "--password-stdin", "--", "ghcr.io"],
            Assert.Single(runner.Invocations).Arguments);
    }

    [Fact]
    public async Task ASuccessfulLoginIsRecordedSoTheListCanSayWhen()
    {
        await Service(new FakeProcessRunner()).LoginAsync("a1");

        Assert.NotNull(Assert.Single(_registries.Load()).LastLoginAt);
    }

    /// <summary>
    /// Docker's own message, because it is the one that says whether the password is
    /// wrong or the host is unreachable — and it never echoes what was sent.
    /// </summary>
    [Fact]
    public async Task AFailedLoginReportsDockersReasonAndRecordsNothing()
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(1, string.Empty, "unauthorized: incorrect username or password"));

        var failure = await Service(runner).LoginAsync("a1");

        Assert.Equal("unauthorized: incorrect username or password", failure);
        Assert.Null(Assert.Single(_registries.Load()).LastLoginAt);
    }

    [Fact]
    public async Task AVaultEntryThatIsGoneIsReportedRatherThanThrown()
    {
        _registries.Save([new Registry { Id = "a1", Host = "ghcr.io", Username = "deploy", SecretName = "MISSING" }]);
        var runner = new FakeProcessRunner();

        var failure = await Service(runner).LoginAsync("a1");

        Assert.Contains("no entry called 'MISSING'", failure);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task AnUnknownRegistryIsNotSomethingToLogInTo() =>
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => Service(new FakeProcessRunner()).LoginAsync("nope"));

    [Fact]
    public async Task DockerHubIsAddressedTheWayDockerStoresIt()
    {
        _registries.Save([new Registry { Id = "a1", Host = "docker.io", Username = "deploy", SecretName = "GHCR_TOKEN" }]);
        var runner = new FakeProcessRunner();

        await Service(runner).LoginAsync("a1");

        Assert.Equal(Registry.DockerHub, Assert.Single(runner.Invocations).Arguments[^1]);
    }

    /// <summary>
    /// Otherwise "removed" means only "removed from this list" while the daemon
    /// keeps pulling with the same credential.
    /// </summary>
    [Fact]
    public async Task SigningOutIsWhatMakesARemovalMeanSomething()
    {
        var runner = new FakeProcessRunner();

        await Service(runner).LogoutAsync("ghcr.io");

        Assert.Equal(["logout", "--", "ghcr.io"], Assert.Single(runner.Invocations).Arguments);
    }

    [Fact]
    public async Task ADaemonThatWasNeverSignedInIsNotAFailureWorthThrowingOver()
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(1, string.Empty, "Not logged in to ghcr.io"));

        await Service(runner).LogoutAsync("ghcr.io");
    }
}
