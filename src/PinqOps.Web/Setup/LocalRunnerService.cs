namespace PinqOps.Web;

/// <summary>
/// Inspects the self-hosted runner installed on this machine: its systemd
/// service state and its <c>_diag</c> logs (whose newest Worker log timestamp is
/// the last time the runner actually executed a job locally).
/// </summary>
public sealed class LocalRunnerService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    private readonly IProcessRunner _processRunner;

    public LocalRunnerService(IProcessRunner processRunner) => _processRunner = processRunner;

    /// <summary>One <c>actions.runner.*</c> systemd service and its live state.</summary>
    public sealed record RunnerUnit(string Unit, string ActiveState, string SubState);

    public static bool IsInstalled(string runnerDirectory) =>
        Directory.Exists(runnerDirectory) && File.Exists(Path.Combine(runnerDirectory, ".runner"));

    /// <summary>
    /// The repository URL the runner in <paramref name="runnerDirectory"/> is
    /// registered to, read from its <c>.runner</c> file — or null when nothing
    /// is registered there (or the file is unreadable).
    /// </summary>
    public static string? GetRegisteredUrl(string runnerDirectory) =>
        RunnerRegistration.ReadUrl(runnerDirectory);

    /// <summary>
    /// Whether <paramref name="registeredUrl"/> (from a runner's <c>.runner</c>
    /// file) and <paramref name="repoUrl"/> point at the same repository.
    /// </summary>
    public static bool MatchesRepo(string? registeredUrl, string? repoUrl)
    {
        var registered = NormalizeRepoUrl(registeredUrl);
        var expected = NormalizeRepoUrl(repoUrl);
        return registered is not null && expected is not null
            && string.Equals(registered, expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the locally installed runner is registered to <paramref name="repoUrl"/>.
    /// A runner registered to a different repository (a leftover from an earlier
    /// setup) must count as NOT installed, otherwise the setup flow would start
    /// the wrong repository's runner and report success while the selected repo
    /// stays runner-less on GitHub.
    /// </summary>
    public static bool IsInstalledFor(string runnerDirectory, string? repoUrl) =>
        IsInstalled(runnerDirectory) && MatchesRepo(GetRegisteredUrl(runnerDirectory), repoUrl);

    private static string? NormalizeRepoUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        try
        {
            return GitHubRepositoryParser.Parse(url).ToUrl();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public async Task<object> GetStatusAsync(string runnerDirectory)
    {
        // Same definition as the readiness check: "installed" means registered
        // (.runner exists), not merely downloaded.
        var installed = IsInstalled(runnerDirectory);

        // One read serves the agent name, the registered URL, and the unit scan.
        var (agentName, gitHubUrl) = RunnerRegistration.Read(runnerDirectory);

        var diagDirectory = Path.Combine(runnerDirectory, "_diag");
        var lastWorkerLogAt = LatestWriteTime(diagDirectory, "Worker_*.log");
        var lastRunnerLogAt = LatestWriteTime(diagDirectory, "Runner_*.log");

        return new
        {
            installed,
            runnerDirectory,
            agentName,
            gitHubUrl,
            registeredRepoUrl = NormalizeRepoUrl(gitHubUrl),
            service = await GetServiceStatusAsync(runnerDirectory, gitHubUrl).ConfigureAwait(false),
            lastWorkerLogAt,
            lastRunnerLogAt,
            // Every runner unit on the host, not just this directory's — a server
            // can carry more than one (e.g. after re-registering to a new repo).
            units = await ListUnitsAsync().ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Every <c>actions.runner.*</c> systemd service on the host with its state.
    /// Returns an empty list when systemd is unavailable.
    /// </summary>
    public async Task<IReadOnlyList<RunnerUnit>> ListUnitsAsync()
    {
        var list = await RunAsync(
                "systemctl", "list-units", "--all", "--type=service", "--plain", "--no-legend", "--no-pager",
                "actions.runner.*")
            .ConfigureAwait(false);
        if (list is null || !list.Succeeded)
        {
            return Array.Empty<RunnerUnit>();
        }

        var units = new List<RunnerUnit>();
        foreach (var line in list.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Columns: UNIT LOAD ACTIVE SUB DESCRIPTION…
            var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length >= 4 && columns[0].StartsWith("actions.runner.", StringComparison.Ordinal))
            {
                units.Add(new RunnerUnit(columns[0], columns[2], columns[3]));
            }
        }

        return units;
    }

    /// <summary>
    /// The last <paramref name="lines"/> journal lines for a runner unit. The
    /// unit is checked against the actual runner units on the host first, so an
    /// arbitrary service's journal can never be read through this path.
    /// </summary>
    public async Task<string> GetLogsAsync(string unit, int lines)
    {
        if (!await IsKnownRunnerUnitAsync(unit).ConfigureAwait(false))
        {
            throw new ArgumentException($"'{unit}' is not a known runner service.", nameof(unit));
        }

        var clampedLines = Math.Clamp(lines, 1, 1000);
        var logs = await RunAsync(
                "journalctl", "-u", unit, "-n", clampedLines.ToString(), "--no-pager", "--no-hostname")
            .ConfigureAwait(false);
        if (logs is null)
        {
            return "journalctl is unavailable on this host.";
        }

        return logs.Succeeded ? logs.StandardOutput.TrimEnd() : logs.StandardError.Trim();
    }

    private async Task<bool> IsKnownRunnerUnitAsync(string unit)
    {
        if (string.IsNullOrWhiteSpace(unit) || !unit.StartsWith("actions.runner.", StringComparison.Ordinal))
        {
            return false;
        }

        var units = await ListUnitsAsync().ConfigureAwait(false);
        return units.Any(runnerUnit => string.Equals(runnerUnit.Unit, unit, StringComparison.Ordinal));
    }

    /// <summary>
    /// Starts the runner's systemd service (fallback: <c>sudo ./svc.sh start</c>
    /// in the install directory) so the dashboard can bring an offline runner
    /// back without SSH.
    /// </summary>
    public async Task<object> StartServiceAsync(string runnerDirectory)
    {
        var lines = new List<string>();

        var unit = await FindUnitAsync(runnerDirectory, RunnerRegistration.ReadUrl(runnerDirectory)).ConfigureAwait(false);
        if (unit is not null)
        {
            // "--" so a unit name can never be read as a flag, even if the
            // validation above is ever loosened.
            var start = await RunAsync("systemctl", "start", "--", unit).ConfigureAwait(false);
            if (start is { Succeeded: true })
            {
                lines.Add($"systemctl start {unit}: ok");
                return new { succeeded = true, log = string.Join('\n', lines) };
            }

            lines.Add($"systemctl start {unit} failed: {start?.StandardError.Trim() ?? "systemctl unavailable"}");
        }
        else
        {
            lines.Add("no systemd unit found for this runner");
        }

        if (File.Exists(Path.Combine(runnerDirectory, "svc.sh")))
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var svc = await _processRunner.RunAsync("sudo", ["./svc.sh", "start"], runnerDirectory, cts.Token)
                    .ConfigureAwait(false);
                lines.Add((svc.StandardOutput + svc.StandardError).Trim());
                return new { succeeded = svc.Succeeded, log = string.Join('\n', lines) };
            }
            catch (Exception exception)
            {
                lines.Add(exception.Message);
            }
        }

        return new { succeeded = false, log = string.Join('\n', lines) };
    }

    /// <summary>
    /// Resolves the systemd unit belonging to THIS runner directory. The unit
    /// name svc.sh wrote into <c>{dir}/.service</c> is authoritative; only when
    /// that file is missing fall back to scanning <c>actions.runner.*</c> units,
    /// filtered to the repository the directory is registered to — never "the
    /// first actions.runner.* unit", which may belong to another repository.
    /// </summary>
    private async Task<string?> FindUnitAsync(string runnerDirectory, string? registeredUrl)
    {
        var serviceFile = Path.Combine(runnerDirectory, ".service");
        if (File.Exists(serviceFile))
        {
            try
            {
                var unit = (await File.ReadAllTextAsync(serviceFile).ConfigureAwait(false)).Trim();
                // Validated the same way the fallback scan below filters names: the
                // contents of this file become a systemctl argument, so an
                // unexpected value would either name an arbitrary service or — if
                // it began with '-' — be read by systemctl as a flag.
                if (IsRunnerUnitName(unit))
                {
                    return unit;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // svc.sh writes .service as root; an unreadable file must fall
                // through to the scan, not break the whole status endpoint.
            }
        }

        var list = await RunAsync(
                "systemctl", "list-units", "--all", "--plain", "--no-legend", "--no-pager",
                "actions.runner.*")
            .ConfigureAwait(false);
        if (list is null || !list.Succeeded)
        {
            return null;
        }

        return MatchingUnit(list.StandardOutput, registeredUrl);
    }

    /// <summary>
    /// The runner unit in <paramref name="systemctlOutput"/> that belongs to the
    /// repository <paramref name="registeredUrl"/> names, or null when none does.
    /// </summary>
    internal static string? MatchingUnit(string systemctlOutput, string? registeredUrl)
    {
        ArgumentNullException.ThrowIfNull(systemctlOutput);

        // No repository to filter by is no match, not any match. Admitting
        // everything here meant a directory with no registration — an app added but
        // not yet given a runner, which is every second app on a host — resolved to
        // whichever runner happened to be listed first, and was then shown, and
        // could be started, as that app's own.
        if (ExpectedUnitPrefix(registeredUrl) is not { } expectedPrefix)
        {
            return null;
        }

        return systemctlOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .FirstOrDefault(name => name is not null
                && name.StartsWith("actions.runner.", StringComparison.Ordinal)
                && name.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether a string is safe to hand systemctl as a runner unit name: the
    /// <c>actions.runner.</c> prefix every runner unit carries, and only the
    /// characters a systemd unit name may contain — so it can be neither another
    /// service nor a flag.
    /// </summary>
    private static bool IsRunnerUnitName(string unit) =>
        unit.Length > 0
        && unit.StartsWith("actions.runner.", StringComparison.Ordinal)
        && unit.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '@' or '\\');

    private static string? ExpectedUnitPrefix(string? registeredUrl)
    {
        if (registeredUrl is null)
        {
            return null;
        }

        try
        {
            var repository = GitHubRepositoryParser.Parse(registeredUrl);
            return $"actions.runner.{repository.Owner}-{repository.Name}.";
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether this runner's systemd service is currently active, or null when
    /// the service (or systemd itself) can't be found. Lets the dashboard
    /// auto-start a stopped runner instead of asking the user to press a button.
    /// </summary>
    public async Task<bool?> IsServiceActiveAsync(string runnerDirectory)
    {
        var unit = await FindUnitAsync(runnerDirectory, RunnerRegistration.ReadUrl(runnerDirectory)).ConfigureAwait(false);
        if (unit is null)
        {
            return null;
        }

        var show = await RunAsync("systemctl", "show", "--property=ActiveState", "--", unit).ConfigureAwait(false);
        if (show is null || !show.Succeeded)
        {
            return null;
        }

        foreach (var line in show.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator > 0 && line[..separator] == "ActiveState")
            {
                return line[(separator + 1)..].Trim() == "active";
            }
        }

        return null;
    }

    private async Task<object?> GetServiceStatusAsync(string runnerDirectory, string? registeredUrl)
    {
        var unit = await FindUnitAsync(runnerDirectory, registeredUrl).ConfigureAwait(false);
        if (unit is null)
        {
            return null;
        }

        var show = await RunAsync(
                "systemctl", "show", unit,
                "--property=ActiveState", "--property=SubState", "--property=ExecMainStartTimestamp")
            .ConfigureAwait(false);
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        if (show is not null && show.Succeeded)
        {
            foreach (var line in show.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = line.IndexOf('=');
                if (separator > 0)
                {
                    properties[line[..separator]] = line[(separator + 1)..].Trim();
                }
            }
        }

        return new
        {
            unit,
            activeState = properties.GetValueOrDefault("ActiveState"),
            subState = properties.GetValueOrDefault("SubState"),
            since = properties.GetValueOrDefault("ExecMainStartTimestamp"),
        };
    }

    private async Task<ProcessResult?> RunAsync(string fileName, params string[] arguments)
    {
        try
        {
            using var cts = new CancellationTokenSource(CommandTimeout);
            return await _processRunner.RunAsync(fileName, arguments, workingDirectory: null, cts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // systemctl may be missing entirely (containers, macOS); report "unknown".
            return null;
        }
    }

    private static DateTimeOffset? LatestWriteTime(string directory, string pattern)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        DateTimeOffset? latest = null;
        foreach (var file in Directory.EnumerateFiles(directory, pattern))
        {
            var writeTime = File.GetLastWriteTimeUtc(file);
            if (latest is null || writeTime > latest)
            {
                latest = writeTime;
            }
        }

        return latest;
    }
}
