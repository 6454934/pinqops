namespace PinqOps;

/// <summary>Default <see cref="IFileDownloader"/> backed by <see cref="HttpClient"/>.</summary>
public sealed class HttpFileDownloader : IFileDownloader, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public HttpFileDownloader(HttpClient? httpClient = null)
    {
        // HttpClient.Timeout bounds the whole operation including reading the body,
        // and the default is 100 seconds. The file this exists to fetch is the
        // ~180 MB Actions runner tarball, which needs a sustained ~15 Mbit/s to
        // land inside that — on anything slower every install failed at the same
        // point with "the request was canceled". Cancellation is the bound instead:
        // the caller's token carries the real budget.
        _httpClient = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _ownsClient = httpClient is null;
    }

    public async Task DownloadAsync(string url, string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        try
        {
            using var response = await _httpClient
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var destination = File.Create(destinationPath))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // A partial file would be extracted by a retry as though it were the
            // whole archive, so an interrupted download leaves nothing behind.
            TryDelete(destinationPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: the download already failed, and the caller's error is
            // the one worth reporting.
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
