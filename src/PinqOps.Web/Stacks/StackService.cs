using PinqOps.Stacks;

namespace PinqOps.Web;

/// <summary>What a save did, or why it did not.</summary>
public sealed record StackSaveResult(bool Saved, string? Error);

/// <summary>
/// Hand-written compose projects: the escape hatch for everything the catalog and
/// the app wizard do not cover.
///
/// <para><b>Nothing is written over a working stack until compose has accepted
/// it.</b> The new YAML goes to a candidate file beside the live one, <c>docker
/// compose config</c> is run against that, and only a file compose parses replaces
/// what is running. Writing first and validating after would mean a typo leaves the
/// project unrunnable — and the operator's own last-known-good text gone with
/// it.</para>
///
/// <para><b>Validated in place, not through a pipe.</b> <c>compose config</c>
/// resolves relative paths, <c>env_file:</c> and bind mounts against the file's own
/// directory. A check run in a scratch directory answers about a different project
/// than the one that would actually run.</para>
/// </summary>
public sealed class StackService
{
    private const string DockerExecutable = "docker";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A ceiling on a stack file. Generous for anything hand-written, and the thing
    /// standing between a paste gone wrong and a compose parse that never returns.
    /// </summary>
    public const int MaximumYamlBytes = 256 * 1024;

    private readonly IProcessRunner _processRunner;
    private readonly ILogger<StackService> _logger;
    private readonly string _root;

    public StackService(IProcessRunner processRunner, ILogger<StackService> logger, string? root = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(logger);
        _processRunner = processRunner;
        _logger = logger;
        _root = root ?? StackPaths.DefaultDirectory;
    }

    public string Root => _root;

    public IReadOnlyList<string> List() => StackPaths.List(_root);

    /// <summary>The stack's compose file and dotenv, or nulls when it has none.</summary>
    public (string? Yaml, string? Env) Read(string name)
    {
        var compose = StackPaths.ComposeFile(_root, name);
        var env = StackPaths.EnvFile(_root, name);
        return (
            File.Exists(compose) ? File.ReadAllText(compose) : null,
            File.Exists(env) ? File.ReadAllText(env) : null);
    }

    /// <summary>
    /// Validates and stores a stack. The dotenv is written first because
    /// <c>compose config</c> interpolates it — validating the YAML against the old
    /// environment would accept a file that fails on the very next command.
    /// </summary>
    public async Task<StackSaveResult> SaveAsync(
        string name, string yaml, string? env, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        if (!StackName.IsValid(name))
        {
            return new StackSaveResult(false, $"'{name}' is not a stack name. Use lowercase letters, digits, - and _.");
        }

        if (System.Text.Encoding.UTF8.GetByteCount(yaml) > MaximumYamlBytes)
        {
            return new StackSaveResult(false, $"A stack file may be at most {MaximumYamlBytes / 1024} KB.");
        }

        var directory = StackPaths.DirectoryFor(_root, name);
        Directory.CreateDirectory(directory);

        var envFile = StackPaths.EnvFile(_root, name);
        var previousEnv = File.Exists(envFile) ? await File.ReadAllTextAsync(envFile, cancellationToken) : null;
        if (env is not null)
        {
            await File.WriteAllTextAsync(envFile, env, cancellationToken);
        }

        var candidate = StackPaths.CandidateFile(_root, name);
        await File.WriteAllTextAsync(candidate, yaml, cancellationToken);

        try
        {
            var invalid = await ValidateAsync(directory, candidate, cancellationToken).ConfigureAwait(false);
            if (invalid is not null)
            {
                // Both go back: an environment saved beside a refused file would
                // change what the running stack resolves to on its next restart,
                // from an edit that was rejected.
                await RestoreEnvAsync(envFile, previousEnv, cancellationToken).ConfigureAwait(false);
                return new StackSaveResult(false, invalid);
            }

            File.Move(candidate, StackPaths.ComposeFile(_root, name), overwrite: true);
            _logger.LogWarning("Stack {Name} saved", name);
            return new StackSaveResult(true, null);
        }
        finally
        {
            TryDelete(candidate);
        }
    }

    /// <summary>Runs <c>compose config</c>; null when compose accepted the file.</summary>
    private async Task<string?> ValidateAsync(string directory, string candidate, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);

        var result = await _processRunner.RunAsync(
            DockerExecutable,
            ["compose", "-f", candidate, "config", "--quiet"],
            directory,
            timeout.Token).ConfigureAwait(false);

        if (result.Succeeded)
        {
            return null;
        }

        // Compose's own message names the line and the key. Replacing it with
        // "invalid YAML" would throw away the only useful part.
        var detail = result.StandardError.Trim();
        return detail.Length > 0 ? detail : $"docker compose config exited {result.ExitCode}.";
    }

    /// <summary>Starts or restarts the stack.</summary>
    public Task<string> UpAsync(string name, CancellationToken cancellationToken = default) =>
        RunAsync(name, ["up", "-d"], cancellationToken);

    /// <summary>
    /// Stops the stack and removes its containers and network.
    ///
    /// <para>Without <c>--volumes</c>, always. A stack's volumes are its data, and a
    /// button that reads "stop" must not be the one that deletes a database.</para>
    /// </summary>
    public Task<string> DownAsync(string name, CancellationToken cancellationToken = default) =>
        RunAsync(name, ["down"], cancellationToken);

    public Task<string> PullAsync(string name, CancellationToken cancellationToken = default) =>
        RunAsync(name, ["pull"], cancellationToken);

    /// <summary>What <c>compose ps</c> reports for the stack.</summary>
    public async Task<List<System.Text.Json.JsonElement>> StatusAsync(
        string name, CancellationToken cancellationToken = default)
    {
        var compose = StackPaths.ComposeFile(_root, name);
        if (!File.Exists(compose))
        {
            return [];
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);

        var result = await _processRunner.RunAsync(
            DockerExecutable,
            [.. Project(name, compose), "ps", "-a", "--format", "json"],
            StackPaths.DirectoryFor(_root, name),
            timeout.Token).ConfigureAwait(false);

        return result.Succeeded ? [.. JsonLines.Parse(result.StandardOutput)] : [];
    }

    /// <summary>
    /// Deletes the stack's files. The containers are taken down first — otherwise
    /// "removed" leaves a running project with no file to manage it by, which is a
    /// stack nothing in the dashboard can reach again.
    /// </summary>
    public async Task RemoveAsync(string name, CancellationToken cancellationToken = default)
    {
        var directory = StackPaths.DirectoryFor(_root, name);
        if (!Directory.Exists(directory))
        {
            throw new KeyNotFoundException("Unknown stack.");
        }

        if (File.Exists(StackPaths.ComposeFile(_root, name)))
        {
            try
            {
                await DownAsync(name, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                // Reported, not fatal: a project docker cannot stop still has files
                // the operator asked to be rid of.
                _logger.LogWarning("Stopping stack {Name} before removing it failed: {Detail}", name, exception.Message);
            }
        }

        Directory.Delete(directory, recursive: true);
        _logger.LogWarning("Stack {Name} removed", name);
    }

    private async Task<string> RunAsync(
        string name, IReadOnlyList<string> command, CancellationToken cancellationToken)
    {
        var compose = StackPaths.ComposeFile(_root, name);
        if (!File.Exists(compose))
        {
            throw new KeyNotFoundException("Unknown stack.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);

        var result = await _processRunner.RunAsync(
            DockerExecutable,
            [.. Project(name, compose), .. command],
            StackPaths.DirectoryFor(_root, name),
            timeout.Token).ConfigureAwait(false);

        return result.Succeeded
            ? (result.StandardOutput + result.StandardError).Trim()
            : throw new InvalidOperationException(
                $"docker compose {command[0]} failed: {result.StandardError.Trim()}");
    }

    /// <summary>
    /// The flags that pin every command to this stack.
    ///
    /// <para><c>-p</c> explicitly, rather than letting compose take the project name
    /// from the directory: a file with its own top-level <c>name:</c> would
    /// otherwise run under that instead, and the dashboard would be managing a
    /// project it cannot find again.</para>
    /// </summary>
    private static string[] Project(string name, string composeFile) =>
        ["compose", "-p", name, "-f", composeFile];

    private static async Task RestoreEnvAsync(string envFile, string? previous, CancellationToken cancellationToken)
    {
        if (previous is null)
        {
            TryDelete(envFile);
            return;
        }

        await File.WriteAllTextAsync(envFile, previous, cancellationToken).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
