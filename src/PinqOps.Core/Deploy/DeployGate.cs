namespace PinqOps.Deploy;

/// <summary>
/// One deploy, rollback or apply at a time per compose project — across
/// processes, which the in-process gates cannot promise.
///
/// <para><b>Why a lock file.</b> The runner CLI and the dashboard are two
/// processes over the same <c>.env</c> and the same containers. A CI deploy
/// pinning its tag while a dashboard rollback is mid-pull ends with the
/// containers recreated on whichever tag was pinned last while history records
/// the other operation as successful — both report success, and neither
/// operator's intent ran deterministically. <see cref="EnvFileStore"/>'s lock
/// only serialises single-key writes, not the pin→pull→up sequence.</para>
///
/// <para><b>The handle is the lock.</b> An exclusive <see cref="FileStream"/> on
/// <c>.pinqops/deploy.lock</c>, which the OS releases when the process exits —
/// so a crashed holder cannot wedge the next deploy the way a marker file
/// would.</para>
///
/// <para><b>Best-effort where locking is impossible.</b> A state directory that
/// cannot be created or a lock file that cannot be opened for permission
/// reasons degrades to no gate — the behaviour before this existed — rather
/// than failing a deploy over a lock. Contention is different: a holder that
/// stays alive past the wait budget fails the acquisition loudly, because
/// proceeding anyway is exactly the interleaving this exists to prevent.</para>
/// </summary>
public sealed class DeployGate : IDisposable
{
    /// <summary>
    /// How long a deploy or rollback waits for the current holder. Generous on
    /// purpose: the holder is a real deploy, and queueing behind it is what the
    /// operator meant.
    /// </summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(500);

    private readonly FileStream? _handle;

    private DeployGate(FileStream? handle) => _handle = handle;

    public static string LockFile(string composeFilePath) =>
        Path.Combine(PinqOpsStatePaths.StateDirectory(composeFilePath), "deploy.lock");

    /// <summary>
    /// Takes the project's gate, waiting up to <paramref name="waitFor"/> for the
    /// current holder. Throws <see cref="InvalidOperationException"/> when the
    /// holder outlives the wait.
    /// </summary>
    public static async Task<DeployGate> AcquireAsync(
        string composeFilePath,
        TimeSpan waitFor,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        string lockPath;
        try
        {
            lockPath = LockFile(composeFilePath);
        }
        catch (ArgumentException)
        {
            return new DeployGate(null);
        }

        return await AcquireFileAsync(lockPath, waitFor, log, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The same exclusive-handle lock on an arbitrary path, for callers guarding
    /// something other than a deploy sequence (e.g. a preview's port allocation).
    /// </summary>
    public static async Task<DeployGate> AcquireFileAsync(
        string lockPath,
        TimeSpan waitFor,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new DeployGate(null);
        }

        var deadline = DateTimeOffset.UtcNow + waitFor;
        var announced = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new DeployGate(new FileStream(
                    lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new InvalidOperationException(
                        "a deploy or rollback is in progress for this project — try again when it finishes.");
                }

                if (!announced)
                {
                    announced = true;
                    log?.Invoke("another deploy or rollback holds this project; waiting for it to finish");
                }

                await Task.Delay(RetryInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                // No permission for the lock file; the deploy itself may still be
                // permitted, so do not block it — the same stance EnvFileStore takes.
                return new DeployGate(null);
            }
        }
    }

    public void Dispose() => _handle?.Dispose();
}
