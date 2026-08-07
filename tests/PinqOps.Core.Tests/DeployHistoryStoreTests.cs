using Xunit;

namespace PinqOps.Tests;

public class DeployHistoryStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _composePath;
    private readonly DeployHistoryStore _store;

    public DeployHistoryStoreTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-history-tests").FullName;
        _composePath = Path.Combine(_directory, "docker-compose.yml");
        _store = new DeployHistoryStore(_composePath);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static DeployRecord Record(
        string tag, string result = DeployRecordValues.ResultSucceeded, string? previousTag = null) => new()
    {
        Id = DeployHistoryStore.NewRecordId(),
        Tag = tag,
        StartedAt = DateTimeOffset.UtcNow,
        Result = result,
        Trigger = result == DeployRecordValues.ResultRolledBack
            ? DeployRecordValues.TriggerRollback
            : DeployRecordValues.TriggerCi,
        // What the deploy replaced. Deployer always records it, and the rollback
        // chain is followed through it.
        PreviousTag = previousTag,
    };

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(_store.Load());
    }

    [Fact]
    public void Append_StoresNewestFirst_InStateDirectory()
    {
        _store.Append(Record("sha-1"));
        _store.Append(Record("sha-2"));

        var records = _store.Load();
        Assert.Equal(2, records.Count);
        Assert.Equal("sha-2", records[0].Tag);
        Assert.Equal("sha-1", records[1].Tag);
        Assert.True(File.Exists(Path.Combine(_directory, ".pinqops", "history.json")));
    }

    [Fact]
    public void Append_CapsAtMaxEntries()
    {
        for (var i = 0; i < DeployHistoryStore.MaxEntries + 5; i++)
        {
            _store.Append(Record($"sha-{i}"));
        }

        var records = _store.Load();
        Assert.Equal(DeployHistoryStore.MaxEntries, records.Count);
        Assert.Equal($"sha-{DeployHistoryStore.MaxEntries + 4}", records[0].Tag);
    }

    [Fact]
    public void LastSuccessfulTagBefore_SkipsFailuresAndCurrentTag()
    {
        _store.Append(Record("sha-1"));
        _store.Append(Record("sha-2", DeployRecordValues.ResultFailed));
        _store.Append(Record("sha-3"));

        Assert.Equal("sha-1", _store.LastSuccessfulTagBefore("sha-3"));
        Assert.Equal("sha-3", _store.LastSuccessfulTagBefore("sha-9"));

        // History is keyed by project DIRECTORY; an empty project has none.
        var emptyDirectory = Directory.CreateDirectory(Path.Combine(_directory, "empty")).FullName;
        Assert.Null(new DeployHistoryStore(Path.Combine(emptyDirectory, "docker-compose.yml")).LastSuccessfulTagBefore("x"));
    }

    /// <summary>
    /// A rollback is a successful deployment of the tag it restored, so its record
    /// counts. Skipping rolled_back records made the walk return the tag the last
    /// rollback had just escaped from — so a second `pinqops rollback` rolled
    /// FORWARD onto the bad release instead of one step further back.
    /// </summary>
    [Fact]
    public void LastSuccessfulTagBefore_KeepsMovingBackwardsAcrossRepeatedRollbacks()
    {
        _store.Append(Record("sha-1"));
        _store.Append(Record("sha-2"));
        _store.Append(Record("sha-3"));

        // Roll back off sha-3 onto sha-2.
        Assert.Equal("sha-2", _store.LastSuccessfulTagBefore("sha-3"));
        _store.Append(Record("sha-2", DeployRecordValues.ResultRolledBack, previousTag: "sha-3"));

        // The next rollback must go to sha-1, not back to sha-3.
        Assert.Equal("sha-1", _store.LastSuccessfulTagBefore("sha-2"));
        _store.Append(Record("sha-1", DeployRecordValues.ResultRolledBack, previousTag: "sha-2"));

        // And there is nothing older to reach for.
        Assert.Null(_store.LastSuccessfulTagBefore("sha-1"));
    }

    [Fact]
    public void LastSuccessfulTagBefore_AfterANewDeployFollowingARollback_RollsBackToTheRollbackTarget()
    {
        _store.Append(Record("sha-1"));
        _store.Append(Record("sha-2"));
        _store.Append(Record("sha-1", DeployRecordValues.ResultRolledBack, previousTag: "sha-2"));
        _store.Append(Record("sha-3"));

        Assert.Equal("sha-1", _store.LastSuccessfulTagBefore("sha-3"));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmptyInsteadOfThrowing()
    {
        Directory.CreateDirectory(Path.Combine(_directory, ".pinqops"));
        File.WriteAllText(Path.Combine(_directory, ".pinqops", "history.json"), "{not json");

        Assert.Empty(_store.Load());
    }
}
