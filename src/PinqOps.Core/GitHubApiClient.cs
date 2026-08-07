using System.Net.Http.Headers;
using System.Text.Json;

namespace PinqOps;

/// <summary>
/// Default <see cref="IGitHubApiClient"/> backed by <see cref="HttpClient"/>. It
/// POSTs the repository's registration-token endpoint with the personal access
/// token in the Authorization header only — the token is never placed in a URL,
/// query string, or log.
/// </summary>
public sealed class GitHubApiClient : IGitHubApiClient, IDisposable
{
    private const string ApiVersion = "2022-11-28";
    private const string ProductName = "pinqops";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public GitHubApiClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsClient = httpClient is null;
    }

    public Task<string> CreateRegistrationTokenAsync(
        GitHubRepository repository,
        string personalAccessToken,
        CancellationToken cancellationToken = default) =>
        CreateRunnerTokenAsync(repository, personalAccessToken, remove: false, cancellationToken);

    public Task<string> CreateRemovalTokenAsync(
        GitHubRepository repository,
        string personalAccessToken,
        CancellationToken cancellationToken = default) =>
        CreateRunnerTokenAsync(repository, personalAccessToken, remove: true, cancellationToken);

    private async Task<string> CreateRunnerTokenAsync(
        GitHubRepository repository,
        string personalAccessToken,
        bool remove,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(personalAccessToken);

        var url = remove ? repository.RemovalTokenUrl : repository.RegistrationTokenUrl;
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", personalAccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd(ProductName);
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new GitHubApiException((int)response.StatusCode, DescribeFailure(response.StatusCode, body));
        }

        return ReadToken(body);
    }

    private static string ReadToken(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("token", out var tokenElement)
            && tokenElement.ValueKind == JsonValueKind.String)
        {
            var token = tokenElement.GetString();
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }
        }

        throw new GitHubApiException(200, "GitHub returned no runner token in its response.");
    }

    private static string DescribeFailure(System.Net.HttpStatusCode statusCode, string body)
    {
        var status = (int)statusCode;
        var hint = status switch
        {
            401 => "the PAT is missing, invalid, or expired.",
            403 => "you must be a repository admin; a classic PAT needs the 'repo' scope, a fine-grained PAT needs "
                   + "'Administration: Read and write'. If the org enforces SSO, authorize the PAT for the org.",
            404 => "the repository was not found or the token cannot see it — check the owner/repo and the token's access.",
            _ => "the GitHub API rejected the request.",
        };

        var apiMessage = TryReadMessage(body);
        var suffix = string.IsNullOrWhiteSpace(apiMessage) ? string.Empty : $" GitHub says: {apiMessage}.";
        return $"GitHub rejected the runner-token request ({status}): {hint}{suffix}";
    }

    private static string? TryReadMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("message", out var messageElement)
                   && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
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
