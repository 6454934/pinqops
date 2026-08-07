using System.Text;
using PinqOps.Alerts;
using Xunit;

namespace PinqOps.Tests.Alerts;

/// <summary>
/// Reading a rotating log without loading it.
///
/// <para>The newest lines are at the end of the file, so answering "the last twenty"
/// by reading the whole archive and reversing it costs the archive to produce a few
/// kilobytes — which is what made one log search allocate every collected file before
/// it looked at the first line. Reading backwards in blocks is what makes a bounded
/// read bounded, and it has to give exactly the same lines in exactly the same order
/// as the version that was correct and expensive.</para>
/// </summary>
public class RotatingJsonLogStreamTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("pinqops-stream-").FullName;

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private RotatingJsonLog Log(long maxBytes = 0, int generations = 3) =>
        new(Path.Combine(_directory, "log.jsonl"), generations, maxBytes);

    [Fact]
    public void AnEmptyArchiveStreamsNothing() =>
        Assert.Empty(Log().StreamLines(oldestFirst: false));

    [Fact]
    public void TheLinesAreTheSameOnesReadWholeInBothDirections()
    {
        var log = Log();
        foreach (var index in Enumerable.Range(0, 50))
        {
            log.Append($"line {index}");
        }

        Assert.Equal(log.ReadLines(oldestFirst: true), log.StreamLines(oldestFirst: true));
        Assert.Equal(log.ReadLines(oldestFirst: false), log.StreamLines(oldestFirst: false));
    }

    /// <summary>
    /// Across generations too, which is where the order is easiest to get wrong: the
    /// live file is newest and the highest-numbered generation is oldest.
    /// </summary>
    [Fact]
    public void RotationDoesNotChangeTheOrder()
    {
        // Small enough that appending rotates repeatedly.
        var log = Log(maxBytes: 200);
        foreach (var index in Enumerable.Range(0, 60))
        {
            log.Append($"line {index}");
        }

        Assert.Equal(log.ReadLines(oldestFirst: false), log.StreamLines(oldestFirst: false));
        Assert.Equal(log.ReadLines(oldestFirst: true), log.StreamLines(oldestFirst: true));
    }

    /// <summary>
    /// A block boundary falls wherever it falls. Decoding each side of it separately
    /// would cut a multi-byte character in half and turn text into replacement marks
    /// — so the split is on the newline byte, which never appears inside a UTF-8
    /// sequence, and each line is decoded whole.
    /// </summary>
    [Fact]
    public void TextSurvivesABlockBoundaryFallingInsideACharacter()
    {
        var path = Path.Combine(_directory, "log.jsonl");
        var lines = new List<string>();

        // Filler either side of the 64 KB boundary the reader reads in, with
        // multi-byte characters dense enough that one has to straddle it.
        for (var index = 0; index < 4_000; index++)
        {
            lines.Add($"çğüşöİ-{index}-ağırlıklı ölçüm değeri şükrü");
        }

        File.WriteAllText(path, string.Join('\n', lines) + "\n", new UTF8Encoding(false));

        var streamed = Log().StreamLines(oldestFirst: false).ToList();

        Assert.Equal(lines.Count, streamed.Count);
        Assert.Equal(lines[^1], streamed[0]);
        Assert.Equal(lines[0], streamed[^1]);
        Assert.DoesNotContain(streamed, line => line.Contains('�'));
    }

    /// <summary>A file whose last line has no newline after it still yields that line.</summary>
    [Fact]
    public void AFileWithNoTrailingNewlineKeepsItsLastLine()
    {
        File.WriteAllText(Path.Combine(_directory, "log.jsonl"), "first\nsecond\nthird");

        Assert.Equal(["third", "second", "first"], Log().StreamLines(oldestFirst: false));
    }

    /// <summary>Blank lines are not lines — the same rule the whole-file read applies.</summary>
    [Fact]
    public void BlankLinesAreSkipped()
    {
        File.WriteAllText(Path.Combine(_directory, "log.jsonl"), "first\n\n\nsecond\n");

        Assert.Equal(["second", "first"], Log().StreamLines(oldestFirst: false));
    }

    /// <summary>
    /// The point of the whole thing: stopping after a few lines reads a few lines'
    /// worth, not the file's worth.
    /// </summary>
    [Fact]
    public void StoppingEarlyDoesNotReadTheWholeFile()
    {
        // Written straight to the file rather than appended a line at a time: what
        // is under test is the read, and Append opens the file per line.
        var padding = new string('x', 200);
        using (var writer = new StreamWriter(Path.Combine(_directory, "log.jsonl")))
        {
            foreach (var index in Enumerable.Range(0, 100_000))
            {
                writer.WriteLine($"line {index} {padding}");
            }
        }

        var log = Log();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var taken = log.StreamLines(oldestFirst: false).Take(3).ToList();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(3, taken.Count);
        Assert.StartsWith("line 99999 ", taken[0], StringComparison.Ordinal);
        Assert.True(
            allocated < 1024 * 1024,
            $"reading three lines allocated {allocated / 1024} KB, so the file was read whole");
    }
}
