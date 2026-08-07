using PinqOps.Deploy;
using Xunit;

namespace PinqOps.Tests.Deploy;

/// <summary>
/// The clamps that keep a hand-edited <c>deploy.json</c> from making a deploy hang,
/// spin, or request something that is not a path. Every one of these values reaches
/// a loop or a URL.
/// </summary>
public class ReadinessSettingsTests
{
    [Fact]
    public void TheProbeIsOffUntilSomebodyTurnsItOn()
    {
        // Deliberate: a gate that can fail a deploy must not switch on underneath
        // an existing app during an upgrade.
        Assert.False(new ReadinessSettings().Enabled);
        Assert.False(new DeploySettings().Readiness.Enabled);
    }

    [Fact]
    public void TheDefaultsAreUsableWithoutEditingAnything()
    {
        var settings = new ReadinessSettings { Enabled = true }.Normalized();

        Assert.Equal("/", settings.Path);
        Assert.Equal(200, settings.ExpectedStatusFrom);
        Assert.Equal(399, settings.ExpectedStatusTo);
        Assert.Equal(2, settings.ConsecutiveSuccesses);
    }

    [Theory]
    [InlineData("healthz", "/healthz")]
    [InlineData("  /healthz  ", "/healthz")]
    [InlineData("/", "/")]
    public void APathIsGivenItsLeadingSlash(string written, string expected) =>
        Assert.Equal(expected, new ReadinessSettings { Path = written }.Normalized().Path);

    [Theory]
    // Scheme-relative: Uri would resolve this to a different server entirely.
    [InlineData("//evil.example.com/")]
    [InlineData("/with space")]
    [InlineData("/with\ttab")]
    [InlineData("/back\\slash")]
    [InlineData("")]
    [InlineData("   ")]
    public void APathThatIsNotOneFallsBackToTheRoot(string written) =>
        Assert.Equal("/", new ReadinessSettings { Path = written }.Normalized().Path);

    /// <summary>
    /// The dashboard rejects what this refuses rather than cleaning it up. A path
    /// quietly rewritten to "/" would leave the operator believing the probe checks
    /// /healthz when it does not, and a probe checking the wrong thing is worse than
    /// no probe at all.
    /// </summary>
    [Theory]
    [InlineData("healthz", "/healthz")]
    [InlineData("/healthz?ready=1", "/healthz?ready=1")]
    [InlineData("", "/")]
    [InlineData(null, "/")]
    public void AUsablePathIsAcceptedAndGivenItsSlash(string? written, string expected)
    {
        Assert.True(ReadinessSettings.TryNormalizePath(written, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("//evil.example.com/")]
    [InlineData("http://evil.example.com/")]
    [InlineData("/with space")]
    [InlineData("/back\\slash")]
    public void SomethingThatIsNotAPathIsRefusedRatherThanRewritten(string written) =>
        Assert.False(ReadinessSettings.TryNormalizePath(written, out _));

    [Fact]
    public void ANullPathIsNeverStored()
    {
        // A hand-edited `"path": null` deserializes straight over the initializer.
        Assert.Equal("/", new ReadinessSettings { Path = null! }.Path);
    }

    [Fact]
    public void ANullReadinessBlockIsNeverStored() =>
        Assert.NotNull(new DeploySettings { Readiness = null! }.Readiness);

    [Fact]
    public void AnInvertedStatusRangeIsWidenedRatherThanLeftAcceptingNothing()
    {
        // 300-200 accepts no status at all, so every deploy would fail the probe
        // with nothing to indicate the range is why.
        var settings = new ReadinessSettings { ExpectedStatusFrom = 300, ExpectedStatusTo = 200 }.Normalized();

        Assert.Equal(300, settings.ExpectedStatusFrom);
        Assert.Equal(300, settings.ExpectedStatusTo);
    }

    [Fact]
    public void StatusCodesAreHeldToThreeDigits()
    {
        var settings = new ReadinessSettings { ExpectedStatusFrom = -5, ExpectedStatusTo = 9000 }.Normalized();

        Assert.Equal(100, settings.ExpectedStatusFrom);
        Assert.Equal(599, settings.ExpectedStatusTo);
    }

    [Fact]
    public void AZeroIntervalCannotSpinTheProbe()
    {
        var settings = new ReadinessSettings { IntervalSeconds = 0, TimeoutSeconds = 0 }.Normalized();

        Assert.Equal(1, settings.IntervalSeconds);
        Assert.Equal(1, settings.TimeoutSeconds);
    }

    [Fact]
    public void ATimeoutCannotOutlastAnyPlausibleDeploy() =>
        Assert.Equal(600, new ReadinessSettings { TimeoutSeconds = 86_400 }.Normalized().TimeoutSeconds);

    [Fact]
    public void ConsecutiveSuccessesAreAtLeastOne() =>
        Assert.Equal(1, new ReadinessSettings { ConsecutiveSuccesses = 0 }.Normalized().ConsecutiveSuccesses);

    [Fact]
    public void NormalizingDoesNotMutateTheStoredSettings()
    {
        var stored = new ReadinessSettings { Path = "healthz", IntervalSeconds = 0 };

        stored.Normalized();

        Assert.Equal("healthz", stored.Path);
        Assert.Equal(0, stored.IntervalSeconds);
    }
}

public class DeploySettingsStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _composePath;

    public DeploySettingsStoreTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-deploy-settings-tests").FullName;
        _composePath = Path.Combine(_directory, "docker-compose.yml");
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void AMissingFileIsTheDefaults()
    {
        var settings = new DeploySettingsStore(_composePath).Load();

        Assert.False(settings.Readiness.Enabled);
    }

    [Fact]
    public void ItLivesBesideTheOtherProjectState()
    {
        var store = new DeploySettingsStore(_composePath);

        Assert.Equal(Path.Combine(_directory, ".pinqops", "deploy.json"), store.Path_);
    }

    [Fact]
    public void WhatIsSavedIsWhatIsLoaded()
    {
        var store = new DeploySettingsStore(_composePath);
        store.Save(new DeploySettings
        {
            Readiness = new ReadinessSettings { Enabled = true, Path = "/healthz", ConsecutiveSuccesses = 3 },
        });

        var loaded = new DeploySettingsStore(_composePath).Load().Readiness;

        Assert.True(loaded.Enabled);
        Assert.Equal("/healthz", loaded.Path);
        Assert.Equal(3, loaded.ConsecutiveSuccesses);
    }

    [Fact]
    public void ACorruptFileIsTheDefaultsRatherThanACrash()
    {
        // Every deploy on the server reads this file. A bad edit must cost the
        // probe, never the deploy.
        Directory.CreateDirectory(Path.Combine(_directory, ".pinqops"));
        File.WriteAllText(Path.Combine(_directory, ".pinqops", "deploy.json"), "{ not json");

        Assert.False(new DeploySettingsStore(_composePath).Load().Readiness.Enabled);
    }
}
