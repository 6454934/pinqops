using System.Text.Json;

namespace PinqOps.Deploy;

/// <summary>
/// How the deploy sequence should treat this project, beyond what the workflow
/// passes on the command line. Lives beside <c>history.json</c> and
/// <c>notify.json</c> in the project's <c>.pinqops/</c> directory, so the CLI on
/// the runner and the dashboard read the same answer.
/// </summary>
public sealed class DeploySettings
{
    private ReadinessSettings _readiness = new();

    /// <summary>
    /// The HTTP check that runs after the containers are up. Never null: a
    /// hand-edited <c>"readiness": null</c> would otherwise throw on every deploy.
    /// </summary>
    public ReadinessSettings Readiness { get => _readiness; set => _readiness = value ?? new ReadinessSettings(); }

    /// <summary>
    /// How many copies of the app service to run. One — a single container — is
    /// every existing project.
    ///
    /// <para>More than one requires that the proxy publishes the app's host port:
    /// two containers cannot bind the same one, so a project that still publishes
    /// its own port fails to scale with a port collision rather than a message
    /// anybody can act on.</para>
    /// </summary>
    public int Replicas { get; set; } = DefaultReplicas;

    public const int DefaultReplicas = 1;

    /// <summary>
    /// A ceiling, not a recommendation. It exists so a typed-in 500 fails as a form
    /// value rather than as a server that runs out of memory mid-deploy.
    /// </summary>
    public const int MaximumReplicas = 20;

    /// <summary>The stored count, held to something a single server can run.</summary>
    public static int ClampReplicas(int replicas) => Math.Clamp(replicas, DefaultReplicas, MaximumReplicas);

    private string _activeColor = DeployColors.First;
    private string _proxyTarget = string.Empty;

    /// <summary>
    /// What the proxy's routes call this app.
    ///
    /// <para>Recorded here rather than derived, because the CLI on the runner is
    /// what performs a deploy and it knows only a compose file path. Deriving the
    /// name from the repository would be right until the day it was not — and the
    /// symptom would be a cutover that reports success while switching a route that
    /// belongs to nothing.</para>
    /// </summary>
    public string ProxyTarget { get => _proxyTarget; set => _proxyTarget = value ?? string.Empty; }

    /// <summary>
    /// Whether this project is deployed as two colours, so a release has no gap.
    /// Off is every existing project, and off is the ordinary
    /// pull-recreate-health-check deploy this has always done.
    /// </summary>
    public bool BlueGreen { get; set; }

    /// <summary>
    /// The colour currently serving traffic.
    ///
    /// <para><b>Written only after the proxy has been switched over, never
    /// before.</b> That one rule is what makes every crash recoverable: the file on
    /// disk always describes the colour the proxy is actually pointing at, so
    /// whatever a restart finds, it can tell which half of the cutover happened.</para>
    /// </summary>
    public string ActiveColor { get => _activeColor; set => _activeColor = DeployColors.Normalize(value); }

    /// <summary>
    /// Whether the colour that was live before this deploy is left running with no
    /// traffic.
    ///
    /// <para>On by default, and the cost is honest: the app uses twice the memory
    /// all the time. What it buys is a rollback that is a proxy reload rather than a
    /// pull and a restart — under a second, against a version that is already
    /// proven to run on this server.</para>
    /// </summary>
    public bool KeepPreviousColor { get; set; } = true;

    private AutoscaleSettings _autoscale = new();

    /// <summary>
    /// Whether the copy count follows the load. Never null: a hand-edited
    /// <c>"autoscale": null</c> would otherwise throw on every tick.
    /// </summary>
    public AutoscaleSettings Autoscale { get => _autoscale; set => _autoscale = value ?? new AutoscaleSettings(); }

    private string _balancingPolicy = Proxy.LoadBalancingPolicies.RoundRobin;

    /// <summary>
    /// How the proxy spreads requests when there is more than one copy. Kept here
    /// even at one copy, so going back up to three remembers the choice instead of
    /// silently reverting to round robin.
    /// </summary>
    public string BalancingPolicy
    {
        get => _balancingPolicy;
        set => _balancingPolicy = value ?? Proxy.LoadBalancingPolicies.RoundRobin;
    }
}

/// <summary>
/// An HTTP request the app has to answer before a deploy is called a success.
///
/// <para><b>Why this exists next to the compose health check rather than instead
/// of it.</b> <see cref="ComposeHealthChecker"/> asks docker whether the container
/// is running and whether its own HEALTHCHECK is happy. Plenty of applications
/// have no HEALTHCHECK at all, and a process that started and then failed to bind
/// its port is "running" by every measure docker has. Asking the application for a
/// page is the only question that distinguishes started from serving.</para>
///
/// <para><b>Off unless somebody turns it on.</b> A gate that can fail a deploy is
/// not something to switch on underneath people during an upgrade: an app with no
/// route at <c>/</c> would start failing deploys that had worked for a year, and
/// the first anyone would hear of it is a red deploy at three in the morning.</para>
/// </summary>
public sealed class ReadinessSettings
{
    public const string DefaultPath = "/";

    /// <summary>Second only to <see cref="Enabled"/>: everything else has a sane default.</summary>
    public const int DefaultConsecutiveSuccesses = 2;

    private string _path = DefaultPath;

    public bool Enabled { get; set; }

    /// <summary>The path to request, e.g. <c>/healthz</c>. Always starts with a slash.</summary>
    public string Path { get => _path; set => _path = value ?? DefaultPath; }

    /// <summary>Lowest status code that counts as ready.</summary>
    public int ExpectedStatusFrom { get; set; } = 200;

    /// <summary>
    /// Highest status code that counts as ready. The default range ends at 399 so a
    /// redirect to a login page counts — the question is whether the application is
    /// answering, not whether this particular path is public.
    /// </summary>
    public int ExpectedStatusTo { get; set; } = 399;

    /// <summary>How long to wait between attempts.</summary>
    public int IntervalSeconds { get; set; } = 2;

    /// <summary>How long the app has to become ready before the deploy fails.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>How long one request may take.</summary>
    public int RequestTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// How many answers in a row it takes. Two rather than one because a process
    /// that binds its port before it finishes loading answers once and then stops
    /// answering — the deploy would be called green in exactly the window where the
    /// application is least able to serve.
    /// </summary>
    public int ConsecutiveSuccesses { get; set; } = DefaultConsecutiveSuccesses;

    /// <summary>
    /// The same settings with every value inside a range the probe can actually
    /// run. A hand-edited file must never be able to make a deploy hang, spin, or
    /// request something that is not a path.
    /// </summary>
    public ReadinessSettings Normalized() => new()
    {
        Enabled = Enabled,
        Path = NormalizePath(Path),
        ExpectedStatusFrom = Math.Clamp(ExpectedStatusFrom, 100, 599),
        // Never below the lower bound: an inverted range accepts nothing, so every
        // deploy would fail the probe with no indication that the range is why.
        ExpectedStatusTo = Math.Clamp(ExpectedStatusTo, Math.Clamp(ExpectedStatusFrom, 100, 599), 599),
        IntervalSeconds = Math.Clamp(IntervalSeconds, 1, 60),
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 1, 600),
        RequestTimeoutSeconds = Math.Clamp(RequestTimeoutSeconds, 1, 60),
        ConsecutiveSuccesses = Math.Clamp(ConsecutiveSuccesses, 1, 10),
    };

    /// <summary>
    /// A request path and nothing else, given its leading slash. False when the
    /// value could carry a host, a scheme or a control character — it is
    /// concatenated onto a URL, and quietly cleaning one of those up would probe an
    /// address nobody wrote.
    /// </summary>
    public static bool TryNormalizePath(string? path, out string normalized)
    {
        normalized = DefaultPath;

        var value = (path ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            // Nothing written is not a mistake — it means the root.
            return true;
        }

        // A whole URL is not a path. Prepending a slash would make it one —
        // "/http://example.com/" — and leave the operator with a probe that 404s
        // forever against an address they never meant to write.
        if (value.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        if (value[0] != '/')
        {
            value = "/" + value;
        }

        // "//host" is a scheme-relative URL: Uri resolves it to a different server
        // entirely.
        if (value.StartsWith("//", StringComparison.Ordinal)
            || value.Any(character => char.IsControl(character) || character is ' ' or '\\'))
        {
            return false;
        }

        normalized = value;
        return true;
    }

    /// <summary>
    /// The same, for a stored value: a file nobody can fix from the dashboard must
    /// still produce a probe, so an unusable path falls back to the root here rather
    /// than failing every deploy.
    /// </summary>
    private static string NormalizePath(string path) =>
        TryNormalizePath(path, out var normalized) ? normalized : DefaultPath;
}

/// <summary>
/// Reads and writes <c>.pinqops/deploy.json</c>. A missing or corrupt file means
/// "the defaults", never a crash — the same stance every other pinqops store takes,
/// and the one that keeps a bad edit from blocking every deploy on the server.
/// </summary>
public sealed class DeploySettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// One gate per file, shared by every instance addressing it.
    ///
    /// <para>An instance field here served no purpose: every caller news up its own
    /// store, so no two of them ever held the same lock and nothing was serialised
    /// at all.</para>
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lock> Gates =
        new(StringComparer.Ordinal);

    private readonly string _path;

    public DeploySettingsStore(string composeFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeFilePath);
        _path = PinqOpsStatePaths.DeploySettingsFile(composeFilePath);
    }

    private Lock Gate => Gates.GetOrAdd(_path, _ => new Lock());

    public string Path_ => _path;

    public DeploySettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<DeploySettings>(SecureFile.ReadAllText(_path), SerializerOptions)
                    ?? new DeploySettings();
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
        }

        return new DeploySettings();
    }

    public void Save(DeploySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Atomic, and 0600 like every other file in .pinqops/ — the write itself is
        // already crash-safe; the lock only closes the in-process window between a
        // read-modify-write pair.
        lock (Gate)
        {
            SecureFile.WriteAllText(_path, JsonSerializer.Serialize(settings, SerializerOptions));
        }
    }

    /// <summary>
    /// Reads, changes and writes back under one lock, so the settings written are
    /// the ones on disk with only <paramref name="change"/> applied.
    ///
    /// <para><b>Why a callback and not load-then-save.</b> A caller holding a
    /// snapshot writes back every field in it, including the ones somebody else has
    /// changed since — a coloured deploy reads the file before it pulls and saves it
    /// minutes later at the cutover, so a copy count or an autoscale setting edited
    /// in between was silently put back the way it was. Changing one field here
    /// changes one field.</para>
    /// </summary>
    public DeploySettings Update(Action<DeploySettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        lock (Gate)
        {
            var settings = Load();
            change(settings);
            SecureFile.WriteAllText(_path, JsonSerializer.Serialize(settings, SerializerOptions));
            return settings;
        }
    }
}
