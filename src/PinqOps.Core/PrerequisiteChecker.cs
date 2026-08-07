namespace PinqOps;

/// <summary>The result of probing a single prerequisite tool.</summary>
public sealed record PrerequisiteResult(string Name, bool IsPresent, string InstallHint);

/// <summary>The outcome of a full prerequisite scan.</summary>
public sealed record PrerequisiteReport(IReadOnlyList<PrerequisiteResult> Results)
{
    public bool AllPresent => Results.All(result => result.IsPresent);

    public IReadOnlyList<PrerequisiteResult> Missing =>
        Results.Where(result => !result.IsPresent).ToArray();
}

/// <summary>
/// Detects the tools the setup wizard needs on the server — Docker, the Compose
/// v2 plugin, tar, and systemd — by running each tool's version command. It only
/// reports; it never installs anything. Probes all tools in one pass so a bare
/// server sees every gap at once.
/// </summary>
public sealed class PrerequisiteChecker
{
    private readonly IProcessRunner _processRunner;

    private static readonly Probe[] Probes =
    {
        new(
            "Docker Engine",
            "docker",
            new[] { "version" },
            "Install Docker Engine and start it (see docs/SETUP.md section 3.2 for the apt-repo block), then:\n"
            + "      sudo systemctl enable --now docker"),
        new(
            "Docker Compose plugin",
            "docker",
            new[] { "compose", "version" },
            "Install the Compose v2 plugin:\n"
            + "      sudo apt-get install -y docker-compose-plugin"),
        new(
            "tar",
            "tar",
            new[] { "--version" },
            "Install tar:\n"
            + "      sudo apt-get install -y tar"),
        new(
            "systemd",
            "systemctl",
            new[] { "--version" },
            "The runner installs as a systemd service, so it needs an init system.\n"
            + "      Run pinqops setup on a systemd-based host (a normal VM, not a minimal container)."),
    };

    public PrerequisiteChecker(IProcessRunner processRunner)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public async Task<PrerequisiteReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<PrerequisiteResult>(Probes.Length);
        foreach (var probe in Probes)
        {
            var isPresent = await ProbeAsync(probe, cancellationToken).ConfigureAwait(false);
            results.Add(new PrerequisiteResult(probe.Name, isPresent, probe.InstallHint));
        }

        return new PrerequisiteReport(results);
    }

    private async Task<bool> ProbeAsync(Probe probe, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processRunner
                .RunAsync(probe.FileName, probe.Arguments, workingDirectory: null, cancellationToken)
                .ConfigureAwait(false);
            return result.Succeeded;
        }
        catch (Exception)
        {
            // The binary is not on PATH: Process.Start throws rather than
            // returning a non-zero exit code. A launch failure is the probe's
            // answer — "not present" — not an error to surface.
            return false;
        }
    }

    private sealed record Probe(string Name, string FileName, IReadOnlyList<string> Arguments, string InstallHint);
}
