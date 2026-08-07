namespace PinqOps;

/// <summary>
/// Mints short-lived self-hosted-runner registration and removal tokens from a
/// GitHub personal access token. Abstracted so the token flow can be tested
/// without network access.
/// </summary>
public interface IGitHubApiClient
{
    Task<string> CreateRegistrationTokenAsync(
        GitHubRepository repository,
        string personalAccessToken,
        CancellationToken cancellationToken = default);

    Task<string> CreateRemovalTokenAsync(
        GitHubRepository repository,
        string personalAccessToken,
        CancellationToken cancellationToken = default);
}
