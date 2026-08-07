namespace PinqOps;

/// <summary>
/// Makes sure Docker Engine and the Compose v2 plugin are available on a bare
/// Ubuntu/Debian server. Used by <c>pinqops setup</c> and
/// <c>pinqops-ui install-service</c> so a fresh box does not leave the
/// operator staring at "command not found" after the dashboard starts.
///
/// Unsupported distributions are left alone with a clear hint — pinqops does
/// not try to drive every package manager on the planet.
/// </summary>
public sealed class DockerBootstrapper
{
    private readonly IProcessRunner _processRunner;
    private readonly Action<string>? _log;
    private readonly Func<string?> _readOsRelease;

    public DockerBootstrapper(
        IProcessRunner processRunner,
        Action<string>? log = null,
        Func<string?>? readOsRelease = null)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _log = log;
        _readOsRelease = readOsRelease ?? ReadOsReleaseFile;
    }

    /// <summary>
    /// Returns true when <c>docker</c> and <c>docker compose</c> already work,
    /// or after a successful install on Ubuntu/Debian. Returns false when the
    /// host cannot be bootstrapped automatically.
    /// </summary>
    public async Task<bool> EnsureReadyAsync(
        string? dockerGroupUser = null,
        CancellationToken cancellationToken = default)
    {
        if (await IsReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            await EnsureDockerGroupAsync(dockerGroupUser, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (!IsSupportedDistribution())
        {
            _log?.Invoke(
                "Docker is missing and this host is not Ubuntu/Debian — install Docker Engine "
                + "manually (see docs/SETUP.md section 3.2), then re-run.");
            return false;
        }

        _log?.Invoke("Docker is missing — installing Docker Engine + Compose plugin (a few minutes)…");
        if (!await InstallAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (!await IsReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            _log?.Invoke("error: Docker packages installed but `docker version` still fails — check journalctl -u docker.");
            return false;
        }

        await EnsureDockerGroupAsync(dockerGroupUser, cancellationToken).ConfigureAwait(false);
        Progress(100, "Docker Engine and Compose are ready.");
        return true;
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        return await ProbeAsync("docker", new[] { "version" }, cancellationToken).ConfigureAwait(false)
            && await ProbeAsync("docker", new[] { "compose", "version" }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> InstallAsync(CancellationToken cancellationToken)
    {
        // Discrete steps (no operator input in any of them) so we can report a
        // percentage between the long apt calls — a single opaque bash script
        // left the operator staring at a frozen terminal for minutes.
        var distro = ResolveAptDistro();
        if (distro is null)
        {
            _log?.Invoke("error: could not resolve Ubuntu/Debian codename from /etc/os-release.");
            return false;
        }

        var (id, codename) = distro.Value;
        // id/codename come from os-release (ubuntu/debian + jammy/bookworm/…);
        // refuse anything that could break out of the double-quoted echo below.
        if (!IsSafeToken(id) || !IsSafeToken(codename))
        {
            _log?.Invoke("error: refusing to install Docker — os-release values look unsafe.");
            return false;
        }

        var steps = new (int Percent, string Label, string[] Arguments)[]
        {
            (5, "updating apt indexes…", new[] { "apt-get", "update" }),
            (15, "installing curl + ca-certificates…", new[] { "apt-get", "install", "-y", "ca-certificates", "curl" }),
            (25, "adding Docker apt keyring…", new[] { "bash", "-c",
                "install -m 0755 -d /etc/apt/keyrings && "
                + $"curl -fsSL https://download.docker.com/linux/{id}/gpg -o /etc/apt/keyrings/docker.asc && "
                + "chmod a+r /etc/apt/keyrings/docker.asc" }),
            (40, "adding Docker apt repository…", new[] { "bash", "-c",
                $"echo \"deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] "
                + $"https://download.docker.com/linux/{id} {codename} stable\" "
                + "> /etc/apt/sources.list.d/docker.list" }),
            (50, "refreshing apt indexes…", new[] { "apt-get", "update" }),
            (70, "installing Docker packages (this is the long step)…", new[]
            {
                "apt-get", "install", "-y",
                "docker-ce", "docker-ce-cli", "containerd.io",
                "docker-buildx-plugin", "docker-compose-plugin",
            }),
            (90, "starting Docker…", new[] { "systemctl", "enable", "--now", "docker" }),
        };

        foreach (var (percent, label, arguments) in steps)
        {
            Progress(percent, label);
            if (!await RunSudoAsync(arguments, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    private (string Id, string Codename)? ResolveAptDistro()
    {
        var text = _readOsRelease();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string? id = null;
        string? codename = null;
        string? idLike = null;
        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = raw.Trim();
            if (line.StartsWith("ID=", StringComparison.Ordinal))
            {
                id = Unquote(line["ID=".Length..]);
            }
            else if (line.StartsWith("VERSION_CODENAME=", StringComparison.Ordinal))
            {
                codename = Unquote(line["VERSION_CODENAME=".Length..]);
            }
            else if (line.StartsWith("ID_LIKE=", StringComparison.Ordinal))
            {
                idLike = Unquote(line["ID_LIKE=".Length..]);
            }
        }

        if (string.IsNullOrWhiteSpace(codename))
        {
            return null;
        }

        if (string.Equals(id, "ubuntu", StringComparison.OrdinalIgnoreCase)
            || string.Equals(id, "debian", StringComparison.OrdinalIgnoreCase))
        {
            return (id!.ToLowerInvariant(), codename);
        }

        if (idLike is not null
            && (idLike.Contains("debian", StringComparison.OrdinalIgnoreCase)
                || idLike.Contains("ubuntu", StringComparison.OrdinalIgnoreCase)))
        {
            return ("debian", codename);
        }

        return null;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1]
            : value;

    private static bool IsSafeToken(string value) =>
        value.Length > 0 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private async Task<bool> RunSudoAsync(string[] arguments, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processRunner
                .RunAsync("sudo", arguments, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                var detail = string.IsNullOrWhiteSpace(result.StandardError)
                    ? result.StandardOutput.Trim()
                    : result.StandardError.Trim();
                _log?.Invoke($"error: Docker install failed (exit {result.ExitCode}): {detail}");
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            _log?.Invoke($"error: could not install Docker ({exception.Message}). Run with sudo?");
            return false;
        }
    }

    private void Progress(int percent, string label) =>
        _log?.Invoke($"[{percent,3}%] {label}");

    private async Task EnsureDockerGroupAsync(string? user, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(user) || user is "root")
        {
            return;
        }

        try
        {
            var result = await _processRunner
                .RunAsync("sudo", new[] { "usermod", "-aG", "docker", user }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (result.Succeeded)
            {
                _log?.Invoke($"added '{user}' to the docker group (new sessions pick this up automatically).");
            }
        }
        catch (Exception)
        {
            // Non-fatal: root can still talk to the daemon; the operator can usermod later.
        }
    }

    private bool IsSupportedDistribution() => ResolveAptDistro() is not null;

    private static string? ReadOsReleaseFile()
    {
        try
        {
            return File.Exists("/etc/os-release") ? File.ReadAllText("/etc/os-release") : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task<bool> ProbeAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processRunner
                .RunAsync(fileName, arguments, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return result.Succeeded;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
