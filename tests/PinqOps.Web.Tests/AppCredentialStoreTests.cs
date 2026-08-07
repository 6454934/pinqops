using Xunit;

namespace PinqOps.Web.Tests;

public class AppCredentialStoreTests : IDisposable
{
    private const string Local = ManagedEnvironment.LocalId;

    private readonly string _directory;
    private readonly AppCredentialStore _store;

    public AppCredentialStoreTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-creds-tests").FullName;
        _store = new AppCredentialStore(Path.Combine(_directory, "app-credentials.json"));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void GetOrCreatePassword_IsStableAcrossCalls()
    {
        var first = _store.GetOrCreatePassword(Local, "postgres");
        var second = _store.GetOrCreatePassword(Local, "postgres");

        Assert.Equal(first, second);
        Assert.Equal(PasswordGenerator.Length, first.Length);
    }

    [Fact]
    public void GetOrCreatePassword_DiffersPerApp_AndIsCaseInsensitive()
    {
        var postgres = _store.GetOrCreatePassword(Local, "postgres");
        var mysql = _store.GetOrCreatePassword(Local, "mysql");

        Assert.NotEqual(postgres, mysql);
        Assert.Equal(postgres, _store.GetOrCreatePassword(Local, "Postgres"));
    }

    [Fact]
    public void SetEnv_PersistsRetrievableCredentials_WithOwnerOnlyPermissions()
    {
        _store.SetEnv(Local, "postgres", new Dictionary<string, string> { ["POSTGRES_PASSWORD"] = "s3cret" });

        var reloaded = new AppCredentialStore(_store.Path_);
        Assert.Equal("s3cret", reloaded.Get(Local, "postgres")!["POSTGRES_PASSWORD"]);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_store.Path_));
        }
    }

    [Fact]
    public void Get_UnknownApp_ReturnsNull()
    {
        Assert.Null(_store.Get(Local, "nope"));
    }

    [Fact]
    public void CorruptFile_StartsFresh()
    {
        File.WriteAllText(_store.Path_, "{broken");

        Assert.Null(_store.Get(Local, "postgres"));
        Assert.NotNull(_store.GetOrCreatePassword(Local, "postgres"));
    }
}

/// <summary>
/// What the credentials dialog is allowed to show. The raw "password" entry is
/// normally a duplicate of a named one and is hidden — but for redis, keydb,
/// nats and surrealdb the password only ever reaches the container through its
/// command line, so it is the only record of it and hiding it left those apps
/// claiming they had no stored credentials at all.
/// </summary>
public class AppCredentialDisplayTests
{
    [Fact]
    public void Displayable_HidesTheRawPasswordWhenANamedEntryCarriesIt()
    {
        var env = new Dictionary<string, string>
        {
            [AppCredentialStore.PasswordKey] = "s3cret",
            ["POSTGRES_PASSWORD"] = "s3cret",
        };

        var shown = AppCredentialStore.Displayable(env);

        Assert.Equal("POSTGRES_PASSWORD", Assert.Single(shown).Key);
    }

    [Fact]
    public void Displayable_ShowsTheRawPasswordWhenNothingElseDoes()
    {
        // What a redis install stores: the command line carried the password, so
        // ResolveEnv recorded no named credential for it.
        var env = new Dictionary<string, string> { [AppCredentialStore.PasswordKey] = "s3cret" };

        var only = Assert.Single(AppCredentialStore.Displayable(env));

        Assert.Equal(AppCredentialStore.PasswordKey, only.Key);
        Assert.Equal("s3cret", only.Value);
    }

    [Fact]
    public void Displayable_KeepsNamedEntriesThatAreNotThePassword()
    {
        var env = new Dictionary<string, string>
        {
            [AppCredentialStore.PasswordKey] = "s3cret",
            ["MONGO_INITDB_ROOT_USERNAME"] = "root",
            ["MONGO_INITDB_ROOT_PASSWORD"] = "s3cret",
        };

        var shown = AppCredentialStore.Displayable(env).Select(pair => pair.Key).ToList();

        Assert.Contains("MONGO_INITDB_ROOT_USERNAME", shown);
        Assert.Contains("MONGO_INITDB_ROOT_PASSWORD", shown);
        Assert.DoesNotContain(AppCredentialStore.PasswordKey, shown);
    }

    [Fact]
    public void Displayable_OfNothingIsEmpty()
    {
        Assert.Empty(AppCredentialStore.Displayable(null));
        Assert.Empty(AppCredentialStore.Displayable(new Dictionary<string, string>()));
    }
}

/// <summary>
/// Credentials used to be keyed by app id alone, so the same app on two hosts
/// shared one generated password — compromising staging would have handed over
/// production's.
/// </summary>
public class AppCredentialEnvironmentTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pinqops-cred-env-").FullName;

    private AppCredentialStore Store() => new(Path.Combine(_dir, "app-credentials.json"));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void TheSameAppOnAnotherEnvironmentGetsItsOwnPassword()
    {
        var store = Store();

        var staging = store.GetOrCreatePassword("staging", "postgres");
        var production = store.GetOrCreatePassword("production", "postgres");

        Assert.NotEqual(staging, production);
    }

    // A reinstall on the same host must still line up with data in the volume.
    [Fact]
    public void ThePasswordIsStableWithinAnEnvironment()
    {
        var store = Store();

        Assert.Equal(
            store.GetOrCreatePassword("staging", "postgres"),
            store.GetOrCreatePassword("staging", "postgres"));
    }

    [Fact]
    public void StoredEnvIsScopedToItsEnvironment()
    {
        var store = Store();
        store.SetEnv("staging", "postgres", new Dictionary<string, string> { ["POSTGRES_PASSWORD"] = "a" });

        Assert.Equal("a", store.Get("staging", "postgres")!["POSTGRES_PASSWORD"]);
        Assert.Null(store.Get("production", "postgres"));
    }

    // Entries written before environments existed described the only host there
    // was; losing them would regenerate passwords that no longer match the data
    // in the existing volumes.
    [Fact]
    public void EntriesFromBeforeEnvironmentsBelongToLocal()
    {
        var path = Path.Combine(_dir, "app-credentials.json");
        File.WriteAllText(path, """{"postgres":{"env":{"password":"legacy-secret"},"createdAt":"2026-01-01T00:00:00+00:00"}}""");

        var store = new AppCredentialStore(path);

        Assert.Equal("legacy-secret", store.GetOrCreatePassword(ManagedEnvironment.LocalId, "postgres"));
    }
}
