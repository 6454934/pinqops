using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

public class ContainerAccessTests
{
    private static ContainerOwnershipStore.ContainerOwnership Owned(string owner, string access = ContainerOwnershipStore.AccessPrivate) =>
        new() { Owner = owner, Access = access };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Admin_ManagesEverything_EvenUnowned(string? _)
    {
        await Task.CompletedTask;
        Assert.True(ContainerAccess.CanManage("admin", "root", null));
        Assert.True(ContainerAccess.CanManage("admin", "root", Owned("someone-else")));
    }

    [Fact]
    public void Viewer_ManagesNothing()
    {
        Assert.False(ContainerAccess.CanManage("read", "alice", Owned("alice", ContainerOwnershipStore.AccessPublic)));
        Assert.False(ContainerAccess.CanManage("read", "alice", null));
    }

    [Fact]
    public void Deployer_ManagesOwnAndPublic_ButNotOthersOrUnowned()
    {
        Assert.True(ContainerAccess.CanManage("deploy", "alice", Owned("alice")));
        Assert.True(ContainerAccess.CanManage("deploy", "alice", Owned("bob", ContainerOwnershipStore.AccessPublic)));
        Assert.False(ContainerAccess.CanManage("deploy", "alice", Owned("bob")));
        Assert.False(ContainerAccess.CanManage("deploy", "alice", null));
    }

    [Fact]
    public void Ownership_MatchIsCaseInsensitive()
    {
        Assert.True(ContainerAccess.CanManage("deploy", "Alice", Owned("alice")));
    }
}

public class ContainerOwnershipStoreTests
{
    private const string Local = ManagedEnvironment.LocalId;

    private static string TempPath() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pinqops-owners-{System.IO.Path.GetRandomFileName()}.json");

    [Fact]
    public void SetGet_RoundTrips_AndNormalizesAccess()
    {
        var path = TempPath();
        try
        {
            var store = new ContainerOwnershipStore(path);
            store.Set(Local, "burze-api", "alice", "public");
            store.Set(Local, "cache", "bob", "not-a-level");

            Assert.Equal("alice", store.Get(Local, "burze-api")!.Owner);
            Assert.Equal(ContainerOwnershipStore.AccessPublic, store.Get(Local, "burze-api")!.Access);
            // An unrecognized access value falls back to the safe default.
            Assert.Equal(ContainerOwnershipStore.AccessPrivate, store.Get(Local, "cache")!.Access);
            Assert.Null(store.Get(Local, "unknown"));
        }
        finally { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
    }

    [Fact]
    public void Remove_DropsTheEntry()
    {
        var path = TempPath();
        try
        {
            var store = new ContainerOwnershipStore(path);
            store.Set(Local, "burze-api", "alice", "private");
            store.Remove(Local, "burze-api");
            Assert.Null(store.Get(Local, "burze-api"));
        }
        finally { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
    }

    [Fact]
    public void Ownership_PersistsAcrossInstances()
    {
        var path = TempPath();
        try
        {
            new ContainerOwnershipStore(path).Set(Local, "burze-api", "alice", "public");
            var reloaded = new ContainerOwnershipStore(path).Get(Local, "burze-api");
            Assert.Equal("alice", reloaded!.Owner);
            Assert.Equal(ContainerOwnershipStore.AccessPublic, reloaded.Access);
        }
        finally { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
    }

    [Fact]
    public void LegacyAndMigratedKeysForOneContainer_DoNotThrow_AndTheNamespacedOneWins()
    {
        // Both spellings fold onto "local/web". ToDictionary threw for that
        // collision, and this load runs inside the ownership middleware — outside
        // the handler error boundary — so the throw was an unhandled failure on
        // every governed request, on a file the store promises must never lock
        // anyone out.
        var path = TempPath();
        try
        {
            System.IO.File.WriteAllText(
                path,
                """{"web":{"owner":"legacy","access":"private"},"local/web":{"owner":"current","access":"public"}}""");

            var record = new ContainerOwnershipStore(path).Get(Local, "web");

            Assert.NotNull(record);
            Assert.Equal("current", record!.Owner);
            Assert.Equal(ContainerOwnershipStore.AccessPublic, record.Access);
        }
        finally { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
    }

    [Fact]
    public void CorruptFile_ReadsAsUnowned_RatherThanThrowing()
    {
        var path = TempPath();
        try
        {
            System.IO.File.WriteAllText(path, "{ not json");

            var store = new ContainerOwnershipStore(path);

            Assert.Null(store.Get(Local, "web"));
            Assert.Empty(store.All(Local));
        }
        finally { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
    }
}

/// <summary>
/// Ownership used to be keyed by container name alone. With more than one Docker
/// host that meant owning <c>web</c> on staging also granted <c>web</c> in
/// production, so the key now carries the environment.
/// </summary>
public class ContainerOwnershipEnvironmentTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"pinqops-owners-env-{Path.GetRandomFileName()}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    [Fact]
    public void TheSameNameOnAnotherEnvironmentIsADifferentContainer()
    {
        var store = new ContainerOwnershipStore(_path);
        store.Set("staging", "web", "alice", ContainerOwnershipStore.AccessPublic);

        Assert.Equal("alice", store.Get("staging", "web")!.Owner);
        Assert.Null(store.Get("production", "web"));
    }

    // Unowned means admin-only, so the isolation has to hold through the rule
    // the middleware actually consults, not just the store.
    [Fact]
    public void ADeployerCannotReachTheSameNameElsewhere()
    {
        var store = new ContainerOwnershipStore(_path);
        store.Set("staging", "web", "alice", ContainerOwnershipStore.AccessPublic);

        Assert.True(ContainerAccess.CanManage("deploy", "alice", store.Get("staging", "web")));
        Assert.False(ContainerAccess.CanManage("deploy", "alice", store.Get("production", "web")));
    }

    [Fact]
    public void RemovingOnOneEnvironmentLeavesTheOther()
    {
        var store = new ContainerOwnershipStore(_path);
        store.Set("staging", "web", "alice", ContainerOwnershipStore.AccessPrivate);
        store.Set("production", "web", "bob", ContainerOwnershipStore.AccessPrivate);

        store.Remove("staging", "web");

        Assert.Null(store.Get("staging", "web"));
        Assert.Equal("bob", store.Get("production", "web")!.Owner);
    }

    [Fact]
    public void AllIsScopedToOneEnvironmentAndKeyedByContainerName()
    {
        var store = new ContainerOwnershipStore(_path);
        store.Set("staging", "web", "alice", ContainerOwnershipStore.AccessPrivate);
        store.Set("production", "db", "bob", ContainerOwnershipStore.AccessPrivate);

        var staging = store.All("staging");

        Assert.Equal(["web"], staging.Keys);
        Assert.Equal("alice", staging["web"].Owner);
    }

    // Records written before environments existed described the only host there
    // was, so they have to keep working as local rather than becoming unowned —
    // which would silently make every one of them admin-only.
    [Fact]
    public void RecordsFromBeforeEnvironmentsBelongToLocal()
    {
        File.WriteAllText(_path, """{"burze-api":{"owner":"alice","access":"public"}}""");

        var store = new ContainerOwnershipStore(_path);

        Assert.Equal("alice", store.Get(ManagedEnvironment.LocalId, "burze-api")!.Owner);
        Assert.Equal(ContainerOwnershipStore.AccessPublic, store.Get(ManagedEnvironment.LocalId, "burze-api")!.Access);
    }

    [Fact]
    public void MigratedRecordsSurviveARewrite()
    {
        File.WriteAllText(_path, """{"burze-api":{"owner":"alice","access":"public"}}""");

        new ContainerOwnershipStore(_path).Set(ManagedEnvironment.LocalId, "other", "bob", "private");

        var reloaded = new ContainerOwnershipStore(_path);
        Assert.Equal("alice", reloaded.Get(ManagedEnvironment.LocalId, "burze-api")!.Owner);
        Assert.Equal("bob", reloaded.Get(ManagedEnvironment.LocalId, "other")!.Owner);
    }

    [Fact]
    public void EnvironmentIdIsMatchedCaseInsensitively()
    {
        var store = new ContainerOwnershipStore(_path);
        store.Set("Staging", "web", "alice", ContainerOwnershipStore.AccessPrivate);

        Assert.NotNull(store.Get("staging", "web"));
    }
}
