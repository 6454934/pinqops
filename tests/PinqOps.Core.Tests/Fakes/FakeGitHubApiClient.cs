namespace PinqOps.Tests.Fakes;

/// <summary>Records mint calls and returns a canned registration token.</summary>
public sealed class FakeGitHubApiClient : IGitHubApiClient
{
    private readonly string _token;

    public FakeGitHubApiClient(string token = "api-registration-token")
    {
        _token = token;
    }

    public List<(GitHubRepository Repository, string PersonalAccessToken)> Calls { get; } = new();

    public List<(GitHubRepository Repository, string PersonalAccessToken)> RemovalCalls { get; } = new();

    public Task<string> CreateRegistrationTokenAsync(
        GitHubRepository repository,
        string personalAccessToken,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((repository, personalAccessToken));
        return Task.FromResult(_token);
    }

    public Task<string> CreateRemovalTokenAsync(
        GitHubRepository repository,
        string personalAccessToken,
        CancellationToken cancellationToken = default)
    {
        RemovalCalls.Add((repository, personalAccessToken));
        return Task.FromResult("api-removal-token");
    }
}
