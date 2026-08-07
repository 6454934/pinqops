namespace PinqOps;

/// <summary>
/// Resolves a self-hosted-runner registration token using the fixed fallback
/// chain: a pre-supplied token, then the authenticated gh CLI, then a personal
/// access token via the GitHub API, then a pasted token. The personal access
/// token is used once and never returned, logged, or persisted.
/// </summary>
public sealed class RegistrationTokenResolver
{
    private readonly GhCli _ghCli;
    private readonly IGitHubApiClient _gitHubApiClient;
    private readonly IPrompt _prompt;
    private readonly Action<string>? _log;

    /// <summary>
    /// The PAT pasted at <see cref="ResolveAsync"/>'s prompt, kept only so the
    /// removal-token step of the same run can use it. Never returned, logged or
    /// persisted, like the one passed in options.
    /// </summary>
    private string? _promptedPersonalAccessToken;

    public RegistrationTokenResolver(
        GhCli ghCli,
        IGitHubApiClient gitHubApiClient,
        IPrompt prompt,
        Action<string>? log = null)
    {
        _ghCli = ghCli ?? throw new ArgumentNullException(nameof(ghCli));
        _gitHubApiClient = gitHubApiClient ?? throw new ArgumentNullException(nameof(gitHubApiClient));
        _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
        _log = log;
    }

    public async Task<string> ResolveAsync(
        GitHubRepository repository,
        SetupOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(options);

        // 0. Pre-supplied registration token (scripted / non-interactive paste).
        if (!string.IsNullOrWhiteSpace(options.RegistrationToken))
        {
            _log?.Invoke("using the registration token supplied via --token/RUNNER_TOKEN");
            return options.RegistrationToken;
        }

        // 1. Authenticated gh CLI mints one automatically.
        var tokenFromGh = await TryResolveWithGhAsync(repository, options, cancellationToken).ConfigureAwait(false);
        if (tokenFromGh is not null)
        {
            return tokenFromGh;
        }

        // 2. Personal access token -> GitHub REST API.
        var personalAccessToken = ResolvePersonalAccessToken(options);
        if (!string.IsNullOrWhiteSpace(personalAccessToken))
        {
            _log?.Invoke("minting a registration token via the GitHub API");
            return await _gitHubApiClient
                .CreateRegistrationTokenAsync(repository, personalAccessToken, cancellationToken)
                .ConfigureAwait(false);
        }

        // 3. Paste a registration token obtained from the GitHub UI.
        if (!options.NonInteractive)
        {
            var pasted = _prompt.AskSecret(
                "Paste a runner registration token (repo -> Settings -> Actions -> Runners -> New self-hosted runner): ");
            if (!string.IsNullOrWhiteSpace(pasted))
            {
                return pasted.Trim();
            }
        }

        // 4. Nothing left to try.
        throw new InvalidOperationException(
            "No registration token available: gh is not authenticated and no --pat or --token was supplied. "
            + "Pass --token <registration-token> or --pat <github-pat>, or run pinqops setup interactively.");
    }

    /// <summary>
    /// Best-effort removal token for de-registering a leftover runner that is
    /// registered to <paramref name="oldRepository"/>. Uses the same silent
    /// sources as registration (gh CLI, then a supplied or already-prompted PAT)
    /// but never prompts anew: cleanup falls back to deleting local files when no
    /// token is available.
    /// </summary>
    public async Task<string?> TryResolveRemovalTokenAsync(
        GitHubRepository oldRepository,
        SetupOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(oldRepository);
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            if (options.UseGhCli
                && await _ghCli.IsAvailableAsync(cancellationToken).ConfigureAwait(false)
                && await _ghCli.IsAuthenticatedAsync(cancellationToken).ConfigureAwait(false))
            {
                return await _ghCli.CreateRemovalTokenAsync(oldRepository, cancellationToken).ConfigureAwait(false);
            }

            // The supplied PAT, or the one the user pasted at ResolveAsync's prompt
            // moments ago — which used to be forgotten here, so an interactive setup
            // silently degraded to force-deleting the old runner's local files and
            // left an orphaned offline entry on the old repository.
            var personalAccessToken = !string.IsNullOrWhiteSpace(options.PersonalAccessToken)
                ? options.PersonalAccessToken
                : _promptedPersonalAccessToken;
            if (!string.IsNullOrWhiteSpace(personalAccessToken))
            {
                return await _gitHubApiClient
                    .CreateRemovalTokenAsync(oldRepository, personalAccessToken, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (GitHubApiException exception)
        {
            _log?.Invoke($"could not mint a removal token for {oldRepository.Owner}/{oldRepository.Name}: {exception.Message}");
        }

        return null;
    }

    private async Task<string?> TryResolveWithGhAsync(
        GitHubRepository repository,
        SetupOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.UseGhCli)
        {
            return null;
        }

        if (!await _ghCli.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (!await _ghCli.IsAuthenticatedAsync(cancellationToken).ConfigureAwait(false))
        {
            _log?.Invoke("gh is installed but not logged in (run 'gh auth login'); falling back");
            return null;
        }

        _log?.Invoke("authenticated gh CLI detected — minting a registration token automatically");
        try
        {
            return await _ghCli.CreateRegistrationTokenAsync(repository, cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubApiException exception)
        {
            // gh being logged in says nothing about whether that account can
            // administer THIS repository. Letting the failure escape here ended
            // the whole chain — a perfectly valid --pat two steps down was never
            // tried, for a repo gh's account simply had no admin on.
            _log?.Invoke($"gh could not mint the token ({exception.Message}); falling back");
            return null;
        }
    }

    private string? ResolvePersonalAccessToken(SetupOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.PersonalAccessToken))
        {
            return options.PersonalAccessToken;
        }

        if (options.NonInteractive)
        {
            return null;
        }

        var answer = _prompt.AskSecret(
            "Paste a GitHub PAT (classic 'repo' scope, or fine-grained 'Administration: Read and write') "
            + "to mint a token automatically, or press Enter to paste a registration token instead: ");
        _promptedPersonalAccessToken = string.IsNullOrWhiteSpace(answer) ? null : answer.Trim();
        return _promptedPersonalAccessToken;
    }
}
