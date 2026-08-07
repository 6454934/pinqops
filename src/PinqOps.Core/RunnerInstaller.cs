namespace PinqOps;

/// <summary>
/// Installs and registers a GitHub Actions self-hosted runner as a systemd
/// service. External work (download, extract, config.sh, svc.sh) is delegated to
/// injected abstractions so the orchestration is testable.
/// </summary>
public sealed class RunnerInstaller
{
    private readonly IProcessRunner _processRunner;
    private readonly IFileDownloader _fileDownloader;
    private readonly Action<string>? _log;

    public RunnerInstaller(IProcessRunner processRunner, IFileDownloader fileDownloader, Action<string>? log = null)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _fileDownloader = fileDownloader ?? throw new ArgumentNullException(nameof(fileDownloader));
        _log = log;
    }

    /// <summary>
    /// Downloads the runner (if needed), registers it unattended, and installs +
    /// starts it as a systemd service running as <paramref name="serviceUser"/>.
    /// Returns true on success.
    /// </summary>
    public async Task<bool> InstallAsync(
        RunnerInstallOptions options,
        string serviceUser,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceUser);

        Directory.CreateDirectory(options.InstallDirectory);

        var configScript = Path.Combine(options.InstallDirectory, "config.sh");
        if (!File.Exists(configScript))
        {
            var archivePath = Path.Combine(options.InstallDirectory, options.ArchiveFileName);
            _log?.Invoke($"downloading runner {options.RunnerVersion}");
            await _fileDownloader.DownloadAsync(options.DownloadUrl, archivePath, cancellationToken).ConfigureAwait(false);

            _log?.Invoke("extracting runner");
            if (!await RunAsync("tar", new[] { "xzf", archivePath }, options.InstallDirectory, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        // Let config.sh register when setup runs as root; the runner otherwise
        // refuses. The variable is ignored for non-root users, and the child
        // process inherits it.
        Environment.SetEnvironmentVariable("RUNNER_ALLOW_RUNASROOT", "1");

        // config.sh refuses to configure over an existing registration (even
        // with --replace), so any leftover — possibly for a different repo —
        // must be de-registered and its systemd unit uninstalled first.
        if (File.Exists(Path.Combine(options.InstallDirectory, ".runner")))
        {
            await CleanupExistingRegistrationAsync(options, cancellationToken).ConfigureAwait(false);
        }

        _log?.Invoke($"registering runner '{options.RunnerName}' with labels '{options.Labels}'");

        // Invoke config.sh by its full path: .NET resolves a relative executable
        // against the current process's directory, not the child WorkingDirectory,
        // so "./config.sh" is not found when pinqops runs from another directory.
        if (!await RunAsync(configScript, options.ConfigureArguments(), options.InstallDirectory, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        _log?.Invoke("installing systemd service");
        if (!await RunAsync("sudo", new[] { "./svc.sh", "install", serviceUser }, options.InstallDirectory, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        _log?.Invoke("starting systemd service");
        if (!await RunAsync("sudo", new[] { "./svc.sh", "start" }, options.InstallDirectory, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        _log?.Invoke("runner installed and started");
        return true;
    }

    /// <summary>
    /// Stops, uninstalls and de-registers a runner already present under
    /// <paramref name="installDirectory"/>. Used by app purge and by install
    /// when replacing a leftover registration. Best-effort: failures are logged
    /// and never throw.
    /// </summary>
    public Task UninstallAsync(
        string installDirectory,
        string? removalToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        // RegistrationToken is unused for uninstall; Create still requires one.
        var options = RunnerInstallOptions.Create(
                "https://github.com/pinqops/uninstall",
                "unused",
                installDirectory: installDirectory)
            with { RemovalToken = removalToken };
        return CleanupExistingRegistrationAsync(options, cancellationToken);
    }

    /// <summary>
    /// Stops and uninstalls the existing runner service, then de-registers the
    /// old runner (with <see cref="RunnerInstallOptions.RemovalToken"/> when
    /// available, otherwise by force-deleting its registration files). All
    /// steps are best-effort: a half-broken leftover must never block a fresh
    /// registration.
    /// </summary>
    private async Task CleanupExistingRegistrationAsync(
        RunnerInstallOptions options,
        CancellationToken cancellationToken)
    {
        var directory = options.InstallDirectory;
        _log?.Invoke("existing runner registration found; removing it first");

        if (File.Exists(Path.Combine(directory, "svc.sh")))
        {
            await RunAsync("sudo", new[] { "./svc.sh", "stop" }, directory, cancellationToken).ConfigureAwait(false);
            await RunAsync("sudo", new[] { "./svc.sh", "uninstall" }, directory, cancellationToken).ConfigureAwait(false);
        }

        var removed = false;
        var configScript = Path.Combine(directory, "config.sh");
        if (!string.IsNullOrWhiteSpace(options.RemovalToken) && File.Exists(configScript))
        {
            removed = await RunAsync(
                    configScript,
                    new[] { "remove", "--token", options.RemovalToken },
                    directory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!removed)
        {
            // Without (or after a failed) config.sh remove, delete the
            // registration files so config.sh accepts a fresh registration. The
            // orphaned GitHub-side entry just shows offline until purged. Each
            // delete is best-effort too: a root-owned leftover must not turn
            // cleanup into a hard failure — config.sh will complain if the file
            // that matters could not be removed.
            _log?.Invoke("could not de-register the old runner cleanly; deleting its local registration files");
            foreach (var name in new[] { ".runner", ".credentials", ".credentials_rsaparams", ".service" })
            {
                var path = Path.Combine(directory, name);
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _log?.Invoke($"could not delete {name}: {exception.Message}");
                }
            }
        }
    }

    private async Task<bool> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await _processRunner
            .RunAsync(fileName, arguments, workingDirectory, cancellationToken)
            .ConfigureAwait(false);

        if (result.StandardOutput.Length > 0)
        {
            _log?.Invoke(result.StandardOutput.TrimEnd());
        }

        if (!result.Succeeded)
        {
            _log?.Invoke($"'{fileName}' failed (exit {result.ExitCode}): {result.StandardError.TrimEnd()}");
            return false;
        }

        return true;
    }
}
