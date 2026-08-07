using System.Security.Cryptography;
using System.Text;

namespace PinqOps.Tests.Fakes;

/// <summary>Records download requests and optionally writes a placeholder file.</summary>
public sealed class FakeFileDownloader : IFileDownloader
{
    private readonly bool _createFile;
    private readonly string _content;
    private readonly string? _manifestOverride;

    /// <param name="manifestOverride">
    /// Serve this instead of a manifest matching <paramref name="content"/>, to
    /// exercise the mismatch path.
    /// </param>
    public FakeFileDownloader(bool createFile = true, string content = "fake-archive", string? manifestOverride = null)
    {
        _createFile = createFile;
        _content = content;
        _manifestOverride = manifestOverride;
    }

    public List<(string Url, string DestinationPath)> Downloads { get; } = new();

    /// <summary>The asset downloads, i.e. everything but the checksum manifest.</summary>
    public IReadOnlyList<(string Url, string DestinationPath)> AssetDownloads =>
        [.. Downloads.Where(download => !IsManifest(download.Url))];

    public Task DownloadAsync(string url, string destinationPath, CancellationToken cancellationToken = default)
    {
        Downloads.Add((url, destinationPath));
        if (!_createFile)
        {
            return Task.CompletedTask;
        }

        // The updater verifies the download against the release's checksum
        // manifest before swapping a binary in, so the fake has to serve one.
        File.WriteAllText(
            destinationPath,
            IsManifest(url) ? _manifestOverride ?? ManifestFor(_content) : _content);

        return Task.CompletedTask;
    }

    private static bool IsManifest(string url) =>
        url.EndsWith(SelfUpdater.ChecksumFileName, StringComparison.Ordinal);

    /// <summary>A manifest listing both published assets with the digest of <paramref name="content"/>.</summary>
    public static string ManifestFor(string content)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        return $"{digest}  pinqops\n{digest}  pinqops-ui\n";
    }
}
