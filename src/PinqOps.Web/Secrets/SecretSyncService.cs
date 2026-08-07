using PinqOps;
using PinqOps.Secrets;

namespace PinqOps.Web;

/// <summary>
/// Keeps every connected app's <c>.env</c> in step with the secret store.
///
/// <para>Called after each write rather than on a timer: a rotation that only
/// reached the app on the next tick would leave a window where the dashboard
/// reports the new version and the container still holds the old one. The store
/// is the source of truth and <c>.env</c> is derived from it, so a sync is
/// idempotent and safe to repeat.</para>
///
/// <para>A single app that cannot be written — its compose directory has not been
/// created yet, or the file is not writable — must not fail the request that
/// caused the sync: the secret itself was stored successfully, and refusing the
/// whole call would leave the operator unable to manage secrets at all because one
/// unrelated app is in a bad state. Those apps come back as warnings the caller
/// surfaces, and are logged with the underlying exception.</para>
/// </summary>
public sealed class SecretSyncService
{
    private readonly UiConfigStore _config;
    private readonly SecretStore _secrets;
    private readonly ILogger<SecretSyncService> _logger;

    public SecretSyncService(UiConfigStore config, SecretStore secrets, ILogger<SecretSyncService> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(logger);
        _config = config;
        _secrets = secrets;
        _logger = logger;
    }

    /// <summary>
    /// Materialises the current secrets into every app. <paramref name="retiredNames"/>
    /// carries names that have just been deleted from the store: they are no longer
    /// in <see cref="SecretStore.ManagedNames"/>, so without them the value would be
    /// left behind in the <c>.env</c> of every app that had it.
    /// </summary>
    public IReadOnlyList<string> Sync(IEnumerable<string>? retiredNames = null)
    {
        var managed = new HashSet<string>(_secrets.ManagedNames(), StringComparer.Ordinal);
        foreach (var retired in retiredNames ?? [])
        {
            managed.Add(retired);
        }

        var warnings = new List<string>();
        foreach (var app in _config.Current.Apps)
        {
            var warning = SyncApp(app, managed);
            if (warning is not null)
            {
                warnings.Add(warning);
            }
        }

        return warnings;
    }

    private string? SyncApp(AppConnection app, IReadOnlyCollection<string> managedNames)
    {
        var envFile = PinqOpsStatePaths.EnvFile(app.ComposeFile);
        var directory = Path.GetDirectoryName(envFile);

        // An app registered but never published has no compose directory yet. That
        // is an ordinary state, not a failure, and the wizard writes the .env when
        // it creates the project — at which point the next sync fills it in.
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        try
        {
            var materialized = SecretMaterializer.Apply(envFile, _secrets.Resolve(app.Id), managedNames);
            if (materialized.Changed)
            {
                _logger.LogInformation(
                    "Secrets materialised for {AppId}: {Written} written, {Removed} removed",
                    app.Id, materialized.Written.Count, materialized.Removed.Count);
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(exception, "Could not write secrets into {EnvFile}", envFile);
            return app.Id;
        }
    }
}
