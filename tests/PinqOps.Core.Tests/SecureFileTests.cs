using System.Diagnostics;
using Xunit;

namespace PinqOps.Tests;

/// <summary>
/// Every config, credential and state file in pinqops is written through this
/// one primitive, and every one of them is read by a dashboard request that can
/// arrive while a write is in flight. The write is the side that must survive
/// that overlap: a reader losing a race just re-reads, but a writer losing it
/// throws the operator's change away.
/// </summary>
public class SecureFileTests : IDisposable
{
    /// <summary>Long enough that the writer definitely meets the open handle, short enough to stay a fast test.</summary>
    private const int ReaderHoldMilliseconds = 150;

    private const int WriterMustGiveUpWithinSeconds = 5;

    /// <summary>Enough overlap to hit the commit window reliably, few enough to stay fast.</summary>
    private const int ContendedIterations = 200;

    private readonly string _directory = Directory.CreateTempSubdirectory("pinqops-secure-file-").FullName;

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Path_(string name) => Path.Combine(_directory, name);

    [Fact]
    public void WhatWasWrittenComesBack()
    {
        var path = Path_("config.json");

        SecureFile.WriteAllText(path, "{}");

        Assert.Equal("{}", File.ReadAllText(path));
    }

    [Fact]
    public void AnExistingFileIsReplaced()
    {
        var path = Path_("config.json");
        SecureFile.WriteAllText(path, "first");

        SecureFile.WriteAllText(path, "second");

        Assert.Equal("second", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_directory));
    }

    /// <summary>
    /// The replacing rename needs delete access to the destination, and a reader
    /// that opened it the way <see cref="File.ReadAllText"/> does — shared for
    /// reading only — does not grant that. A dashboard GET landing in the
    /// microseconds a write needs is not an exotic interleaving; it is what the
    /// browser's auto-refresh does all day. The write has to wait the reader out
    /// rather than fail and discard the change.
    /// </summary>
    [Fact]
    public async Task AWriteWaitsOutAReaderHoldingTheFileOpen()
    {
        var path = Path_("config.json");
        SecureFile.WriteAllText(path, "first");

        using var opened = new ManualResetEventSlim();
        var reader = Task.Run(() =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            opened.Set();
            Thread.Sleep(ReaderHoldMilliseconds);
        });

        opened.Wait();
        SecureFile.WriteAllText(path, "second");

        await reader;
        Assert.Equal("second", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_directory));
    }

    /// <summary>
    /// Waiting a reader out must stay bounded. A reader that never lets go is a
    /// stuck process, not a race, and a config write that blocks on it forever
    /// would take the request thread with it.
    /// </summary>
    [Fact]
    public void AReaderThatNeverLetsGoDoesNotBlockTheWriterForever()
    {
        var path = Path_("config.json");
        SecureFile.WriteAllText(path, "first");

        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var elapsed = Stopwatch.StartNew();
        try
        {
            SecureFile.WriteAllText(path, "second");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Expected where a held read handle blocks the rename outright.
        }

        elapsed.Stop();
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(WriterMustGiveUpWithinSeconds),
            $"the write took {elapsed.Elapsed} before giving up");
    }

    /// <summary>
    /// The other half of the same race. A read that starts while a write is
    /// committing must not fail: it escapes <c>Load</c>, which only catches
    /// <see cref="System.Text.Json.JsonException"/>, and takes out whichever
    /// request or background tick called it.
    /// </summary>
    [Fact]
    public void AReadSurvivesAWriteCommittingUnderneathIt()
    {
        var path = Path_("config.json");
        SecureFile.WriteAllText(path, "first");

        var failures = 0;
        Parallel.Invoke(
            () =>
            {
                for (var i = 0; i < ContendedIterations; i++)
                {
                    SecureFile.WriteAllText(path, $"write-{i}");
                }
            },
            () =>
            {
                for (var i = 0; i < ContendedIterations; i++)
                {
                    if (SecureFile.ReadAllText(path).Length == 0)
                    {
                        Interlocked.Increment(ref failures);
                    }
                }
            });

        Assert.Equal(0, failures);
    }

    /// <summary>A read of something that was never written still fails immediately.</summary>
    [Fact]
    public void AMissingFileIsNotRetried()
    {
        var elapsed = Stopwatch.StartNew();

        Assert.Throws<FileNotFoundException>(() => SecureFile.ReadAllText(Path_("absent.json")));

        elapsed.Stop();
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1), $"a missing file took {elapsed.Elapsed}");
    }

    /// <summary>A write that could not land must not leave its temp file behind.</summary>
    [Fact]
    public void AFailedWriteLeavesNoTempFile()
    {
        var path = Path_("config.json");
        SecureFile.WriteAllText(path, "first");

        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                SecureFile.WriteAllText(path, "second");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The point of the test is what is on disk afterwards.
            }
        }

        Assert.Equal([path], Directory.GetFiles(_directory));
    }

    [Fact]
    public void PreservingInodeUpdatesAnExistingFile()
    {
        var path = Path_("Caddyfile");
        SecureFile.WriteAllText(path, "first", ownerOnly: false);

        SecureFile.WriteAllTextPreservingInode(path, "second", ownerOnly: false);

        Assert.Equal("second", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_directory));
    }

    /// <summary>
    /// Docker's single-file bind mount pins the inode that existed at container
    /// start. A rename-replace would leave the container reading stale bytes.
    /// </summary>
    [Fact]
    public void PreservingInodeKeepsTheSameInodeOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var path = Path_("Caddyfile");
        SecureFile.WriteAllText(path, "first", ownerOnly: false);
        var before = LinuxInode(path);

        SecureFile.WriteAllTextPreservingInode(path, "second with a site block", ownerOnly: false);

        Assert.Equal("second with a site block", File.ReadAllText(path));
        Assert.Equal(before, LinuxInode(path));
    }

    private static string LinuxInode(string path)
    {
        var start = new ProcessStartInfo("stat", ["-c", "%i", path])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("stat failed to start");
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return output;
    }
}
