using System.Text.Json;

namespace PinqOps.Proxy;

/// <summary>
/// The managed reverse proxy's configuration: which domains route to which
/// containers, and the ACME account settings. Server-global (one proxy serves
/// every app and catalog service), stored as <c>/opt/pinqops/proxy/domains.json</c>
/// so both the dashboard and — later — the runner CLI can read and write it.
/// </summary>
public sealed class DomainConfig
{
    /// <summary>Contact e-mail for Let's Encrypt (recommended, not required).</summary>
    public string AcmeEmail { get; set; } = string.Empty;

    /// <summary>Use the LE staging CA (untrusted certs, no rate limit) for testing.</summary>
    public bool UseStagingCa { get; set; }

    public List<DomainEntry> Domains { get; set; } = [];

    /// <summary>
    /// Host ports the proxy publishes and forwards, for apps reached by
    /// <c>http://server:8080</c> rather than by a domain. Empty on every existing
    /// install, so this changes nothing until something adds an entry.
    /// </summary>
    public List<PortEntry> Ports { get; set; } = [];

    /// <summary>
    /// How long Caddy is given to finish in-flight requests when it is asked to
    /// stop. Zero leaves the directive out, which is what every existing config
    /// renders. It matters when the proxy has to be recreated to change its
    /// published port set: without it, <c>docker stop</c> cuts live requests
    /// instead of draining them.
    /// </summary>
    public int GracePeriodSeconds { get; set; }

    /// <summary>
    /// The DNS-01 challenge settings. Null means none, which is what every existing
    /// install has — and which keeps wildcards refused, because HTTP-01 cannot prove
    /// control of a name that has no host to serve from.
    /// </summary>
    public DnsChallenge? Dns { get; set; }

    /// <summary>
    /// Running behind a CDN. Null means not, which is every existing install and
    /// changes nothing.
    /// </summary>
    public EdgeMode? Edge { get; set; }

    /// <summary>
    /// Whether the proxy writes a JSON access log for the traffic summary. Off is
    /// every existing install and adds nothing to the file.
    /// </summary>
    public bool AccessLog { get; set; }

    /// <summary>
    /// True when at least one enabled, non-deferred domain or port would be
    /// considered for emission (names and container present). Used to tell a
    /// transient empty render apart from an intentional clear — Dns/AccessLog
    /// alone do not count, because they do not produce site blocks.
    /// </summary>
    public bool HasEmittableRoutes() =>
        Domains.Exists(entry =>
            entry is { Enabled: true, ProxyDeferred: false }
            && !string.IsNullOrWhiteSpace(entry.Domain)
            && !string.IsNullOrWhiteSpace(entry.TargetContainer))
        || Ports.Exists(entry =>
            entry.Enabled
            && entry.HostPort > 0
            && !string.IsNullOrWhiteSpace(entry.TargetContainer));

    /// <summary>
    /// True when at least one enabled domain or port is still on file — including
    /// <see cref="DomainEntry.ProxyDeferred"/> ones. A site-less Caddyfile must not
    /// be reloaded while this is true: deferred routes are omitted on purpose, and
    /// reloading the resulting global-only file adapts to an HTTP app with no sites
    /// and tears down every listener (same outage as a header-only <c>{}</c>).
    /// </summary>
    public bool HasRetainedRoutes() =>
        Domains.Exists(entry =>
            entry.Enabled
            && !string.IsNullOrWhiteSpace(entry.Domain)
            && !string.IsNullOrWhiteSpace(entry.TargetContainer))
        || Ports.Exists(entry =>
            entry.Enabled
            && entry.HostPort > 0
            && !string.IsNullOrWhiteSpace(entry.TargetContainer));
}

/// <summary>
/// One host port the proxy owns, forwarded to a container.
///
/// <para>Deliberately separate from <see cref="DomainEntry"/> rather than a
/// nullable domain on it: a domain block gets automatic HTTPS and a bare port
/// block must not (<c>http://server:8080</c> has to keep working exactly as it did
/// when the app published the port itself), and one field that silently switches
/// between those two is the kind of thing nobody notices until certificates start
/// being requested for an IP address.</para>
/// </summary>
public sealed class PortEntry
{
    /// <summary>The port the proxy listens on. Never 80 or 443 — those are its own.</summary>
    public int HostPort { get; set; }

    /// <summary>What this port points at: an app id (slug), or <c>catalog:&lt;id&gt;</c>.</summary>
    public string Target { get; set; } = string.Empty;

    public string TargetContainer { get; set; } = string.Empty;

    public int TargetPort { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// How the proxy talks to the upstream. Null means Caddy's defaults. Present
    /// here as well as on a domain because an app reached by <c>server:8080</c> has
    /// exactly the same streaming problem as one reached by name.
    /// </summary>
    public UpstreamOptions? Upstream { get; set; }
}

/// <summary>One domain routed to one container.</summary>
public sealed class DomainEntry
{
    public string Domain { get; set; } = string.Empty;

    /// <summary>What this domain points at: an app id (slug), or <c>catalog:&lt;id&gt;</c>.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>The container name the proxy forwards to (resolved when added).</summary>
    public string TargetContainer { get; set; } = string.Empty;

    public int TargetPort { get; set; }

    /// <summary>
    /// Whether <see cref="TargetPort"/> was the caller's explicit choice rather
    /// than the app's default. Drift detection needs the difference: an explicit
    /// port is supposed to disagree with the default, and comparing it against the
    /// recomputed default flagged the domain as drifted forever. Null on entries
    /// stored before this field existed, which drift treats as "not explicit".
    /// </summary>
    public bool? TargetPortExplicit { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The response headers the proxy adds. Null means the defaults, which is what
    /// lets an existing <c>domains.json</c> pick them up without being rewritten.
    /// </summary>
    public SecurityHeaders? Security { get; set; }

    /// <summary>The request ceiling, if any. Null and disabled both mean no limit.</summary>
    public RateLimit? RateLimit { get; set; }

    /// <summary>How the proxy talks to the upstream. Null means Caddy's defaults.</summary>
    public UpstreamOptions? Upstream { get; set; }

    /// <summary>
    /// How this domain gets its certificate. <see cref="DomainTlsModes.Acme"/> is
    /// every existing install and leaves Caddy's automatic Let's Encrypt alone.
    /// <see cref="DomainTlsModes.Custom"/> points Caddy at files under the proxy
    /// certs directory instead.
    /// </summary>
    public string TlsMode { get; set; } = DomainTlsModes.Acme;

    /// <summary>
    /// When true, the domain is stored for the dashboard but omitted from the
    /// Caddyfile until HTTPS provision clears the flag (DNS-only wait finished).
    /// Prevents a foreign Apply during WaitingDns from starting ACME on NXDOMAIN.
    /// Absent on older <c>domains.json</c> entries (false).
    /// </summary>
    public bool ProxyDeferred { get; set; }
}

/// <summary>Values <see cref="DomainEntry.TlsMode"/> may hold.</summary>
public static class DomainTlsModes
{
    public const string Acme = "acme";

    public const string Custom = "custom";

    public static bool IsCustom(string? mode) =>
        string.Equals(mode, Custom, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Filesystem locations of the proxy's generated files.</summary>
public static class ProxyPaths
{
    public const string DefaultDirectory = "/opt/pinqops/proxy";

    /// <summary>
    /// pinqops' own Caddy build. Named here rather than in each caller because the
    /// dashboard runs it, the CLI validates against it, and a coloured deploy
    /// reloads it — three copies of one string that must not drift apart, since a
    /// validator built from a different Caddy would accept a file the running one
    /// refuses.
    /// </summary>
    public const string DefaultImage = "ghcr.io/pinqponq/pinqops-caddy:2";

    public static string DomainsFile(string proxyDirectory) => Path.Combine(proxyDirectory, "domains.json");

    /// <summary>
    /// Where the proxy directory is bind-mounted inside the proxy container, so that
    /// what Caddy writes lands on the host.
    ///
    /// <para>Deliberately not <c>/etc/caddy</c>: only the Caddyfile itself is mounted
    /// there, as a single read-only file rather than a directory. A log written
    /// beside it stays in the container's writable layer, where the dashboard cannot
    /// read it, the next recreate of the container destroys it, and it fills the
    /// container rather than the host disk the roll settings were sized for.</para>
    /// </summary>
    public const string LogDirectory = "/etc/caddy/log";

    /// <summary>
    /// Where the proxy writes its access log, inside its own container. Under
    /// <see cref="LogDirectory"/>, so it is the same file the dashboard reads back
    /// through <see cref="AccessLogFile"/>.
    /// </summary>
    public const string AccessLogPath = $"{LogDirectory}/access.log";

    /// <summary>The same file, as the dashboard sees it.</summary>
    public static string AccessLogFile(string proxyDirectory) => Path.Combine(proxyDirectory, "access.log");

    /// <summary>
    /// Custom certificates on the host. The proxy directory is already bind-mounted
    /// into the container at <see cref="LogDirectory"/>, so these files are visible
    /// there without a second volume.
    /// </summary>
    public static string CertsDirectory(string proxyDirectory) => Path.Combine(proxyDirectory, "certs");

    /// <summary>Where Caddy reads custom certs inside its container.</summary>
    public const string CertsDirectoryInContainer = $"{LogDirectory}/certs";

    public static string CaddyfilePath(string proxyDirectory) => Path.Combine(proxyDirectory, "Caddyfile");

    /// <summary>What every candidate file's name starts with.</summary>
    public const string CandidatePrefix = "Caddyfile.candidate";

    /// <summary>
    /// A fresh path to write a regenerated Caddyfile to for validation, before it
    /// replaces the live one. It sits in the proxy directory because that directory
    /// is what a throwaway validating container mounts.
    ///
    /// <para><b>A new name every time.</b> Adding a domain, a deploy's cutover and
    /// the proxy watchdog all apply, and nothing stops two of them overlapping. On
    /// one shared path the second writer replaces the first one's file and the first
    /// one's cleanup deletes the second's — so one validation finds nothing to read
    /// and the other passes a config it never wrote. The second is the dangerous
    /// half: a Caddyfile that would have been rejected gets installed because it was
    /// checked against somebody else's, and the proxy restarts until it is fixed by
    /// hand.</para>
    /// </summary>
    public static string NewCandidatePath(string proxyDirectory) =>
        Path.Combine(
            proxyDirectory,
            $"{CandidatePrefix}-{Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6))}");

    /// <summary>
    /// The last Caddyfile the proxy is known to have accepted. Kept so a config
    /// that validates but that Caddy then refuses to load can be rolled back —
    /// the proxy runs with <c>--restart unless-stopped</c>, so a file it cannot
    /// parse is not one bad reload, it is a restart loop and a total outage.
    /// </summary>
    public static string LastGoodPath(string proxyDirectory) => Path.Combine(proxyDirectory, "Caddyfile.last-good");
}

/// <summary>
/// Loads and saves <see cref="DomainConfig"/> (camelCase JSON, 0600). Writes are
/// atomic (temp + rename) and serialized with a short retry, because the
/// dashboard and the runner CLI may both write during a preview deploy.
/// </summary>
public sealed class DomainConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _directory;
    private readonly string _path;
    private readonly Lock _gate = new();

    public DomainConfigStore(string? proxyDirectory = null)
    {
        _directory = proxyDirectory ?? ProxyPaths.DefaultDirectory;
        _path = ProxyPaths.DomainsFile(_directory);
    }

    public string Path_ => _path;

    /// <summary>
    /// Load, mutate and save under one lock, returning whatever the callback
    /// returns.
    ///
    /// The add, toggle and delete routes each read-modify-write this file. Two that
    /// both loaded before either saved lost one of the changes — a route someone
    /// just added disappearing, or a deleted one coming back. The write itself is
    /// already atomic and cross-process-safe; this closes the in-process window
    /// between the read and it.
    /// </summary>
    public T Update<T>(Func<DomainConfig, T> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_gate)
        {
            var config = Load();
            var result = mutate(config);
            Save(config);
            return result;
        }
    }

    public DomainConfig Load()
    {
        // A concurrent SecureFile write can briefly surface empty or half-read
        // text under FileShare.Delete. Treating that as "no routes" used to
        // regenerate a header-only Caddyfile and wipe the live proxy — retry
        // before giving up.
        const int attempts = 4;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return new DomainConfig();
                }

                var text = SecureFile.ReadAllText(_path);
                if (string.IsNullOrWhiteSpace(text))
                {
                    if (attempt < attempts)
                    {
                        Thread.Sleep(15 * attempt);
                        continue;
                    }

                    return new DomainConfig();
                }

                return JsonSerializer.Deserialize<DomainConfig>(text, SerializerOptions)
                    ?? new DomainConfig();
            }
            catch (JsonException)
            {
                if (attempt < attempts)
                {
                    Thread.Sleep(15 * attempt);
                    continue;
                }

                // A persistently corrupt file means "no routes", never a crash.
                return new DomainConfig();
            }
        }

        return new DomainConfig();
    }

    /// <summary>
    /// Whether the on-disk file still has enabled routes that should emit site
    /// blocks. Retries briefly like <see cref="Load"/> so a concurrent write is
    /// not mistaken for an intentional clear.
    /// </summary>
    public bool DiskHasEmittableRoutes() => DiskMatches(static config => config.HasEmittableRoutes());

    /// <summary>
    /// Whether the on-disk file still has enabled domains or ports — including
    /// deferred ones. The empty-Caddyfile guard asks this so a WaitingDns domain
    /// cannot authorize a site-less reload that kills the proxy.
    /// </summary>
    public bool DiskHasRetainedRoutes() => DiskMatches(static config => config.HasRetainedRoutes());

    private bool DiskMatches(Func<DomainConfig, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        const int attempts = 4;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return false;
                }

                var text = SecureFile.ReadAllText(_path);
                if (string.IsNullOrWhiteSpace(text))
                {
                    if (attempt < attempts)
                    {
                        Thread.Sleep(15 * attempt);
                        continue;
                    }

                    return false;
                }

                var config = JsonSerializer.Deserialize<DomainConfig>(text, SerializerOptions);
                return config is not null && predicate(config);
            }
            catch (JsonException)
            {
                if (attempt < attempts)
                {
                    Thread.Sleep(15 * attempt);
                    continue;
                }

                // Present but unreadable — do not treat as an intentional empty clear.
                return new FileInfo(_path).Length > 2;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt < attempts)
                {
                    Thread.Sleep(15 * attempt);
                    continue;
                }

                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Writes through the same primitive every other store uses, which keeps the
    /// two properties this file needs and which a copy of the logic here kept
    /// getting subtly wrong: the temp name is unique per write, so a concurrent
    /// dashboard and CLI writer cannot clobber each other's half-written file,
    /// and the temp is created owner-only <em>before</em> any content reaches it.
    /// </summary>
    public void Save(DomainConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Directory.CreateDirectory(_directory);

        SecureFile.WriteAllText(_path, JsonSerializer.Serialize(config, SerializerOptions));
    }
}

/// <summary>Validation for a routable domain name.</summary>
public static class DomainName
{
    /// <summary>
    /// The stored form of a domain, without the validation <see cref="Normalize"/>
    /// applies. Adding a route stores <c>Normalize</c>'s output, so every route
    /// that looks one up again — toggle, delete — has to fold the caller's spelling
    /// exactly the same way, or a domain stored as <c>app.example.com</c> is never
    /// found when it is addressed by its equally valid absolute form
    /// <c>app.example.com.</c>.
    ///
    /// Lookup deliberately does not validate: a route already on file must stay
    /// removable even if it would no longer pass <c>Normalize</c> (a hand-edited
    /// domains.json, or a rule tightened in a later version).
    /// </summary>
    public static string NormalizeForLookup(string? domain) =>
        (domain ?? string.Empty).Trim().ToLowerInvariant().TrimEnd('.');

    /// <summary>
    /// The stored form of a domain, validated.
    ///
    /// <para><paramref name="allowWildcard"/> is false by default, and the refusal
    /// it produces is not a limitation to be worked around: without a DNS-01
    /// challenge configured, a wildcard certificate genuinely cannot be issued, so
    /// accepting the name would only move the failure to issuance — where nobody
    /// sees it. The caller passes true once a DNS provider is set up.</para>
    /// </summary>
    public static string Normalize(string? domain, bool allowWildcard = false)
    {
        var value = NormalizeForLookup(domain);
        if (value.Length is 0 or > 253)
        {
            throw new ArgumentException("Enter a domain name (up to 253 characters).");
        }

        if (value.Contains('*'))
        {
            if (!allowWildcard)
            {
                throw new ArgumentException(
                    "Wildcard domains are not supported — an HTTP-01 certificate cannot cover them.");
            }

            // Exactly one leading "*." and nothing else: "*.*.example.com" and
            // "a.*.example.com" are not names any certificate authority will issue,
            // and "*example.com" is not a wildcard at all.
            if (!value.StartsWith("*.", StringComparison.Ordinal)
                || value.IndexOf('*', 2) >= 0
                || Uri.CheckHostName(value[2..]) != UriHostNameType.Dns)
            {
                throw new ArgumentException(
                    $"'{domain}' is not a valid wildcard domain — write it as '*.example.com'.");
            }

            return value;
        }

        if (Uri.CheckHostName(value) != UriHostNameType.Dns)
        {
            throw new ArgumentException($"'{domain}' is not a valid domain name.");
        }

        return value;
    }

    /// <summary>Whether a stored domain is a wildcard, and therefore needs DNS-01.</summary>
    public static bool IsWildcard(string? domain) =>
        domain is not null && domain.StartsWith("*.", StringComparison.Ordinal);
}
