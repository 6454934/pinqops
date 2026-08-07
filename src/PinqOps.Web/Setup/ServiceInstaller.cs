using System.Text;

namespace PinqOps.Web;

/// <summary>
/// Installs pinqops-ui as a systemd service so the dashboard keeps running
/// after the SSH session ends and comes back after a reboot.
/// </summary>
public sealed class ServiceInstaller
{
    private const string ServiceName = "pinqops-ui";
    private const string UnitPath = $"/etc/systemd/system/{ServiceName}.service";

    private readonly IProcessRunner _processRunner;
    private readonly DockerBootstrapper _dockerBootstrapper;
    private readonly Action<string> _log;

    public ServiceInstaller(
        IProcessRunner processRunner,
        Action<string> log,
        DockerBootstrapper? dockerBootstrapper = null)
    {
        _processRunner = processRunner;
        _log = log;
        _dockerBootstrapper = dockerBootstrapper ?? new DockerBootstrapper(processRunner, log);
    }

    /// <param name="certPasswordFile">
    /// Preferred over <paramref name="certPassword"/>: it keeps the password out
    /// of the unit file and out of the process command line, which every user on
    /// the host can read.
    /// </param>
    public async Task<int> InstallAsync(
        string port,
        string host,
        string? certPath,
        string? certPassword,
        string user,
        string? trustedProxies = null,
        string? certPasswordFile = null)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable)
            || Path.GetFileName(executable) is "dotnet" or "dotnet.exe")
        {
            _log("error: run 'install-service' from the published pinqops-ui binary, not via 'dotnet'.");
            return 1;
        }

        // These values are interpolated into the systemd unit's ExecStart line;
        // reject anything that could inject an extra directive (newline) or break
        // out of a quoted argument (double quote).
        if (!int.TryParse(port, out var portNumber) || portNumber is < 1 or > 65535)
        {
            _log("error: --port must be an integer between 1 and 65535.");
            return 1;
        }

        foreach (var (option, value) in new[]
                 {
                     ("--host", host),
                     ("--cert", certPath ?? string.Empty),
                     ("--cert-password", certPassword ?? string.Empty),
                     ("--user", user),
                     ("--trusted-proxy", trustedProxies ?? string.Empty),
                     ("--cert-password-file", certPasswordFile ?? string.Empty),
                 })
        {
            if (value.AsSpan().IndexOfAny('\r', '\n', '"') >= 0)
            {
                _log($"error: {option} contains an invalid character (newline or double quote).");
                return 1;
            }
        }

        // systemd splits ExecStart on whitespace, so a value carrying a space
        // appends arguments to the command line rather than being part of the one
        // it belongs to. User= is not a command line but is likewise a single
        // token, and a whitespace-bearing value there is never a real user.
        foreach (var (option, value) in new[] { ("--host", host), ("--user", user) })
        {
            if (value.AsSpan().IndexOfAny(' ', '\t') >= 0)
            {
                _log($"error: {option} must not contain whitespace.");
                return 1;
            }
        }

        // The dashboard talks to Docker on every page. Install it now on
        // Ubuntu/Debian so `install-service` on a bare VPS does not leave the
        // operator with opaque "Something went wrong" cards.
        if (!await _dockerBootstrapper.EnsureReadyAsync(dockerGroupUser: user).ConfigureAwait(false))
        {
            _log("error: Docker is required. Install it (docs/SETUP.md §3.2), then re-run install-service.");
            return 1;
        }

        // Quoted like every other interpolated value — unquoted, a space in the
        // host would have become extra arguments to the service.
        var execStart = new StringBuilder($"\"{executable}\" --port {port} --host \"{host}\"");
        if (!string.IsNullOrWhiteSpace(certPath))
        {
            execStart.Append($" --cert \"{certPath}\"");
        }

        // The file form wins: it keeps the password out of the unit entirely.
        if (!string.IsNullOrWhiteSpace(certPasswordFile))
        {
            execStart.Append($" --cert-password-file \"{certPasswordFile}\"");
        }
        else if (!string.IsNullOrWhiteSpace(certPassword))
        {
            execStart.Append($" --cert-password \"{certPassword}\"");
            _log(
                "warning: --cert-password puts the password in the unit file and in the service's "
                + "command line, which every local user can read via /proc. Prefer "
                + "--cert-password-file <path> with a 0600 file.");
        }

        if (!string.IsNullOrWhiteSpace(trustedProxies))
        {
            execStart.Append($" --trusted-proxy \"{trustedProxies}\"");
        }

        var unit = $"""
            [Unit]
            Description=pinqops web dashboard
            Wants=network-online.target
            After=network-online.target docker.service

            [Service]
            Type=simple
            User={user}
            ExecStart={execStart}
            Restart=on-failure
            RestartSec=3
            NoNewPrivileges=true

            [Install]
            WantedBy=multi-user.target

            """;

        try
        {
            // Written 0600-first and renamed into place: File.WriteAllText would
            // have created the unit world-readable and only restricted it
            // afterwards, so when the unit embeds the cert password those bytes sat
            // on a readable inode for the window in between (and, on a re-install
            // over an existing 0644 unit, until the chmod landed).
            SecureFile.WriteAllText(UnitPath, unit);

            // Only the password-bearing form has to stay root-only; otherwise the
            // unit is ordinary configuration and readable like any other.
            if (string.IsNullOrWhiteSpace(certPassword) && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    UnitPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
        }
        catch (UnauthorizedAccessException)
        {
            _log($"error: cannot write {UnitPath} — run with sudo.");
            return 1;
        }

        if (!await SystemctlAsync("daemon-reload").ConfigureAwait(false)
            || !await SystemctlAsync("enable", "--now", ServiceName).ConfigureAwait(false))
        {
            return 1;
        }

        _log($"{ServiceName} installed as a systemd service (user '{user}') and started on port {port}.");
        _log("it now survives SSH logout and starts again after a reboot.");

        // Print the live setup code here so the operator does not paste a stale
        // line from an older journalctl grep (restarts used to mint a new code
        // every time; the code is now persisted, but the journal still piles up).
        var code = await WaitForSetupCodeAsync(user).ConfigureAwait(false);
        if (code is not null)
        {
            _log($"first-run setup code: {code}");
            _log("open http://<server>:" + port + " and enter that code (older journal lines are stale).");
        }
        else
        {
            _log($"logs:  journalctl -u {ServiceName} -n 20 --no-pager");
        }

        return 0;
    }

    private async Task<string?> WaitForSetupCodeAsync(string user)
    {
        var home = user is "root"
            ? "/root"
            : Path.Combine("/home", user);
        var path = Path.Combine(home, ".config", "pinqops", "setup-code");

        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    var code = (await File.ReadAllTextAsync(path).ConfigureAwait(false)).Trim().ToLowerInvariant();
                    if (code.Length == 16 && code.All(Uri.IsHexDigit))
                    {
                        return code;
                    }
                }
            }
            catch (IOException)
            {
                // Service may still be creating the file.
            }

            await Task.Delay(200).ConfigureAwait(false);
        }

        return null;
    }

    public async Task<int> UninstallAsync()
    {
        // Best effort: the service may already be stopped or half-removed.
        await SystemctlAsync("disable", "--now", ServiceName).ConfigureAwait(false);

        if (File.Exists(UnitPath))
        {
            try
            {
                File.Delete(UnitPath);
            }
            catch (UnauthorizedAccessException)
            {
                _log($"error: cannot delete {UnitPath} — run with sudo.");
                return 1;
            }
        }

        await SystemctlAsync("daemon-reload").ConfigureAwait(false);
        _log($"{ServiceName} service removed.");
        return 0;
    }

    private async Task<bool> SystemctlAsync(params string[] arguments)
    {
        try
        {
            var result = await _processRunner.RunAsync("systemctl", arguments).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                _log($"error: 'systemctl {string.Join(' ', arguments)}' failed (exit {result.ExitCode}): {result.StandardError.Trim()}");
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            _log($"error: could not run systemctl ({exception.Message}). Is this a systemd machine?");
            return false;
        }
    }
}
