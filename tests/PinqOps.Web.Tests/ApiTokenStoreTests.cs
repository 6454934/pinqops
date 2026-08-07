using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

public class ApiTokenStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pinqops-tokens-").FullName;

    private ApiTokenStore Store() => new(Path.Combine(_dir, "tokens.json"));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly DateTimeOffset Now = new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_ReturnsAPrefixedPlaintextValidatedOnce()
    {
        var store = Store();

        var (token, plaintext) = store.Create("ci", "deploy", Now);

        Assert.StartsWith("pot_", plaintext);
        Assert.EndsWith(token.Last4, plaintext);
        Assert.Equal("deploy", store.Validate(plaintext, Now));
    }

    [Fact]
    public void Validate_UnknownToken_IsNull()
    {
        Assert.Null(Store().Validate("pot_nope", Now));
    }

    [Fact]
    public void PlaintextIsNotRecoverableFromStorage()
    {
        var store = Store();
        var (_, plaintext) = store.Create("t", "read", Now);

        var stored = store.List().Single();

        Assert.DoesNotContain(plaintext, stored.Sha256);
        Assert.NotEqual(plaintext, stored.Sha256);
    }

    [Fact]
    public void Delete_RemovesTheToken()
    {
        var store = Store();
        var (token, plaintext) = store.Create("t", "admin", Now);

        Assert.True(store.Delete(token.Id));
        Assert.Null(store.Validate(plaintext, Now));
    }

    [Fact]
    public void Create_InvalidScope_FallsBackToRead()
    {
        var (token, _) = Store().Create("t", "superuser", Now);
        Assert.Equal("read", token.Scope);
    }
}

public class ApiScopesTests
{
    [Theory]
    [InlineData("GET", "/api/backups", "read")]
    [InlineData("POST", "/api/deploy/rollback", "deploy")]
    [InlineData("POST", "/api/setup/trigger-deploy", "deploy")]
    [InlineData("POST", "/api/compose/apply", "deploy")]
    [InlineData("POST", "/api/apps/install", "deploy")]
    [InlineData("POST", "/api/backups/run/db-postgres", "deploy")]
    [InlineData("POST", "/api/previews/acme-shop/7/teardown", "deploy")]
    [InlineData("POST", "/api/settings", "admin")]
    [InlineData("POST", "/api/tokens", "admin")]
    [InlineData("POST", "/api/domains", "admin")]
    [InlineData("POST", "/api/backups/targets", "admin")]
    [InlineData("DELETE", "/api/tokens/abc", "admin")]
    public void RequiredFor_MapsRoutesToScopes(string method, string path, string expected)
    {
        Assert.Equal(expected, ApiScopes.RequiredFor(method, path));
    }

    // A GET defaults to "read", so every route whose response body carries a
    // secret has to be named explicitly. Without these a viewer could read the
    // whole box: database dumps, generated app passwords, container environments.
    [Theory]
    [InlineData("/api/backups/download?target=db&snapshot=20260101-000000.sql")]
    [InlineData("/api/apps/postgres/credentials")]
    [InlineData("/api/runner/logs")]
    [InlineData("/api/tokens")]
    [InlineData("/api/users")]
    [InlineData("/api/audit")]
    // A Slack incoming-webhook URL is itself the credential, and this route returns
    // it verbatim — the same secret /api/alerts/channels is admin-gated for.
    [InlineData("/api/notifications")]
    public void RequiredFor_SecretBearingReads_AreAdminOnly(string path)
    {
        Assert.Equal("admin", ApiScopes.RequiredFor("GET", path));
    }

    /// <summary>
    /// Routing matches literal path segments case-insensitively, so this table has
    /// to as well. An ordinal comparison let /api/APPS/&lt;id&gt;/credentials fall
    /// through to the coarse "read" default and hand a viewer an app's password.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/APPS/postgres/credentials", "admin")]
    [InlineData("GET", "/api/Apps/postgres/Credentials", "admin")]
    [InlineData("GET", "/api/DOCKER/containers/web/logs", "deploy")]
    [InlineData("GET", "/api/Docker/Containers/web/inspect", "deploy")]
    [InlineData("POST", "/api/DOCKER/containers/web/exec", "admin")]
    [InlineData("POST", "/api/SETUP/trigger-deploy", "deploy")]
    [InlineData("POST", "/api/Setup/install-runner", "admin")]
    public void RequiredFor_MatchesLiteralSegmentsCaseInsensitively(string method, string path, string expected)
    {
        Assert.Equal(expected, ApiScopes.RequiredFor(method, path));
    }

    /// <summary>
    /// "/api/backups/run" as a string prefix also covered
    /// DELETE /api/backups/&lt;targetId&gt;/snapshots/&lt;snapshot&gt; for any target id
    /// starting with "run" — handing a deploy token an admin-only deletion.
    /// </summary>
    [Theory]
    [InlineData("POST", "/api/backups/run/db-postgres", "deploy")]
    [InlineData("DELETE", "/api/backups/runx/snapshots/20260101-000000.sql", "admin")]
    [InlineData("DELETE", "/api/backups/run/snapshots/20260101-000000.sql", "admin")]
    public void RequiredFor_BackupRunIsMatchedOnSegments(string method, string path, string expected)
    {
        Assert.Equal(expected, ApiScopes.RequiredFor(method, path));
    }

    /// <summary>
    /// These fell to the coarse "admin" write default, which locked a viewer out of
    /// signing out and out of rotating its own password. The handlers' own checks
    /// (the presented token, the current password) are the real authorization.
    /// </summary>
    [Theory]
    [InlineData("/api/auth/logout")]
    [InlineData("/api/auth/change-password")]
    public void RequiredFor_SelfServiceWrites_AreAvailableToEveryCaller(string path)
    {
        Assert.Equal("read", ApiScopes.RequiredFor("POST", path));
    }

    // What is firing, and the container names and percentages behind it, are
    // already on the Overview and Containers views — a viewer who cannot see the
    // alerts is a viewer who finds out about an outage last.
    [Theory]
    [InlineData("/api/alerts")]
    [InlineData("/api/alerts/state")]
    [InlineData("/api/alerts/history")]
    [InlineData("/api/alerts/metrics?metric=host.cpu&hours=6")]
    [InlineData("/api/alerts/targets")]
    public void RequiredFor_AlertReads_StayReadable(string path)
    {
        Assert.Equal("read", ApiScopes.RequiredFor("GET", path));
    }

    // The channels route hands back the Slack and webhook URLs verbatim, and
    // those URLs are the credential.
    [Fact]
    public void RequiredFor_AlertChannels_AreAdminOnly()
    {
        Assert.Equal("admin", ApiScopes.RequiredFor("GET", "/api/alerts/channels"));
    }

    // Silencing or disabling a rule turns off paging for everyone on the host, so
    // every alert write stays admin — a deployer rolling back its own app has no
    // business editing the server's monitoring.
    [Theory]
    [InlineData("POST", "/api/alerts/rules")]
    [InlineData("DELETE", "/api/alerts/rules/abcd1234")]
    [InlineData("POST", "/api/alerts/rules/abcd1234/toggle")]
    [InlineData("POST", "/api/alerts/rules/abcd1234/silence")]
    [InlineData("POST", "/api/alerts/rules/abcd1234/test")]
    [InlineData("POST", "/api/alerts/channels")]
    [InlineData("POST", "/api/alerts/channels/test")]
    public void RequiredFor_AlertWrites_AreAdminOnly(string method, string path)
    {
        Assert.Equal("admin", ApiScopes.RequiredFor(method, path));
        Assert.False(ApiScopes.Satisfies("deploy", ApiScopes.RequiredFor(method, path)));
    }

    // The UI needs the ownership map to decide which actions to offer, so it
    // stays readable and the handler filters the records instead.
    [Fact]
    public void RequiredFor_OwnershipMap_StaysReadable()
    {
        Assert.Equal("read", ApiScopes.RequiredFor("GET", "/api/docker/ownership"));
    }

    // Per-container reads expose the container's environment, output and process
    // list, so they sit above a plain viewer but stay reachable by an owner.
    [Theory]
    [InlineData("/api/docker/containers/web/logs")]
    [InlineData("/api/docker/containers/web/inspect")]
    [InlineData("/api/docker/containers/web/top")]
    public void RequiredFor_PerContainerReads_RequireDeploy(string path)
    {
        Assert.Equal("deploy", ApiScopes.RequiredFor("GET", path));
    }

    // Operating a container is a deploy action; running code inside it,
    // destroying it, or changing who owns it is not.
    [Theory]
    [InlineData("action", "deploy")]
    [InlineData("exec", "admin")]
    [InlineData("remove", "admin")]
    [InlineData("commit", "admin")]
    [InlineData("rename", "admin")]
    [InlineData("restart-policy", "admin")]
    [InlineData("owner", "admin")]
    public void RequiredFor_ContainerWrites_SplitByAction(string action, string expected)
    {
        Assert.Equal(expected, ApiScopes.RequiredFor("POST", $"/api/docker/containers/web/{action}"));
    }

    // Creating a container carries no id, so it must not be mistaken for
    // operating one — it stays admin-only.
    [Fact]
    public void RequiredFor_CreateContainer_IsAdmin()
    {
        Assert.Equal("admin", ApiScopes.RequiredFor("POST", "/api/docker/containers"));
    }

    // The console is `exec` held open. Classifying it any lower than the one-shot
    // form would refuse a caller a single command and then hand it a shell
    // instead — and it is a GET, so nothing but this classification stands between
    // the coarse read default and a prompt inside the container.
    [Fact]
    public void RequiredFor_ContainerConsole_IsAdmin_LikeExec()
    {
        // The route's ownership gate cannot be what stops a non-admin, which is
        // why the scope has to: a deploy-scoped caller manages any container
        // marked public, and clears that gate on its way through.
        Assert.True(ContainerAccess.CanManage(
            "deploy",
            "deployer1",
            new ContainerOwnershipStore.ContainerOwnership { Access = ContainerOwnershipStore.AccessPublic }));

        Assert.Equal("admin", ApiScopes.RequiredFor("GET", "/api/ws/containers/web/console"));
        Assert.Equal(
            ApiScopes.RequiredFor("POST", "/api/docker/containers/web/exec"),
            ApiScopes.RequiredFor("GET", "/api/ws/containers/web/console"));
        Assert.False(ApiScopes.Satisfies("deploy", ApiScopes.RequiredFor("GET", "/api/ws/containers/web/console")));
    }

    // Both of these reach host root: install-runner shells out to `sudo ./svc.sh`,
    // and create-dockerfile commits caller-supplied content that the pipeline
    // then builds and runs on the host.
    [Theory]
    [InlineData("/api/setup/install-runner")]
    [InlineData("/api/setup/create-dockerfile")]
    public void RequiredFor_SetupStepsReachingHostRoot_AreAdminOnly(string path)
    {
        Assert.Equal("admin", ApiScopes.RequiredFor("POST", path));
    }

    // The workflow steps commit a fixed server-side template, never caller input,
    // so a deployer may still run the publish wizard end to end.
    [Theory]
    [InlineData("/api/setup/create-workflow")]
    [InlineData("/api/setup/update-workflow")]
    [InlineData("/api/setup/app-var")]
    [InlineData("/api/setup/create-compose")]
    [InlineData("/api/setup/start-runner")]
    public void RequiredFor_RemainingSetupSteps_StayDeploy(string path)
    {
        Assert.Equal("deploy", ApiScopes.RequiredFor("POST", path));
    }

    // A restore wipes and reloads a live database or volume.
    [Fact]
    public void RequiredFor_BackupRestore_IsAdmin()
    {
        Assert.Equal("admin", ApiScopes.RequiredFor("POST", "/api/backups/restore"));
    }

    [Theory]
    [InlineData("read", "read", true)]
    [InlineData("deploy", "read", true)]
    [InlineData("admin", "deploy", true)]
    [InlineData("read", "deploy", false)]
    [InlineData("deploy", "admin", false)]
    public void Satisfies_RespectsTheHierarchy(string have, string need, bool ok)
    {
        Assert.Equal(ok, ApiScopes.Satisfies(have, need));
    }
}

public class ApiTokenExpiryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pinqops-tokenexp-").FullName;

    private ApiTokenStore Store() => new(Path.Combine(_dir, "tokens.json"));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly DateTimeOffset Now = new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);

    // An agent token handed out once and forgotten used to be valid forever.
    [Fact]
    public void Create_WithAnExpiry_StopsValidatingAfterIt()
    {
        var store = Store();
        var (_, plaintext) = store.Create("ci", "deploy", Now, expiresInDays: 30);

        Assert.Equal("deploy", store.Validate(plaintext, Now.AddDays(29)));
        Assert.Null(store.Validate(plaintext, Now.AddDays(31)));
    }

    [Fact]
    public void Create_WithoutAnExpiry_NeverExpires()
    {
        var store = Store();
        var (token, plaintext) = store.Create("ci", "deploy", Now);

        Assert.Null(token.ExpiresAt);
        Assert.Equal("deploy", store.Validate(plaintext, Now.AddYears(10)));
    }

    // Kept and listed as expired, so it is obvious why it stopped working.
    [Fact]
    public void AnExpiredTokenIsStillListed()
    {
        var store = Store();
        store.Create("ci", "read", Now, expiresInDays: 1);

        var stored = store.List().Single();

        Assert.True(stored.IsExpired(Now.AddDays(2)));
        Assert.False(stored.IsExpired(Now));
    }

    [Fact]
    public void ZeroOrNegativeDays_MeansNoExpiry()
    {
        Assert.Null(Store().Create("a", "read", Now, expiresInDays: 0).Token.ExpiresAt);
        Assert.Null(Store().Create("b", "read", Now, expiresInDays: -5).Token.ExpiresAt);
    }

    [Fact]
    public void TokenFileIsOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix file modes only.
        }

        var store = Store();
        store.Create("ci", "read", Now);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(Path.Combine(_dir, "tokens.json")));
    }
}
