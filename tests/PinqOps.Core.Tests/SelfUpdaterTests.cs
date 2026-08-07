using PinqOps;
using PinqOps.Tests.Fakes;
using Xunit;

namespace PinqOps.Tests;

public class SelfUpdaterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pinqops-update-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>
    /// An updater standing on the platform the binaries are published for, so the
    /// download-and-verify behaviour under test is reached on any developer's
    /// machine rather than only on linux-x64. <see cref="RefusesAPlatformWithNoPublishedBinary"/>
    /// covers the guard itself.
    /// </summary>
    private static SelfUpdater Updater(IFileDownloader downloader, Action<string> log) =>
        new(downloader, log, platformHasAPublishedBinary: true);

    [Fact]
    public async Task RefusesAPlatformWithNoPublishedBinary()
    {
        var target = Path.Combine(_dir, "pinqops");
        File.WriteAllText(target, "old-binary");
        var downloader = new FakeFileDownloader();
        var log = new List<string>();

        var replaced = await new SelfUpdater(downloader, log.Add, platformHasAPublishedBinary: false)
            .UpdateAsync("pinqops", target);

        Assert.Null(replaced);
        Assert.Equal("old-binary", File.ReadAllText(target));
        // It refuses before reaching for the network at all.
        Assert.Empty(downloader.Downloads);
        Assert.Contains(log, line => line.Contains("only supported on linux-x64", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_ReplacesTheTargetWithTheDownloadedAsset()
    {
        var target = Path.Combine(_dir, "pinqops");
        File.WriteAllText(target, "old-binary");
        var downloader = new FakeFileDownloader();
        var log = new List<string>();

        var replaced = await Updater(downloader, log.Add).UpdateAsync("pinqops", target);

        Assert.Equal(target, replaced);
        Assert.Equal("fake-archive", File.ReadAllText(target));

        // Fetched the 'latest' asset for the requested name...
        var download = Assert.Single(downloader.AssetDownloads);
        Assert.Equal($"{SelfUpdater.ReleaseBaseUrl}/pinqops", download.Url);
        // ...beside the target (same volume, for an atomic rename)...
        Assert.Equal(_dir, Path.GetDirectoryName(download.DestinationPath));
        // ...and the temp file was renamed away, not left behind.
        Assert.False(File.Exists(download.DestinationPath));
    }

    [Fact]
    public async Task Update_MarksTheReplacedBinaryExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix file modes are a no-op on Windows.
        }

        var target = Path.Combine(_dir, "pinqops-ui");
        File.WriteAllText(target, "old");
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite); // not executable

        await new SelfUpdater(new FakeFileDownloader(), _ => { }).UpdateAsync("pinqops-ui", target);

        Assert.True(File.GetUnixFileMode(target).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public async Task Update_MissingDownload_KeepsTheOriginal()
    {
        var target = Path.Combine(_dir, "pinqops");
        File.WriteAllText(target, "old-binary");
        // createFile: false records the request but writes nothing to disk.
        var downloader = new FakeFileDownloader(createFile: false);
        var log = new List<string>();

        var replaced = await Updater(downloader, log.Add).UpdateAsync("pinqops", target);

        Assert.Null(replaced);
        Assert.Equal("old-binary", File.ReadAllText(target));
        Assert.Contains(log, line => line.Contains("missing or empty"));
    }
}

/// <summary>
/// The updater swaps in a binary that runs as root and talks to the Docker
/// daemon, so verification fails closed: anything it cannot confirm leaves the
/// current binary in place.
/// </summary>
public class SelfUpdaterVerificationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pinqops-update-verify-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Target(string name = "pinqops")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "old-binary");
        return path;
    }

    /// <summary>
    /// Standing on the published platform, so these refusals are reached for the
    /// reason under test. Without it they hold on a Windows host for a reason
    /// that has nothing to do with verification — the updater declines before it
    /// downloads anything, and "the original was kept" passes vacuously.
    /// </summary>
    private static SelfUpdater Updater(IFileDownloader downloader, Action<string> log) =>
        new(downloader, log, platformHasAPublishedBinary: true);

    [Fact]
    public async Task FetchesTheManifestBeforeSwappingTheBinaryIn()
    {
        var downloader = new FakeFileDownloader();

        await Updater(downloader, _ => { }).UpdateAsync("pinqops", Target());

        Assert.Contains(
            downloader.Downloads,
            download => download.Url == $"{SelfUpdater.ReleaseBaseUrl}/{SelfUpdater.ChecksumFileName}");
    }

    [Fact]
    public async Task ChecksumMismatch_KeepsTheOriginal()
    {
        var target = Target();
        // A manifest for different bytes than the ones that were downloaded.
        var downloader = new FakeFileDownloader(manifestOverride: FakeFileDownloader.ManifestFor("something-else"));
        var log = new List<string>();

        var replaced = await Updater(downloader, log.Add).UpdateAsync("pinqops", target);

        Assert.Null(replaced);
        Assert.Equal("old-binary", File.ReadAllText(target));
        Assert.Contains(log, line => line.Contains("checksum mismatch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ManifestWithoutThisAsset_KeepsTheOriginal()
    {
        var target = Target();
        var downloader = new FakeFileDownloader(
            manifestOverride: $"{new string('a', 64)}  some-other-asset\n");
        var log = new List<string>();

        Assert.Null(await Updater(downloader, log.Add).UpdateAsync("pinqops", target));
        Assert.Equal("old-binary", File.ReadAllText(target));
        Assert.Contains(log, line => line.Contains("does not list", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UnusableManifest_KeepsTheOriginal()
    {
        var target = Target();
        var downloader = new FakeFileDownloader(manifestOverride: "<html>404</html>");
        var log = new List<string>();

        Assert.Null(await Updater(downloader, log.Add).UpdateAsync("pinqops", target));
        Assert.Equal("old-binary", File.ReadAllText(target));
    }

    // No temp files left behind when verification rejects the download.
    [Fact]
    public async Task RejectedDownload_LeavesNoTempFiles()
    {
        var target = Target();
        var downloader = new FakeFileDownloader(manifestOverride: FakeFileDownloader.ManifestFor("something-else"));

        await Updater(downloader, _ => { }).UpdateAsync("pinqops", target);

        Assert.Equal([target], Directory.GetFiles(_dir));
    }
}
