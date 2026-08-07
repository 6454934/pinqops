namespace PinqOps;

/// <summary>
/// Thin wrapper over the <c>gh</c> CLI for the setup wizard's first token
/// branch: detect that gh is installed and authenticated, then mint a runner
/// registration token using gh's own stored credentials.
/// </summary>
public sealed class GhCli
{
    private const string Executable = "gh";

    private readonly IProcessRunner _processRunner;
    private readonly Action<string>? _log;

    public GhCli(IProcessRunner processRunner, Action<string>? log = null)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _log = log;
    }

    /// <summary>True when the gh CLI is installed and on PATH.</summary>
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        SucceedsAsync(new[] { "--version" }, cancellationToken);

    /// <summary>True when gh is logged in (<c>gh auth status</c> exits 0).</summary>
    public Task<bool> IsAuthenticatedAsync(CancellationToken cancellationToken = default) =>
        SucceedsAsync(new[] { "auth", "status" }, cancellationToken);

    /// <summary>Mints a registration token via <c>gh api</c>. Throws on failure.</summary>
    public Task<string> CreateRegistrationTokenAsync(
        GitHubRepository repository,
        CancellationToken cancellationToken = default) =>
        CreateRunnerTokenAsync(repository, "registration-token", cancellationToken);

    /// <summary>Mints a runner removal token via <c>gh api</c>. Throws on failure.</summary>
    public Task<string> CreateRemovalTokenAsync(
        GitHubRepository repository,
        CancellationToken cancellationToken = default) =>
        CreateRunnerTokenAsync(repository, "remove-token", cancellationToken);

    private async Task<string> CreateRunnerTokenAsync(
        GitHubRepository repository,
        string kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);

        var arguments = new List<string>
        {
            "api",
            "-X", "POST",
            $"repos/{repository.Owner}/{repository.Name}/actions/runners/{kind}",
            "--jq", ".token",
        };

        // Without this, gh targets its default host whatever the repository URL
        // said — an enterprise repo's token was minted against github.com, and in
        // the best case that is a 404; in the worst it registers the runner to an
        // identically-named public repository.
        if (!string.Equals(repository.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("--hostname");
            arguments.Add(repository.Host);
        }

        var result = await _processRunner
            .RunAsync(Executable, arguments, workingDirectory: null, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new GitHubApiException(0, $"gh could not mint a {kind}: {result.StandardError.Trim()}");
        }

        return result.StandardOutput.Trim();
    }

    private async Task<bool> SucceedsAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processRunner
                .RunAsync(Executable, arguments, workingDirectory: null, cancellationToken)
                .ConfigureAwait(false);
            return result.Succeeded;
        }
        catch (Exception exception)
        {
            // gh is not installed: Process.Start throws (Win32Exception) rather
            // than returning an exit code. A launch failure means "gh unavailable".
            _log?.Invoke($"gh not available: {exception.Message}");
            return false;
        }
    }
}
