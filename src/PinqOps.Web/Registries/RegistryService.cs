using PinqOps.Registries;
using PinqOps.Secrets;

namespace PinqOps.Web;

/// <summary>
/// Signs the docker daemon in to a private registry.
///
/// <para><b>The password never becomes an argument.</b> An argument list is visible
/// to every user on the host through <c>ps</c>, and to anything reading
/// <c>/proc</c> — so <c>docker login --password-stdin</c> is not a nicety, it is the
/// only form that does not publish the credential to the machine. It is also never
/// logged, never returned, and never written anywhere but the vault.</para>
///
/// <para><b>What docker does with it afterwards is docker's.</b> A successful login
/// writes the credential to the daemon user's <c>~/.docker/config.json</c>, base64
/// and not encrypted. pinqops does not pretend otherwise; the vault is what keeps it
/// out of <em>pinqops'</em> files, and the login is what makes a pull work.</para>
/// </summary>
public sealed class RegistryService
{
    private const string DockerExecutable = "docker";

    private static readonly TimeSpan LoginTimeout = TimeSpan.FromSeconds(30);

    private readonly IProcessRunner _processRunner;
    private readonly RegistryStore _registries;
    private readonly SecretStore _secrets;
    private readonly ILogger<RegistryService> _logger;

    public RegistryService(
        IProcessRunner processRunner,
        RegistryStore registries,
        SecretStore secrets,
        ILogger<RegistryService> logger)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(registries);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(logger);
        _processRunner = processRunner;
        _registries = registries;
        _secrets = secrets;
        _logger = logger;
    }

    public RegistryStore Store => _registries;

    /// <summary>
    /// Signs in, and records when it worked. Returns null on success or the reason
    /// it failed — docker's own message, which is the one that says whether the
    /// password is wrong or the host is unreachable.
    /// </summary>
    public async Task<string?> LoginAsync(string registryId, CancellationToken cancellationToken = default)
    {
        var registry = _registries.Load().Find(entry => string.Equals(entry.Id, registryId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException("Unknown registry.");

        string password;
        try
        {
            password = _secrets.Reveal(SecretScopes.Global, registry.SecretName, version: null).Value;
        }
        catch (Exception exception) when (exception is KeyNotFoundException or ArgumentException)
        {
            // A name the vault will not accept is refused with a different
            // exception, and this returns the reason rather than throwing it.
            return $"The vault has no entry called '{registry.SecretName}'.";
        }

        var host = RegistryValidator.Normalize(registry.Host);
        var result = await _processRunner.RunAsync(
            DockerExecutable,
            // The username is an argument and the password is not, which is the
            // whole point of the split — one is a name, the other is a secret.
            ["login", "--username", registry.Username.Trim(), "--password-stdin", "--", host],
            workingDirectory: null,
            Timeout(cancellationToken).Token,
            standardInput: password).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            // Docker's message, and only docker's. It reports "unauthorized" or a
            // connection error without ever echoing what was sent.
            var detail = result.StandardError.Trim();
            _logger.LogWarning("Signing in to {Host} failed", host);
            return detail.Length > 0 ? detail : $"docker login exited {result.ExitCode}.";
        }

        _registries.Update(stored =>
        {
            var entry = stored.Find(candidate => string.Equals(candidate.Id, registryId, StringComparison.Ordinal));
            if (entry is not null)
            {
                entry.LastLoginAt = DateTimeOffset.UtcNow;
            }

            return 0;
        });

        _logger.LogWarning("Signed in to {Host} as {User}", host, registry.Username);
        return null;
    }

    /// <summary>
    /// Signs the daemon out of a registry. Called when an entry is removed, so a
    /// credential the operator has deleted from pinqops stops working for pulls too
    /// — otherwise "removed" means only "removed from this list".
    /// </summary>
    public async Task LogoutAsync(string host, CancellationToken cancellationToken = default)
    {
        var result = await _processRunner.RunAsync(
            DockerExecutable,
            ["logout", "--", RegistryValidator.Normalize(host)],
            workingDirectory: null,
            Timeout(cancellationToken).Token).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            // Reported, never fatal: the entry is going either way, and a daemon
            // that was never logged in answers with an error that means nothing.
            _logger.LogInformation("Signing out of {Host} reported: {Detail}", host, result.StandardError.Trim());
        }
    }

    private static CancellationTokenSource Timeout(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(LoginTimeout);
        return source;
    }
}
