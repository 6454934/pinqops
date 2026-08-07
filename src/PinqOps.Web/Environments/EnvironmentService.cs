using PinqOps;

namespace PinqOps.Web;

/// <summary>
/// Keeps the on-disk SSH material in step with the environment registry, and
/// resolves an environment to the endpoint docker commands are addressed to.
///
/// The registry is the source of truth; the files here are derived from it and
/// rewritten whenever it changes, so a key removed from the registry stops
/// existing on disk too. Docker's SSH transport passes no options of its own,
/// which is why the key, the port and the pinned host key all have to reach it
/// through a config file rather than the command line.
/// </summary>
public sealed class EnvironmentService
{
    /// <summary>
    /// Names the SSH config the managed block is written into, instead of the one
    /// belonging to the account the dashboard runs as.
    ///
    /// <para>Only the test host sets it. That config is a real person's file, and
    /// a suite that boots the dashboard rewrote it — leaving the hosts a fixture
    /// registered behind in it, whichever fixture booted last deciding what was
    /// left there.</para>
    /// </summary>
    internal const string SshConfigPathVariable = "PINQOPS_SSH_CONFIG";

    private readonly UiConfigStore _store;
    private readonly ILogger<EnvironmentService> _logger;
    private readonly string _sshDirectory;
    private readonly string _sshConfigPath;
    private readonly Lock _gate = new();

    public EnvironmentService(
        UiConfigStore store,
        ILogger<EnvironmentService> logger,
        string? sshConfigPath = null)
    {
        _store = store;
        _logger = logger;
        var configDirectory = Path.GetDirectoryName(store.Path_)!;
        _sshDirectory = Path.Combine(configDirectory, "ssh");
        _sshConfigPath = sshConfigPath
            ?? Environment.GetEnvironmentVariable(SshConfigPathVariable)
            ?? DefaultSshConfigPath;
    }

    /// <summary>
    /// The SSH config of the account the dashboard runs as, which is the file
    /// docker's SSH transport reads and so the only one worth writing in production.
    /// </summary>
    internal static string DefaultSshConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");

    /// <summary>Every registered environment, local first.</summary>
    public IReadOnlyList<ManagedEnvironment> All() => _store.Current.Environments;

    /// <summary>The environment with this id, or null.</summary>
    public ManagedEnvironment? Find(string? id) =>
        _store.Current.Environments.FirstOrDefault(environment =>
            string.Equals(environment.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The environment a request selected, defaulting to local when none was
    /// named. Throws when the id names nothing, so a typo cannot silently operate
    /// on the wrong host — the one failure mode that matters most here.
    /// </summary>
    public ManagedEnvironment Resolve(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? Find(ManagedEnvironment.LocalId) ?? ManagedEnvironment.Local()
            : Find(id) ?? throw new ArgumentException($"Unknown environment '{id}'.");

    public string KeyPathFor(ManagedEnvironment environment) =>
        Path.Combine(_sshDirectory, $"{environment.Id}.key");

    public string KnownHostsPath => Path.Combine(_sshDirectory, "known_hosts");

    /// <summary>Adds or replaces an environment and rewrites the SSH material.</summary>
    public void Save(ManagedEnvironment environment)
    {
        environment.Validate();
        if (environment.Transport == ManagedEnvironment.TransportSsh
            && environment.HostKey is { Length: > 0 } hostKey
            && !SshConfigGenerator.IsValidHostKey(hostKey))
        {
            throw new ArgumentException("The host key is not a valid OpenSSH public key.");
        }

        _store.Update(config =>
        {
            var existing = config.Environments.FindIndex(candidate =>
                string.Equals(candidate.Id, environment.Id, StringComparison.OrdinalIgnoreCase));

            // Adding an environment must not silently drop its key: an edit that
            // leaves the key blank keeps whatever was stored.
            if (existing >= 0)
            {
                environment.PrivateKey ??= config.Environments[existing].PrivateKey;
                environment.HostKey ??= config.Environments[existing].HostKey;
                config.Environments[existing] = environment;
            }
            else
            {
                config.Environments.Add(environment);
            }
        });

        SyncSshMaterial();
    }

    /// <summary>Removes an environment and the material derived from it.</summary>
    public void Remove(string id)
    {
        if (string.Equals(id, ManagedEnvironment.LocalId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The local environment cannot be removed.");
        }

        _store.Update(config => config.Environments.RemoveAll(environment =>
            string.Equals(environment.Id, id, StringComparison.OrdinalIgnoreCase)));

        SyncSshMaterial();
    }

    /// <summary>
    /// Rewrites the key files, the known-hosts file and the managed block of the
    /// SSH config from the registry. Safe to call repeatedly; it is how a restored
    /// or hand-edited config heals itself on the next change.
    /// </summary>
    public void SyncSshMaterial()
    {
        lock (_gate)
        {
            var environments = _store.Current.Environments;
            Directory.CreateDirectory(_sshDirectory);
            RestrictToOwner(_sshDirectory);

            var expectedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var environment in environments.Where(candidate => !candidate.IsLocal))
            {
                if (environment.PrivateKey is not { Length: > 0 } key)
                {
                    continue;
                }

                var path = KeyPathFor(environment);
                expectedKeys.Add(path);

                // 0600 from creation — OpenSSH refuses a key file anyone else can
                // read, and it is shell access to the host besides.
                SecureFile.WriteAllText(path, key.EndsWith('\n') ? key : key + "\n");
            }

            // A key whose environment is gone must not linger.
            foreach (var stale in Directory.EnumerateFiles(_sshDirectory, "*.key").Where(path => !expectedKeys.Contains(path)))
            {
                TryDelete(stale);
            }

            SecureFile.WriteAllText(KnownHostsPath, SshConfigGenerator.GenerateKnownHosts(environments));

            var block = SshConfigGenerator.Generate(environments, KeyPathFor, KnownHostsPath);
            WriteSshConfig(block);
        }
    }

    /// <summary>
    /// Merges the managed block into the user's SSH config, leaving anything they
    /// wrote around it alone. Failure is logged rather than thrown: it breaks
    /// remote environments, but it must not take the dashboard down with it.
    /// </summary>
    private void WriteSshConfig(string managedBlock)
    {
        try
        {
            var directory = Path.GetDirectoryName(_sshConfigPath)!;
            Directory.CreateDirectory(directory);
            RestrictToOwner(directory);
            var existing = File.Exists(_sshConfigPath) ? SecureFile.ReadAllText(_sshConfigPath) : null;
            SecureFile.WriteAllText(_sshConfigPath, SshConfigGenerator.Merge(existing, managedBlock));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(
                exception,
                "Could not update {Path}; SSH environments will not be reachable until this is fixed",
                _sshConfigPath);
        }
    }

    /// <summary>
    /// Makes a directory owner-only. The files inside are already 0600, but a
    /// traversable directory still leaks which hosts exist and lets anything that
    /// can write there drop a key file of its own next to them.
    /// </summary>
    private void RestrictToOwner(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not restrict {Path} to the owner: {Message}", directory, exception.Message);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not remove the stale key file {Path}: {Message}", path, exception.Message);
        }
    }
}
