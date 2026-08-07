using System.Net.Http;
using System.Security.Cryptography;

namespace PinqOps;

/// <summary>
/// Replaces the running pinqops binary in place with the latest published
/// release asset, so operators can update without re-running the curl + install
/// steps from the README. The self-contained linux-x64 asset is downloaded next
/// to the current binary and atomically renamed over it, and the new binary
/// takes effect on the next run.
///
/// <para>
/// <b>The calling process is not fully functional afterwards.</b> The published
/// binaries are self-contained single files, so the framework assemblies are
/// read out of the executable on demand — and once it has been replaced, the
/// ones not already loaded cannot be loaded any more. The first version of the
/// update command hit exactly this: it replaced the binary and then tried to
/// restart the service, which needs <c>System.Diagnostics.Process</c>, and died
/// with a <c>FileNotFoundException</c> for an assembly that ships inside the
/// binary it had just installed.
/// </para>
/// <para>
/// So anything the caller still needs after this returns must already be in
/// memory before it is called — see <c>ProcessRunner.Preload</c> — and the work
/// after the swap should be small, guarded, and never required for the update
/// itself to count as done.
/// </para>
/// </summary>
public sealed class SelfUpdater
{
    /// <summary>Where the release assets live; "latest" always redirects to the newest release.</summary>
    public const string ReleaseBaseUrl = "https://github.com/pinqponq/pinqops/releases/latest/download";

    /// <summary>
    /// The checksum manifest published alongside the binaries, in the format
    /// <c>sha256sum</c> writes (<c>&lt;hex&gt;  &lt;name&gt;</c>).
    /// </summary>
    public const string ChecksumFileName = "SHA256SUMS";

    private readonly IFileDownloader _downloader;
    private readonly Action<string> _log;
    private readonly bool _platformHasAPublishedBinary;

    public SelfUpdater(IFileDownloader downloader, Action<string> log)
        : this(downloader, log, OperatingSystem.IsLinux())
    {
    }

    /// <summary>
    /// Lets a test say which platform it is standing in for.
    ///
    /// Everything below the platform guard — the download, the checksum
    /// verification, the fail-closed refusals, the atomic swap — behaves the same
    /// everywhere, and it is the part worth testing, because it decides whether a
    /// binary that runs as root and talks to the Docker daemon gets installed.
    /// Reaching it only from the published platform would mean those tests never
    /// run on the machine anyone actually develops on.
    /// </summary>
    internal SelfUpdater(IFileDownloader downloader, Action<string> log, bool platformHasAPublishedBinary)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _platformHasAPublishedBinary = platformHasAPublishedBinary;
    }

    /// <summary>
    /// Downloads the latest <paramref name="assetName"/> release asset and
    /// replaces the target executable with it. <paramref name="targetPath"/>
    /// defaults to the running binary (<see cref="Environment.ProcessPath"/>);
    /// tests pass an explicit path. Returns the path that was replaced on
    /// success, or null on a handled failure (already logged).
    /// </summary>
    public async Task<string?> UpdateAsync(string assetName, string? targetPath = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetName);

        // Only linux-x64 self-contained binaries are published, so a swap on any
        // other OS would install something that can't run here.
        if (!_platformHasAPublishedBinary)
        {
            _log("error: self-update is only supported on linux-x64 (the only published binary).");
            return null;
        }

        var target = targetPath ?? Environment.ProcessPath;
        if (string.IsNullOrEmpty(target))
        {
            _log("error: could not determine the path of the running binary.");
            return null;
        }

        // Guard the 'dotnet run' case only when updating the running binary — an
        // explicit target is a real file path the caller chose.
        if (targetPath is null && Path.GetFileName(target) is "dotnet" or "dotnet.exe")
        {
            _log("error: run 'update' from the installed binary, not via 'dotnet run'.");
            return null;
        }

        target = Path.GetFullPath(target);
        var directory = Path.GetDirectoryName(target)!;
        var url = $"{ReleaseBaseUrl}/{assetName}";

        // Download beside the target so the final rename stays on one filesystem
        // (rename is only atomic within a volume) and a half-finished download can
        // never clobber the working binary.
        var temp = Path.Combine(directory, $".{assetName}.update-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}");

        _log($"downloading {url}");
        try
        {
            await _downloader.DownloadAsync(url, temp, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            _log($"error: download failed: {exception.Message}");
            if (exception is UnauthorizedAccessException)
            {
                _log($"cannot write to {directory} — re-run with sudo.");
            }

            TryDelete(temp);
            return null;
        }

        try
        {
            var downloaded = new FileInfo(temp);
            if (!downloaded.Exists || downloaded.Length == 0)
            {
                _log("error: the downloaded binary is missing or empty; keeping the current one.");
                TryDelete(temp);
                return null;
            }

            // This binary is about to replace one that runs as root and talks to
            // the Docker daemon, so "it downloaded" is not enough — it has to be
            // the artifact the release published. Verification fails closed: a
            // missing or non-matching manifest keeps the current binary.
            if (!await VerifyChecksumAsync(temp, assetName, directory, cancellationToken).ConfigureAwait(false))
            {
                TryDelete(temp);
                return null;
            }

            if (!OperatingSystem.IsWindows())
            {
                // rwxr-xr-x — the same mode the install steps set with chmod +x.
                File.SetUnixFileMode(
                    temp,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            // Atomic replace: the running process holds the old inode open, so
            // swapping the path out from under it is safe.
            File.Move(temp, target, overwrite: true);
        }
        catch (UnauthorizedAccessException)
        {
            _log($"error: cannot replace {target} — re-run with sudo.");
            TryDelete(temp);
            return null;
        }
        catch (IOException exception)
        {
            _log($"error: could not replace {target}: {exception.Message}");
            TryDelete(temp);
            return null;
        }

        _log($"updated {target}");
        return target;
    }

    /// <summary>
    /// Checks the downloaded file against the release's <c>SHA256SUMS</c>.
    ///
    /// The manifest comes from the same host as the binary, so this is not a
    /// signature — it does not defend against a compromised release. What it does
    /// catch is a truncated or corrupted download, a stale CDN copy, and an asset
    /// that was swapped without the manifest being regenerated. Signing is the
    /// next step; the manifest is what a signature would attest to.
    /// </summary>
    private async Task<bool> VerifyChecksumAsync(
        string downloadedPath, string assetName, string directory, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(
            directory, $".{ChecksumFileName}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}");

        try
        {
            await _downloader
                .DownloadAsync($"{ReleaseBaseUrl}/{ChecksumFileName}", manifestPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            _log($"error: could not fetch {ChecksumFileName} to verify the download ({exception.Message}).");
            _log("keeping the current binary. Re-run once the release publishes a checksum manifest.");
            TryDelete(manifestPath);
            return false;
        }

        string? expected;
        try
        {
            expected = FindChecksum(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false), assetName);
        }
        finally
        {
            TryDelete(manifestPath);
        }

        if (expected is null)
        {
            _log($"error: {ChecksumFileName} does not list {assetName}; keeping the current binary.");
            return false;
        }

        string actual;
        await using (var stream = File.OpenRead(downloadedPath))
        {
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }

        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(actual), System.Text.Encoding.ASCII.GetBytes(expected)))
        {
            _log($"error: checksum mismatch for {assetName}.");
            _log($"  expected {expected}");
            _log($"  actual   {actual}");
            _log("keeping the current binary.");
            return false;
        }

        _log($"checksum verified ({actual[..16]}…)");
        return true;
    }

    /// <summary>
    /// The hex digest listed for <paramref name="assetName"/>, or null. Accepts
    /// the <c>sha256sum</c> layout, including the <c>*</c> binary-mode marker.
    /// </summary>
    internal static string? FindChecksum(string manifest, string assetName)
    {
        foreach (var line in manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var name = parts[1].TrimStart('*');
            if (string.Equals(name, assetName, StringComparison.Ordinal)
                && parts[0].Length == 64
                && parts[0].All(Uri.IsHexDigit))
            {
                return parts[0].ToLowerInvariant();
            }
        }

        return null;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: the leftover temp file is harmless and hidden (dot-prefixed).
            _log($"note: could not clean up {path}: {exception.Message}");
        }
    }
}
