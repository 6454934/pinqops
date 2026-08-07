namespace PinqOps;

/// <summary>
/// Inputs for <c>pinqops setup</c>. Everything is optional up front: the wizard
/// prompts for what it needs when running interactively, and fails fast when it
/// does not. Runner knobs reuse <see cref="RunnerInstallOptions"/>'s defaults.
/// </summary>
public sealed record SetupOptions
{
    public const string DefaultComposeFilePath = "/opt/pinqops/docker-compose.yml";

    public string? RepositoryUrl { get; init; }
    public string? PersonalAccessToken { get; init; }
    public string? RegistrationToken { get; init; }
    public string ComposeFilePath { get; init; } = DefaultComposeFilePath;
    public string Labels { get; init; } = RunnerInstallOptions.DefaultLabels;
    public string RunnerName { get; init; } = $"{Environment.MachineName}-pinqops";
    public string RunnerVersion { get; init; } = RunnerInstallOptions.DefaultRunnerVersion;
    public string InstallDirectory { get; init; } = RunnerInstallOptions.DefaultInstallDirectory;
    public string ServiceUser { get; init; } = Environment.UserName;
    public bool NonInteractive { get; init; }
    public bool SkipPreflight { get; init; }
    public bool UseGhCli { get; init; } = true;

    public static SetupOptions Create(
        string? repositoryUrl = null,
        string? personalAccessToken = null,
        string? registrationToken = null,
        string? composeFilePath = null,
        string? labels = null,
        string? runnerName = null,
        string? runnerVersion = null,
        string? installDirectory = null,
        string? serviceUser = null,
        bool nonInteractive = false,
        bool skipPreflight = false,
        bool useGhCli = true)
    {
        return new SetupOptions
        {
            RepositoryUrl = NullIfBlank(repositoryUrl),
            PersonalAccessToken = NullIfBlank(personalAccessToken),
            RegistrationToken = NullIfBlank(registrationToken),
            ComposeFilePath = Fallback(composeFilePath, DefaultComposeFilePath),
            Labels = Fallback(labels, RunnerInstallOptions.DefaultLabels),
            RunnerName = Fallback(runnerName, $"{Environment.MachineName}-pinqops"),
            RunnerVersion = Fallback(runnerVersion, RunnerInstallOptions.DefaultRunnerVersion),
            InstallDirectory = Fallback(installDirectory, RunnerInstallOptions.DefaultInstallDirectory),
            ServiceUser = Fallback(serviceUser, Environment.UserName),
            NonInteractive = nonInteractive,
            SkipPreflight = skipPreflight,
            UseGhCli = useGhCli,
        };
    }

    /// <summary>
    /// Builds the runner-install options from a resolved repository URL and
    /// registration token — the single point where the token flows into the
    /// installer (never the personal access token).
    /// </summary>
    public RunnerInstallOptions ToRunnerInstallOptions(string repositoryUrl, string registrationToken) =>
        RunnerInstallOptions.Create(
            repositoryUrl,
            registrationToken,
            Labels,
            RunnerName,
            RunnerVersion,
            InstallDirectory);

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Fallback(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
