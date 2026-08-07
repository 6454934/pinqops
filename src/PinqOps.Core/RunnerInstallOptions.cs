namespace PinqOps;

/// <summary>
/// Options for installing a GitHub Actions self-hosted runner on the production
/// server. The runner connects to GitHub outbound-only, so the server needs no
/// inbound port.
/// </summary>
public sealed record RunnerInstallOptions
{
    public const string DefaultLabels = "pinqops-prod";
    public const string DefaultRunnerVersion = "2.319.1";
    public const string DefaultInstallDirectory = "/opt/actions-runner";

    public required string RepositoryUrl { get; init; }
    public required string RegistrationToken { get; init; }

    /// <summary>
    /// Optional short-lived token used to de-register a runner already present
    /// in <see cref="InstallDirectory"/> before configuring the new one. When
    /// absent, stale registration files are force-deleted instead (the orphaned
    /// GitHub-side entry goes offline and is purged by GitHub).
    /// </summary>
    public string? RemovalToken { get; init; }
    public string Labels { get; init; } = DefaultLabels;
    public string RunnerName { get; init; } = $"{Environment.MachineName}-pinqops";
    public string RunnerVersion { get; init; } = DefaultRunnerVersion;
    public string InstallDirectory { get; init; } = DefaultInstallDirectory;

    /// <summary>
    /// Validates inputs and returns options with defaults applied. Throws when a
    /// required value is missing (fail fast).
    /// </summary>
    public static RunnerInstallOptions Create(
        string? repositoryUrl,
        string? registrationToken,
        string? labels = null,
        string? runnerName = null,
        string? runnerVersion = null,
        string? installDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            throw new ArgumentException("Repository URL is required.", nameof(repositoryUrl));
        }

        if (string.IsNullOrWhiteSpace(registrationToken))
        {
            throw new ArgumentException("Registration token is required.", nameof(registrationToken));
        }

        return new RunnerInstallOptions
        {
            RepositoryUrl = repositoryUrl,
            RegistrationToken = registrationToken,
            Labels = Fallback(labels, DefaultLabels),
            RunnerName = Fallback(runnerName, $"{Environment.MachineName}-pinqops"),
            RunnerVersion = Fallback(runnerVersion, DefaultRunnerVersion),
            InstallDirectory = Fallback(installDirectory, DefaultInstallDirectory),
        };
    }

    /// <summary>The runner release tarball file name (linux-x64).</summary>
    public string ArchiveFileName => $"actions-runner-linux-x64-{RunnerVersion}.tar.gz";

    /// <summary>The GitHub download URL for the runner release tarball.</summary>
    public string DownloadUrl =>
        $"https://github.com/actions/runner/releases/download/v{RunnerVersion}/{ArchiveFileName}";

    /// <summary>Arguments passed to the runner's <c>config.sh</c> for unattended registration.</summary>
    public IReadOnlyList<string> ConfigureArguments() => new[]
    {
        "--url", RepositoryUrl,
        "--token", RegistrationToken,
        "--name", RunnerName,
        "--labels", Labels,
        "--unattended",
        "--replace",
    };

    private static string Fallback(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
