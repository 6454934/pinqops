namespace PinqOps;

/// <summary>
/// Thrown when the GitHub REST API rejects a registration-token request. Carries
/// the HTTP status so the CLI can render an actionable, cause-specific message.
/// </summary>
public sealed class GitHubApiException : Exception
{
    public GitHubApiException(int statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}
