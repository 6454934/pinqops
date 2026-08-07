using PinqOps.Registries;
using Xunit;

namespace PinqOps.Tests.Registries;

public class RegistryValidatorTests
{
    private static Registry Entry(
        string host = "registry.example.com",
        string username = "deploy",
        string secretName = "REGISTRY_TOKEN") =>
        new() { Host = host, Username = username, SecretName = secretName };

    [Fact]
    public void AnOrdinaryEntryIsAccepted() => Assert.Null(RegistryValidator.Validate(Entry()));

    [Theory]
    [InlineData("ghcr.io")]
    [InlineData("registry.example.com:5000")]
    [InlineData("localhost:5000")]
    [InlineData("my-registry.internal")]
    public void AHostnameWithAnOptionalPortIsAHost(string host) =>
        Assert.Null(RegistryValidator.Validate(Entry(host: host)));

    /// <summary>
    /// Docker takes a host, not a URL. A scheme here is the mistake that produces
    /// "https:/​/registry.example.com" in the daemon's auth file and a pull that
    /// never finds the credential it just stored.
    /// </summary>
    [Theory]
    [InlineData("https://registry.example.com")]
    [InlineData("registry.example.com/v2/")]
    [InlineData("registry example.com")]
    [InlineData("-registry.example.com")]
    [InlineData("registry.example.com:notaport")]
    [InlineData("registry.example.com:99999")]
    public void SomethingThatIsNotAHostIsRefused(string host) =>
        Assert.Contains("is not a registry host", RegistryValidator.Validate(Entry(host: host)));

    /// <summary>
    /// Docker Hub's auth key is a URL rather than a hostname, and no operator would
    /// think to type it. Normalising it is the difference between a login that works
    /// and one that fails with a DNS error.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("docker.io")]
    [InlineData("DOCKER.IO")]
    [InlineData("index.docker.io")]
    public void DockerHubIsWrittenTheWayDockerStoresIt(string written) =>
        Assert.Equal(Registry.DockerHub, RegistryValidator.Normalize(written));

    [Fact]
    public void DockerHubIsAcceptedEvenThoughItIsAUrl() =>
        Assert.Null(RegistryValidator.Validate(Entry(host: "docker.io")));

    [Fact]
    public void ATrailingSlashIsNoise() =>
        Assert.Equal("registry.example.com", RegistryValidator.Normalize("registry.example.com/"));

    [Fact]
    public void AnEntryWithoutAnAccountIsRefused() =>
        Assert.Equal("A username is required.", RegistryValidator.Validate(Entry(username: "  ")));

    /// <summary>
    /// The password is not stored here at all — only the name of the vault entry
    /// holding it — so an entry that names no vault entry has no password.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not a name")]
    public void AnEntryWithoutAVaultEntryIsRefused(string secretName) =>
        Assert.Contains("vault entry", RegistryValidator.Validate(Entry(secretName: secretName)));

    [Fact]
    public void ANullFieldIsNeverStored()
    {
        // A hand-edited `"host": null` deserializes straight over the initializer.
        Assert.Equal(string.Empty, new Registry { Host = null! }.Host);
        Assert.Equal(string.Empty, new Registry { Username = null! }.Username);
        Assert.Equal(string.Empty, new Registry { SecretName = null! }.SecretName);
    }
}

public class RegistryStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly RegistryStore _store;

    public RegistryStoreTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-registry-store-tests").FullName;
        _store = new RegistryStore(Path.Combine(_directory, "registries.json"));
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void AMissingFileIsNoRegistries() => Assert.Empty(_store.Load());

    [Fact]
    public void ACorruptFileIsNoRegistriesRatherThanACrash()
    {
        File.WriteAllText(_store.Path_, "{ not json");

        Assert.Empty(_store.Load());
    }

    /// <summary>
    /// The file is read to render a list, and a list is exactly what gets logged,
    /// diffed and pasted into a support message.
    /// </summary>
    [Fact]
    public void WhatIsWrittenHoldsTheSecretsNameAndNotItsValue()
    {
        _store.Save([new Registry { Id = "a1", Host = "ghcr.io", Username = "deploy", SecretName = "GHCR_TOKEN" }]);

        var written = File.ReadAllText(_store.Path_);

        Assert.Contains("GHCR_TOKEN", written, StringComparison.Ordinal);
        Assert.DoesNotContain("password", written, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EachIdIsItsOwn() => Assert.NotEqual(RegistryStore.NewId(), RegistryStore.NewId());
}
