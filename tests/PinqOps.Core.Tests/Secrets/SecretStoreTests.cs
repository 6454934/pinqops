using PinqOps.Secrets;
using Xunit;

namespace PinqOps.Tests;

public class SecretStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory;
    private readonly SecretStore _store;

    public SecretStoreTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-secret-tests").FullName;
        _store = new SecretStore(Path.Combine(_directory, "secrets.json"));
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    // ---- names and scopes ---------------------------------------------------

    [Theory]
    [InlineData("DATABASE_URL")]
    [InlineData("a")]
    [InlineData("_LEADING_UNDERSCORE")]
    [InlineData("Mixed_Case9")]
    public void ValidNamesAreAccepted(string name)
    {
        Assert.Equal(name, SecretName.Normalize(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("9LEADING_DIGIT")]
    [InlineData("HAS-HYPHEN")]
    [InlineData("HAS SPACE")]
    [InlineData("HAS.DOT")]
    [InlineData("HAS$SIGN")]
    public void NamesEnvFileStoreWouldRejectAreRefused(string name)
    {
        Assert.Throws<ArgumentException>(() => SecretName.Normalize(name));
    }

    /// <summary>
    /// A secret named PINQOPS_TAG would be overwritten by the next deploy, and one
    /// named PINQOPS_IMAGE would repoint the app at another image. Reserving the
    /// whole prefix means adding a fifth managed variable cannot re-open this.
    /// </summary>
    [Theory]
    [InlineData("PINQOPS_TAG")]
    [InlineData("PINQOPS_IMAGE")]
    [InlineData("PINQOPS_HOST_PORT")]
    [InlineData("pinqops_anything")]
    public void TheReservedPrefixIsRefused(string name)
    {
        Assert.Throws<ArgumentException>(() => SecretName.Normalize(name));
    }

    [Fact]
    public void AnEmptyScopeMeansGlobal()
    {
        Assert.Equal(SecretScopes.Global, SecretScopes.Normalize(null));
        Assert.Equal(SecretScopes.Global, SecretScopes.Normalize("  "));
    }

    [Fact]
    public void ScopesAreFoldedToLowercase()
    {
        Assert.Equal("my-app", SecretScopes.Normalize("My-App"));
    }

    [Theory]
    [InlineData("has/slash")]
    [InlineData("has space")]
    public void ScopesThatWouldBreakARouteSegmentAreRefused(string scope)
    {
        Assert.Throws<ArgumentException>(() => SecretScopes.Normalize(scope));
    }

    // ---- values -------------------------------------------------------------

    [Fact]
    public void AnEmptyValueIsRefused()
    {
        Assert.Throws<ArgumentException>(() => _store.Set(SecretScopes.Global, "TOKEN", "", null, "admin", Now));
    }

    /// <summary>
    /// The value becomes one KEY=value line, and EnvFileStore refuses a multi-line
    /// value at the write boundary. Refusing it on the way in means a secret that
    /// could never be materialised is never stored.
    /// </summary>
    [Fact]
    public void AMultiLineValueIsRefused()
    {
        Assert.Throws<ArgumentException>(
            () => _store.Set(SecretScopes.Global, "KEY", "line one\nline two", null, "admin", Now));
    }

    // ---- versions -----------------------------------------------------------

    [Fact]
    public void SettingASecretTwiceAddsAVersionAndMakesItCurrent()
    {
        Assert.Equal(1, _store.Set(SecretScopes.Global, "TOKEN", "first", "a token", "admin", Now));
        Assert.Equal(2, _store.Set(SecretScopes.Global, "TOKEN", "second", null, "admin", Now));

        var summary = Assert.Single(_store.List());
        Assert.Equal(2, summary.CurrentVersion);
        Assert.Equal(2, summary.Versions.Count);
        Assert.Equal("a token", summary.Description);
        Assert.Equal("second", _store.Reveal(SecretScopes.Global, "TOKEN", version: null).Value);
        Assert.Equal("first", _store.Reveal(SecretScopes.Global, "TOKEN", version: 1).Value);
    }

    /// <summary>Rolling back keeps what came after, so rolling forward is the same call.</summary>
    [Fact]
    public void RollingBackIsReversible()
    {
        _store.Set(SecretScopes.Global, "TOKEN", "first", null, "admin", Now);
        _store.Set(SecretScopes.Global, "TOKEN", "second", null, "admin", Now);

        _store.UseVersion(SecretScopes.Global, "TOKEN", 1, "admin", Now);
        Assert.Equal("first", _store.Reveal(SecretScopes.Global, "TOKEN", version: null).Value);

        _store.UseVersion(SecretScopes.Global, "TOKEN", 2, "admin", Now);
        Assert.Equal("second", _store.Reveal(SecretScopes.Global, "TOKEN", version: null).Value);
    }

    [Fact]
    public void RollingBackToAVersionThatIsNotThereIsRefused()
    {
        _store.Set(SecretScopes.Global, "TOKEN", "first", null, "admin", Now);

        Assert.Throws<KeyNotFoundException>(() => _store.UseVersion(SecretScopes.Global, "TOKEN", 7, "admin", Now));
    }

    [Fact]
    public void VersionNumbersAreNeverReused()
    {
        for (var value = 1; value <= SecretStore.MaximumVersions + 3; value++)
        {
            _store.Set(SecretScopes.Global, "TOKEN", $"value-{value}", null, "admin", Now);
        }

        var summary = Assert.Single(_store.List());
        Assert.Equal(SecretStore.MaximumVersions, summary.Versions.Count);
        Assert.Equal(SecretStore.MaximumVersions + 3, summary.CurrentVersion);
        // The oldest went, the newest stayed, and nothing was renumbered.
        Assert.Equal(4, summary.Versions.Min(version => version.Version));
    }

    /// <summary>
    /// Whatever is current after an operation is always still on file. A secret
    /// naming a version that has been trimmed away would read as "not configured"
    /// and silently vanish from every app's env, so this is the invariant that
    /// matters — not which particular version survives.
    /// </summary>
    [Fact]
    public void TheCurrentVersionIsAlwaysStillOnFile()
    {
        _store.Set(SecretScopes.Global, "TOKEN", "original", null, "admin", Now);
        _store.UseVersion(SecretScopes.Global, "TOKEN", 1, "admin", Now);

        for (var value = 0; value < SecretStore.MaximumVersions + 5; value++)
        {
            _store.Set(SecretScopes.Global, "TOKEN", $"value-{value}", null, "admin", Now);
        }

        var summary = Assert.Single(_store.List());
        Assert.Equal(SecretStore.MaximumVersions, summary.Versions.Count);
        Assert.Contains(summary.Versions, version => version.Version == summary.CurrentVersion);
        Assert.Equal(
            $"value-{SecretStore.MaximumVersions + 4}",
            _store.Reveal(SecretScopes.Global, "TOKEN", version: null).Value);
    }

    /// <summary>
    /// Setting a new value supersedes a rollback rather than preserving it — the
    /// pinned version stops being current and then ages out like any other. Rolling
    /// back again reaches the oldest version still kept, never a trimmed-away gap.
    /// </summary>
    [Fact]
    public void SettingAValueSupersedesARollback()
    {
        _store.Set(SecretScopes.Global, "TOKEN", "first", null, "admin", Now);
        _store.Set(SecretScopes.Global, "TOKEN", "second", null, "admin", Now);
        _store.UseVersion(SecretScopes.Global, "TOKEN", 1, "admin", Now);

        _store.Set(SecretScopes.Global, "TOKEN", "third", null, "admin", Now);

        var summary = Assert.Single(_store.List());
        Assert.Equal(3, summary.CurrentVersion);
        Assert.Equal("third", _store.Reveal(SecretScopes.Global, "TOKEN", version: null).Value);

        var oldestKept = summary.Versions.Min(version => version.Version);
        _store.UseVersion(SecretScopes.Global, "TOKEN", oldestKept, "admin", Now);
        Assert.Equal("first", _store.Reveal(SecretScopes.Global, "TOKEN", version: null).Value);
    }

    // ---- resolution ---------------------------------------------------------

    [Fact]
    public void ResolveCombinesGlobalAndAppScopedSecrets()
    {
        _store.Set(SecretScopes.Global, "SHARED", "everywhere", null, "admin", Now);
        _store.Set("my-app", "PRIVATE", "just-mine", null, "admin", Now);
        _store.Set("other-app", "THEIRS", "not-mine", null, "admin", Now);

        var resolved = _store.Resolve("my-app");

        Assert.Equal(2, resolved.Count);
        Assert.Equal("everywhere", resolved["SHARED"]);
        Assert.Equal("just-mine", resolved["PRIVATE"]);
    }

    /// <summary>Two secrets cannot fight over one .env key, so the narrower wins.</summary>
    [Fact]
    public void AnAppScopedSecretShadowsAGlobalOneOfTheSameName()
    {
        _store.Set(SecretScopes.Global, "DATABASE_URL", "global-value", null, "admin", Now);
        _store.Set("my-app", "DATABASE_URL", "app-value", null, "admin", Now);

        Assert.Equal("app-value", _store.Resolve("my-app")["DATABASE_URL"]);
        Assert.Equal("global-value", _store.Resolve("other-app")["DATABASE_URL"]);
    }

    [Fact]
    public void ResolveReadsTheCurrentVersionNotTheNewest()
    {
        _store.Set("my-app", "TOKEN", "first", null, "admin", Now);
        _store.Set("my-app", "TOKEN", "second", null, "admin", Now);
        _store.UseVersion("my-app", "TOKEN", 1, "admin", Now);

        Assert.Equal("first", _store.Resolve("my-app")["TOKEN"]);
    }

    [Fact]
    public void ManagedNamesCoversEveryScope()
    {
        _store.Set(SecretScopes.Global, "SHARED", "x", null, "admin", Now);
        _store.Set("my-app", "PRIVATE", "y", null, "admin", Now);

        Assert.Equal(["PRIVATE", "SHARED"], _store.ManagedNames().OrderBy(name => name, StringComparer.Ordinal));
    }

    // ---- removal ------------------------------------------------------------

    [Fact]
    public void RemovingASecretTakesEveryVersionWithIt()
    {
        _store.Set("my-app", "TOKEN", "first", null, "admin", Now);
        _store.Set("my-app", "TOKEN", "second", null, "admin", Now);

        Assert.True(_store.Remove("my-app", "TOKEN"));
        Assert.Empty(_store.List());
        Assert.Throws<KeyNotFoundException>(() => _store.Reveal("my-app", "TOKEN", version: null));
    }

    [Fact]
    public void RemovingSomethingThatIsNotThereReportsIt()
    {
        Assert.False(_store.Remove("my-app", "TOKEN"));
    }

    /// <summary>The same name in two scopes is two secrets; removing one keeps the other.</summary>
    [Fact]
    public void ScopesAreIndependent()
    {
        _store.Set(SecretScopes.Global, "TOKEN", "global", null, "admin", Now);
        _store.Set("my-app", "TOKEN", "scoped", null, "admin", Now);

        _store.Remove("my-app", "TOKEN");

        Assert.Equal("global", _store.Reveal(SecretScopes.Global, "TOKEN", version: null).Value);
    }

    // ---- storage ------------------------------------------------------------

    [Fact]
    public void ValuesAreEncryptedOnDiskAndReadBackAsPlaintext()
    {
        _store.Set(SecretScopes.Global, "TOKEN", "hunter2-in-the-clear", null, "admin", Now);

        var onDisk = File.ReadAllText(_store.Path_);
        Assert.DoesNotContain("hunter2-in-the-clear", onDisk, StringComparison.Ordinal);
        Assert.Contains(SecretBox.Prefix, onDisk, StringComparison.Ordinal);

        var reopened = new SecretStore(_store.Path_);
        Assert.Equal("hunter2-in-the-clear", reopened.Reveal(SecretScopes.Global, "TOKEN", version: null).Value);
    }

    /// <summary>A second save must not encrypt the already-encrypted value again.</summary>
    [Fact]
    public void SavingTwiceDoesNotDoubleEncrypt()
    {
        _store.Set(SecretScopes.Global, "TOKEN", "value", null, "admin", Now);
        _store.Set(SecretScopes.Global, "OTHER", "value", null, "admin", Now);

        Assert.Equal("value", new SecretStore(_store.Path_).Reveal(SecretScopes.Global, "TOKEN", version: null).Value);
    }

    [Fact]
    public void TheFileIsOwnerOnly()
    {
        _store.Set(SecretScopes.Global, "TOKEN", "value", null, "admin", Now);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_store.Path_));
        }
    }

    /// <summary>A corrupt file means "no secrets", never a crash on every request.</summary>
    [Fact]
    public void ACorruptFileLoadsEmpty()
    {
        File.WriteAllText(_store.Path_, "{ this is not json");

        Assert.Empty(_store.List());
    }

    /// <summary>
    /// A hand-edited file can point CurrentVersion at a version that is not there.
    /// Resolving skips it rather than writing an empty value, which would look like
    /// a configured-but-blank secret in the container.
    /// </summary>
    [Fact]
    public void ASecretPointingAtAMissingVersionIsSkippedByResolve()
    {
        _store.Set("my-app", "TOKEN", "value", null, "admin", Now);
        _store.Update<object?>(file =>
        {
            file.Secrets[0].CurrentVersion = 99;
            return null;
        });

        Assert.Empty(_store.Resolve("my-app"));
    }

    [Fact]
    public void RevealingASecretThatIsNotThereIsNotFound()
    {
        Assert.Throws<KeyNotFoundException>(() => _store.Reveal(SecretScopes.Global, "MISSING", version: null));
    }
}
