using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

public class AuditLogTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public AuditLogTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-audit-tests").FullName;
        _path = Path.Combine(_directory, "audit.jsonl");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static AuditEntry Entry(string user, string action, string result = "ok", int status = 200) =>
        new(DateTimeOffset.UnixEpoch, user, action, string.Empty, result, status);

    [Fact]
    public void Read_ReturnsAppendedEntriesNewestFirst()
    {
        var log = new AuditLog(_path);
        log.Append(Entry("alice", "POST /api/deploy/rollback") with { Timestamp = DateTimeOffset.UnixEpoch });
        log.Append(Entry("bob", "DELETE /api/users/x") with { Timestamp = DateTimeOffset.UnixEpoch.AddMinutes(1) });

        var items = log.Read();

        Assert.Equal(2, items.Count);
        Assert.Equal("bob", items[0].User);
        Assert.Equal("alice", items[1].User);
    }

    [Fact]
    public void Read_FiltersByUserAndAction()
    {
        var log = new AuditLog(_path);
        log.Append(Entry("alice", "POST /api/deploy/rollback"));
        log.Append(Entry("bob", "POST /api/users"));
        log.Append(Entry("alice", "POST /api/users"));

        Assert.Equal(2, log.Read(user: "alice").Count);
        Assert.All(log.Read(action: "/api/users"), e => Assert.Contains("/api/users", e.Action));
        Assert.Single(log.Read(user: "alice", action: "rollback"));
    }

    [Fact]
    public void Read_ToleratesACorruptLine()
    {
        File.WriteAllText(_path, "{ not json\n" + """{"ts":"1970-01-01T00:00:00+00:00","user":"a","action":"x","target":"","result":"ok","status":200}""" + "\n");

        var items = new AuditLog(_path).Read();

        Assert.Single(items);
        Assert.Equal("a", items[0].User);
    }

    [Fact]
    public void Append_MissingDirectoryIsCreated()
    {
        var nested = Path.Combine(_directory, "sub", "dir", "audit.jsonl");
        var log = new AuditLog(nested);

        log.Append(Entry("alice", "POST /api/x"));

        Assert.True(File.Exists(nested));
        Assert.Single(log.Read());
    }

    [Fact]
    public void Append_ChainsEachEntryToTheOneBefore()
    {
        var log = new AuditLog(_path);
        log.Append(Entry("alice", "POST /api/a"));
        log.Append(Entry("bob", "POST /api/b"));

        var items = log.Read();

        Assert.All(items, e => Assert.NotEmpty(e.Hash));
        Assert.NotEqual(items[0].Hash, items[1].Hash);
    }

    [Fact]
    public void Verify_PassesOnAnUntouchedTrail()
    {
        var log = new AuditLog(_path);
        log.Append(Entry("alice", "POST /api/a"));
        log.Append(Entry("bob", "POST /api/b"));
        log.Append(Entry("carol", "POST /api/c"));

        var result = log.Verify();

        Assert.True(result.Ok);
        Assert.Equal(3, result.Entries);
        Assert.Equal(-1, result.FirstBrokenIndex);
    }

    // The oldest entry has no predecessor on file, so it cannot be checked —
    // rotation legitimately drops what came before it.
    [Fact]
    public void Verify_CountsOnlyTheCheckableLinks()
    {
        var log = new AuditLog(_path);
        log.Append(Entry("alice", "POST /api/a"));
        log.Append(Entry("bob", "POST /api/b"));

        Assert.Equal(1, log.Verify().Verified);
    }

    [Fact]
    public void Verify_EmptyTrailIsOk()
    {
        var result = new AuditLog(_path).Verify();

        Assert.True(result.Ok);
        Assert.Equal(0, result.Entries);
    }

    [Fact]
    public void Verify_DetectsAnEditedEntry()
    {
        var log = new AuditLog(_path);
        log.Append(Entry("alice", "POST /api/a"));
        log.Append(Entry("mallory", "POST /api/docker/containers/db/remove"));
        log.Append(Entry("carol", "POST /api/c"));

        // Rewrite the middle line to hide who did it, keeping its hash.
        var lines = File.ReadAllLines(_path);
        lines[1] = lines[1].Replace("mallory", "someone");
        File.WriteAllLines(_path, lines);

        var result = log.Verify();

        Assert.False(result.Ok);
        Assert.Equal(1, result.FirstBrokenIndex);
    }

    [Fact]
    public void Verify_DetectsARemovedEntry()
    {
        var log = new AuditLog(_path);
        log.Append(Entry("alice", "POST /api/a"));
        log.Append(Entry("mallory", "POST /api/b"));
        log.Append(Entry("carol", "POST /api/c"));

        var lines = File.ReadAllLines(_path);
        File.WriteAllLines(_path, [lines[0], lines[2]]);

        Assert.False(new AuditLog(_path).Verify().Ok);
    }

    // The trail names who did what to which resource, so it must not land on a
    // world-readable inode.
    [Fact]
    public void Append_CreatesTheTrailOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix file modes only.
        }

        new AuditLog(_path).Append(Entry("alice", "POST /api/x"));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_path));
    }

    // A restarted process picks the chain back up rather than starting a new one.
    [Fact]
    public void Append_ResumesTheChainAcrossInstances()
    {
        new AuditLog(_path).Append(Entry("alice", "POST /api/a"));
        new AuditLog(_path).Append(Entry("bob", "POST /api/b"));

        Assert.True(new AuditLog(_path).Verify().Ok);
    }

    /// <summary>
    /// The trail is the one view that only grows, so it is paged. Pages must not
    /// overlap or skip: an entry seen on page one must not appear again on
    /// page two, which is what an off-by-one in the offset would produce.
    /// </summary>
    [Fact]
    public void ReadPage_WalksTheTrailWithoutRepeatingOrSkipping()
    {
        var log = new AuditLog(_path);
        for (var index = 0; index < 25; index++)
        {
            log.Append(Entry("alice", $"POST /api/{index}") with
            {
                Timestamp = DateTimeOffset.UnixEpoch.AddMinutes(index),
            });
        }

        var seen = new List<string>();
        for (var offset = 0; offset < 25; offset += 10)
        {
            var page = log.ReadPage(limit: 10, offset: offset);
            Assert.Equal(25, page.Total);
            seen.AddRange(page.Items.Select(entry => entry.Action));
        }

        Assert.Equal(25, seen.Count);
        Assert.Equal(25, seen.Distinct().Count());
        // Newest first, all the way through — the pages are one ordered sequence.
        Assert.Equal("POST /api/24", seen[0]);
        Assert.Equal("POST /api/0", seen[^1]);
    }

    /// <summary>
    /// Total counts what the filter matched, not what the page returned —
    /// otherwise the pager can only ever claim there is one page.
    /// </summary>
    [Fact]
    public void ReadPage_CountsTheWholeFilteredSetNotJustThePage()
    {
        var log = new AuditLog(_path);
        for (var index = 0; index < 12; index++)
        {
            log.Append(Entry(index % 2 == 0 ? "alice" : "bob", $"POST /api/{index}"));
        }

        var page = log.ReadPage(limit: 2, offset: 0, user: "alice");

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(6, page.Total);
        Assert.All(page.Items, entry => Assert.Equal("alice", entry.User));
    }

    /// <summary>An offset past the end is empty, not an error — the log rotates.</summary>
    [Fact]
    public void ReadPage_PastTheEndIsEmptyButStillReportsTheTotal()
    {
        var log = new AuditLog(_path);
        log.Append(Entry("alice", "POST /api/a"));

        var page = log.ReadPage(limit: 50, offset: 500);

        Assert.Empty(page.Items);
        Assert.Equal(1, page.Total);
    }
}
