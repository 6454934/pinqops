namespace PinqOps;

/// <summary>
/// Downloads a file to a local path. Abstracted so the runner installer can be
/// tested without network access.
/// </summary>
public interface IFileDownloader
{
    Task DownloadAsync(string url, string destinationPath, CancellationToken cancellationToken = default);
}
