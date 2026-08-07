using System.Text.Json;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

public class UiConfigStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pinqops-uiconfig-").FullName;

    private string ConfigPath => Path.Combine(_dir, "ui.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // The token has push access to the connected repositories, so a copy of
    // ui.json — in a backup, a support bundle, a pasted issue — must not hand it
    // over.
    [Fact]
    public void Save_DoesNotWriteThePatInPlaintext()
    {
        var store = new UiConfigStore(ConfigPath);

        store.Update(config => config.Pat = "ghp_verysecrettoken");

        Assert.DoesNotContain("ghp_verysecrettoken", File.ReadAllText(ConfigPath));
        Assert.Equal("ghp_verysecrettoken", new UiConfigStore(ConfigPath).Current.Pat);
    }

    // Callers keep seeing plaintext; encryption happens on the way to disk only.
    [Fact]
    public void Current_ExposesThePatDecrypted()
    {
        var store = new UiConfigStore(ConfigPath);

        store.Update(config => config.Pat = "ghp_x");

        Assert.Equal("ghp_x", store.Current.Pat);
    }

    // A config written before encryption existed must keep working.
    [Fact]
    public void Load_ReadsAPlaintextPatFromAnOlderConfig()
    {
        File.WriteAllText(ConfigPath, """{ "Pat": "ghp_legacy", "Users": [] }""");

        Assert.Equal("ghp_legacy", new UiConfigStore(ConfigPath).Current.Pat);
    }

    [Fact]
    public void Load_MigratesALegacySingleAppConfig()
    {
        File.WriteAllText(ConfigPath, """
            { "PasswordHash": "c2FsdA==.aGFzaA==",
              "RepoUrl": "https://github.com/acme/shop",
              "Pat": "ghp_x",
              "ComposeFile": "/opt/pinqops/docker-compose.yml",
              "RunnerDirectory": "/opt/actions-runner" }
            """);

        var config = new UiConfigStore(ConfigPath).Current;

        var app = Assert.Single(config.Apps);
        Assert.Equal("acme-shop", app.Id);
        Assert.Equal("https://github.com/acme/shop", app.RepoUrl);
        // The migrated app KEEPS its paths — deploy history and .env live there.
        Assert.Equal("/opt/pinqops/docker-compose.yml", app.ComposeFile);
        Assert.Equal("/opt/actions-runner", app.RunnerDirectory);
        // The legacy password becomes the sole admin user; the top-level field is cleared.
        var user = Assert.Single(config.Users);
        Assert.Equal("admin", user.Username);
        Assert.Equal("c2FsdA==.aGFzaA==", user.PasswordHash);
        Assert.Equal("admin", user.Role);
        Assert.Null(config.PasswordHash);
        Assert.Equal("ghp_x", config.Pat);
        Assert.Null(config.RepoUrl);
    }

    [Fact]
    public void Load_LegacyConfigWithoutPaths_GetsTheOldDefaults()
    {
        File.WriteAllText(ConfigPath, """{ "RepoUrl": "https://github.com/acme/shop" }""");

        var app = Assert.Single(new UiConfigStore(ConfigPath).Current.Apps);

        Assert.Equal(UiConfig.DefaultComposeFile, app.ComposeFile);
        Assert.Equal(UiConfig.DefaultRunnerDirectory, app.RunnerDirectory);
    }

    [Fact]
    public void Load_NeverConnectedLegacyConfig_HasNoApps()
    {
        File.WriteAllText(ConfigPath, """{ "PasswordHash": "s.h" }""");

        var config = new UiConfigStore(ConfigPath).Current;

        Assert.Empty(config.Apps);
        // The password still migrates to the sole admin, even with no app connected.
        Assert.Equal("s.h", Assert.Single(config.Users).PasswordHash);
        Assert.Null(config.PasswordHash);
    }

    [Fact]
    public void Migrate_PreservesExistingUsers_AndIsIdempotent()
    {
        var config = new UiConfig
        {
            PasswordHash = "legacy.hash",
            Users = [new UserAccount { Username = "alice", PasswordHash = "a.h", Role = "deployer" }],
        };

        var migrated = UiConfigStore.Migrate(config);

        // An existing users list means the legacy hash is NOT re-added.
        Assert.Equal("alice", Assert.Single(migrated.Users).Username);
        Assert.Null(migrated.PasswordHash);
    }

    [Fact]
    public void Migrate_IsIdempotent()
    {
        var config = new UiConfig
        {
            RepoUrl = "https://github.com/acme/shop",
            Apps = [new AppConnection { Id = "x", RepoUrl = "u", ComposeFile = "c", RunnerDirectory = "r" }],
        };

        var migrated = UiConfigStore.Migrate(config);

        Assert.Single(migrated.Apps); // the legacy URL must not become a second app
        Assert.Null(migrated.RepoUrl);
    }

    [Fact]
    public void Migrate_GarbageLegacyUrl_StillKeepsTheConnection()
    {
        var config = new UiConfig { RepoUrl = "not a url" };

        var app = Assert.Single(UiConfigStore.Migrate(config).Apps);

        Assert.Equal("app", app.Id);
        Assert.Equal("not a url", app.RepoUrl);
    }

    [Fact]
    public void Update_SavesTheNewShape_WithoutLegacyFields()
    {
        File.WriteAllText(ConfigPath, """
            { "PasswordHash": "s.h", "RepoUrl": "https://github.com/acme/shop" }
            """);
        var store = new UiConfigStore(ConfigPath);

        store.Update(_ => { });

        using var saved = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        Assert.False(saved.RootElement.TryGetProperty("RepoUrl", out _));
        // The legacy top-level password is gone; it now lives in Users.
        Assert.False(saved.RootElement.TryGetProperty("PasswordHash", out _));
        Assert.Equal("s.h", saved.RootElement.GetProperty("Users")[0].GetProperty("PasswordHash").GetString());
        Assert.Equal(1, saved.RootElement.GetProperty("Apps").GetArrayLength());

        // And a reload of the saved file round-trips.
        var reloaded = new UiConfigStore(ConfigPath).Current;
        Assert.Equal("acme-shop", Assert.Single(reloaded.Apps).Id);
    }

    [Fact]
    public void Load_CorruptFile_StartsFresh()
    {
        File.WriteAllText(ConfigPath, "{ not json");

        var config = new UiConfigStore(ConfigPath).Current;

        Assert.Null(config.PasswordHash);
        Assert.Empty(config.Apps);
    }
}
