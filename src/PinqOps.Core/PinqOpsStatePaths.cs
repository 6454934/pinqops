namespace PinqOps;

/// <summary>
/// Resolves the on-server pinqops state files from the compose file path — the
/// one coordinate both the CLI (on the runner) and the web dashboard already
/// share. State lives in a <c>.pinqops/</c> directory next to the compose file,
/// so it works regardless of which user runs which component.
/// </summary>
public static class PinqOpsStatePaths
{
    public const string StateDirectoryName = ".pinqops";

    /// <summary>Directory holding pinqops state, e.g. <c>/opt/pinqops/.pinqops</c>.</summary>
    public static string StateDirectory(string composeFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeFilePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(composeFilePath));
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("Compose file path has no parent directory.", nameof(composeFilePath));
        }

        return Path.Combine(directory, StateDirectoryName);
    }

    /// <summary>Deploy history JSON, e.g. <c>/opt/pinqops/.pinqops/history.json</c>.</summary>
    public static string HistoryFile(string composeFilePath) =>
        Path.Combine(StateDirectory(composeFilePath), "history.json");

    /// <summary>Notification config JSON, e.g. <c>/opt/pinqops/.pinqops/notify.json</c>.</summary>
    public static string NotifyConfigFile(string composeFilePath) =>
        Path.Combine(StateDirectory(composeFilePath), "notify.json");

    /// <summary>
    /// Per-project deploy settings JSON, e.g.
    /// <c>/opt/pinqops/.pinqops/deploy.json</c> — how the deploy sequence should
    /// treat this project, beyond what the workflow passes on the command line.
    /// </summary>
    public static string DeploySettingsFile(string composeFilePath) =>
        Path.Combine(StateDirectory(composeFilePath), "deploy.json");

    /// <summary>
    /// The compose project's dotenv file, e.g. <c>/opt/pinqops/.env</c>. Docker
    /// compose interpolates it automatically from the project directory.
    /// </summary>
    public static string EnvFile(string composeFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeFilePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(composeFilePath));
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("Compose file path has no parent directory.", nameof(composeFilePath));
        }

        return Path.Combine(directory, ".env");
    }

    /// <summary>
    /// Directory to run <c>docker compose</c> from so the project's <c>.env</c>
    /// is loaded and the project directory is unambiguous. Compose only
    /// interpolates <c>.env</c> when the working directory (equivalently, the
    /// project directory) is the one holding the file — passing <c>-f</c> with an
    /// unrelated CWD silently drops every <c>${PINQOPS_HOST_PORT:-…}</c> to its
    /// YAML default and makes a port change in <c>.env</c> produce no config diff,
    /// so <c>up -d</c> never recreates the container onto the chosen port.
    /// Returns null when it can't be resolved to an existing directory; callers
    /// then fall back to the process CWD, exactly as before.
    /// </summary>
    public static string? ComposeWorkingDirectory(string composeFilePath)
    {
        // Rooted only: a relative -f would be re-resolved against this working
        // directory and point at a different file. Every real call site passes an
        // absolute compose path. Directory.Exists keeps a missing compose file
        // producing docker's clean "file not found" instead of a Process.Start
        // directory-not-found throw (and keeps fake-path unit tests at null).
        if (string.IsNullOrWhiteSpace(composeFilePath) || !Path.IsPathRooted(composeFilePath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(composeFilePath));
        return !string.IsNullOrEmpty(directory) && Directory.Exists(directory) ? directory : null;
    }
}
