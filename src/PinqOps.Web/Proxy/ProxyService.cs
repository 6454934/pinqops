using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PinqOps.Proxy;
using PinqOps.Secrets;

namespace PinqOps.Web;

/// <summary>
/// Manages the optional reverse proxy (a Caddy container) that gives deployed
/// apps real domains with automatic Let's Encrypt TLS. The proxy forwards to
/// each app by container name over the shared <c>pinqops-apps</c> network, so
/// domain access and the plain <c>host:port</c> publish coexist — an app with no
/// domain is unaffected. The dashboard owns the Caddyfile; the container only
/// reloads it.
/// </summary>
public sealed class ProxyService
{
    public const string ContainerName = ProxyGateway.DefaultContainerName;

    /// <summary>
    /// pinqops' own Caddy build rather than <c>caddy:2-alpine</c>. Stock Caddy has
    /// no rate-limiting module and no DNS providers, so per-domain rate limits and
    /// wildcard certificates (which need a DNS-01 challenge) are not configuration
    /// away — they need modules compiled in. The image is built by this repository's
    /// CI with xcaddy and published to GHCR; everything else about the container is
    /// unchanged, including the volumes, so an existing proxy keeps its certificates
    /// across the switch.
    /// </summary>
    public const string Image = ProxyPaths.DefaultImage;

    public const string Directory = ProxyPaths.DefaultDirectory;

    private readonly DockerService _docker;
    private readonly ProxyGateway _gateway;
    private static readonly HttpClient PublicIpClient = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static (DateTimeOffset At, string? Ip) _publicIpCache;

    private readonly SecretStore _secrets;
    private readonly ILogger<ProxyService> _logger;
    private readonly string _directory;

    public ProxyService(
        DockerService docker, IProcessRunner processRunner, SecretStore secrets, ILogger<ProxyService> logger)
        : this(docker, processRunner, secrets, logger, Directory)
    {
    }

    /// <summary>
    /// The same, over a proxy directory of the caller's choosing. Only a test names
    /// one: the running server has exactly one proxy and it lives at
    /// <see cref="Directory"/>, but a sequence that has to be driven to its failure
    /// path must not do that to the real one.
    /// </summary>
    internal ProxyService(
        DockerService docker,
        IProcessRunner processRunner,
        SecretStore secrets,
        ILogger<ProxyService> logger,
        string directory)
    {
        _logger = logger;
        ArgumentNullException.ThrowIfNull(docker);
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _docker = docker;
        _secrets = secrets;
        _directory = directory;
        _gateway = new ProxyGateway(
            processRunner, directory, Image, ContainerName, message => logger.LogWarning("{Message}", message));
    }

    public DomainConfigStore Store => _gateway.Store;

    /// <summary>Where this instance keeps domains.json, the Caddyfile and custom certs.</summary>
    public string DataDirectory => _directory;

    /// <summary>
    /// The regenerate-validate-install-reload path, for the Core pieces that have to
    /// change routes themselves — a coloured deploy's cutover, the reconciler that
    /// puts routes back on the recorded colour. They live in Core so the runner CLI
    /// can perform the same cutover with nothing else running.
    /// </summary>
    public ProxyGateway Gateway => _gateway;

    public async Task<object> StatusAsync()
    {
        var (exists, running) = await _docker.ContainerStateAsync(ContainerName);
        var config = Store.Load();

        // A container's port bindings are fixed when it is created, so a config that
        // has gained a port since then describes a route the running proxy cannot
        // serve. Reported rather than repaired: recreating the proxy is a brief
        // outage for every domain, and that is the operator's call to make.
        var expected = ProxyPortSet.HostPorts(config);
        var portsInSync = !exists
            || ProxyPortSet.Matches(config, await _docker.PublishedPortsAsync(ContainerName));
        // Once our proxy owns 80/443 the bind probe reports them busy — that is
        // expected, not a conflict, so only probe when the proxy is not running.
        var portsFree = running || (HostPort.IsAvailable(80) && HostPort.IsAvailable(443));
        return new
        {
            installed = exists,
            running,
            ports80443Free = portsFree,
            // A non-root dashboard cannot bind privileged ports, so a "busy"
            // result may be a permission artifact rather than a real conflict.
            probeUnreliable = !running && !portsFree && !IsRoot(),
            acmeEmail = config.AcmeEmail,
            staging = config.UseStagingCa,
            domainsCount = config.Domains.Count,
            caddyfilePath = ProxyPaths.CaddyfilePath(_directory),
            portsInSync,
            publishedPorts = expected,
            // Which apps the proxy publishes for, so the dashboard can offer the
            // switch and — when the proxy is down — say which apps that is costing.
            enrolled = config.Ports
                .Where(entry => entry.Enabled)
                .Select(entry => new { entry.Target, entry.HostPort })
                .ToList(),
            // The secret's name, never its value — that is resolved only when the
            // container is created.
            dns = new
            {
                enabled = config.Dns?.Enabled ?? false,
                provider = config.Dns?.Provider ?? string.Empty,
                secretName = config.Dns?.SecretName ?? string.Empty,
                zoneId = config.Dns?.ZoneId ?? string.Empty,
                accountId = config.Dns?.AccountId ?? string.Empty,
                usable = config.Dns?.IsUsable() ?? false,
                recordsAvailable = DnsRecordService.AvailabilityOf(config.Dns).Available,
                providers = DnsProviders.All,
            },
            edge = new
            {
                enabled = config.Edge?.Enabled ?? false,
                trustedRanges = config.Edge?.TrustedRanges.Count ?? 0,
                rangesUpdatedAt = config.Edge?.RangesUpdatedAt,
                staticCacheSeconds = config.Edge?.StaticCacheSeconds ?? 0,
            },
        };
    }

    public Task<object> InstallAsync(string? acmeEmail, bool? staging, bool force) =>
        InstallAsync(acmeEmail, staging, force, dns: null);

    public async Task<object> InstallAsync(string? acmeEmail, bool? staging, bool force, DnsChallenge? dns)
    {
        var (exists, _) = await _docker.ContainerStateAsync(ContainerName);
        if (!exists && !force && !(HostPort.IsAvailable(80) && HostPort.IsAvailable(443)))
        {
            throw new InvalidOperationException(
                "Port 80 or 443 is already in use on this server — free it (is another web server running?) "
                + "before installing the proxy. If pinqops runs as a non-root user, privileged ports can look "
                + "busy even when free; retry to install anyway.");
        }

        System.IO.Directory.CreateDirectory(_directory);

        // The credential is in the container's environment, which is fixed when the
        // container is created — so a DNS change is the one setting a running proxy
        // cannot pick up by reloading, and the container is replaced for it.
        var previousDns = DnsFingerprint(Store.Load().Dns);
        var applied = await _gateway.Update(config =>
        {
            WithAcme(config, acmeEmail, staging);
            if (dns is not null)
            {
                config.Dns = dns;
            }
        });

        if (applied.Failed)
        {
            throw new InvalidOperationException(applied.Error!);
        }

        var current = Store.Load();
        var dnsChanged = !string.Equals(previousDns, DnsFingerprint(current.Dns), StringComparison.Ordinal);

        if (exists && !dnsChanged)
        {
            // Reinstall = pick up the new global settings without a fresh container.
            return Described(applied);
        }

        // Resolved before the running proxy is touched. Reading the DNS secret can
        // fail — it may be missing, or named something the vault will not accept —
        // and failing after the old container is gone turns a refused install into
        // an outage for every domain this server serves. Acquire, then commit.
        var environment = ProxyEnvironment(current, _secrets);

        if (exists)
        {
            await _docker.RemoveContainerAsync(ContainerName, removeVolumes: false);
        }

        var output = await _docker.InstallProxyAsync(
            ContainerName,
            Image,
            ProxyPaths.CaddyfilePath(_directory),
            ProxyPortSet.PublishArguments(current),
            environment);

        await ReconnectAppNetworksAsync();
        return new { ok = true, output, recreated = exists };
    }

    /// <summary>Whether the proxy container is up right now.</summary>
    public async Task<bool> IsRunningAsync() => (await _docker.ContainerStateAsync(ContainerName)).Running;

    /// <summary>
    /// How long Caddy is given to finish in-flight requests when it is asked to
    /// stop. Only matters once the proxy owns an app's port, because that is when
    /// stopping it cuts traffic rather than just pausing certificate renewal.
    /// </summary>
    public const int DrainSeconds = 15;

    /// <summary>Records that the proxy publishes a host port for an app.</summary>
    public async Task SetAppPortAsync(
        string target, int hostPort, string container, int containerPort, CancellationToken cancellationToken = default)
    {
        var applied = await _gateway.Update(
            config =>
            {
                config.Ports.RemoveAll(entry =>
                    entry.HostPort == hostPort
                    || string.Equals(entry.Target, target, StringComparison.OrdinalIgnoreCase));

                config.Ports.Add(new PortEntry
                {
                    HostPort = hostPort,
                    Target = target,
                    TargetContainer = container,
                    TargetPort = containerPort,
                    Enabled = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });

                // Set the first time a port is taken over: from here on, stopping
                // the proxy cuts live requests unless it is given time to finish
                // them.
                if (config.GracePeriodSeconds <= 0)
                {
                    config.GracePeriodSeconds = DrainSeconds;
                }
            },
            cancellationToken);

        if (applied.Failed)
        {
            throw new InvalidOperationException(applied.Error!);
        }
    }

    /// <summary>
    /// Points every route for an app at its replica set, or back at its single
    /// container.
    ///
    /// <para>Both kinds of route, deliberately. An app reached by a domain and by
    /// <c>server:8080</c> is one application: balancing one and not the other would
    /// mean the same request landing on a different number of containers depending
    /// on which address it arrived at, which is exactly the kind of difference
    /// nobody thinks to look for.</para>
    ///
    /// <para>Returns how many routes changed, so a caller can say when there was
    /// nothing to point anywhere.</para>
    /// </summary>
    public async Task<int> SetAppBalancingAsync(
        string target, LoadBalancing? balancing, CancellationToken cancellationToken = default)
    {
        var changed = 0;
        var applied = await _gateway.Update(
            config =>
            {
                foreach (var upstream in RoutesFor(config, target))
                {
                    upstream.Balancing = balancing;
                    changed++;
                }
            },
            cancellationToken);

        if (applied.Failed)
        {
            throw new InvalidOperationException(applied.Error!);
        }

        return changed;
    }

    /// <summary>
    /// The upstream options of every enabled route for an app, created on the
    /// entries that had none. Materialised rather than lazy: the caller mutates what
    /// it yields, and the config is saved when the callback returns.
    /// </summary>
    private static List<UpstreamOptions> RoutesFor(DomainConfig config, string target)
    {
        var routes = new List<UpstreamOptions>();

        foreach (var entry in config.Ports)
        {
            if (string.Equals(entry.Target, target, StringComparison.OrdinalIgnoreCase))
            {
                routes.Add(entry.Upstream ??= new UpstreamOptions());
            }
        }

        foreach (var entry in config.Domains)
        {
            if (string.Equals(entry.Target, target, StringComparison.OrdinalIgnoreCase))
            {
                routes.Add(entry.Upstream ??= new UpstreamOptions());
            }
        }

        return routes;
    }

    /// <summary>Forgets an app's host port. Safe when there is none.</summary>
    public async Task RemoveAppPortAsync(string target, CancellationToken cancellationToken = default)
    {
        var applied = await _gateway.Update(
            config => config.Ports.RemoveAll(entry =>
                string.Equals(entry.Target, target, StringComparison.OrdinalIgnoreCase)),
            cancellationToken);

        if (applied.Failed)
        {
            throw new InvalidOperationException(applied.Error!);
        }
    }

    /// <summary>
    /// Forgets a port entry by its host port — the exit for an entry whose target
    /// app no longer exists, which <see cref="RemoveAppPortAsync"/> cannot name.
    /// </summary>
    public async Task ReleasePortAsync(int hostPort, CancellationToken cancellationToken = default)
    {
        var applied = await _gateway.Update(
            config => config.Ports.RemoveAll(entry => entry.HostPort == hostPort),
            cancellationToken);

        if (applied.Failed)
        {
            throw new InvalidOperationException(applied.Error!);
        }
    }

    /// <summary>
    /// Recreates the proxy container so its published ports match the config.
    ///
    /// <para>A container's <c>-p</c> flags are fixed when it is created, so this is
    /// the only way to change them — and it means 80 and 443 are down for the
    /// moment it takes. That is bounded: the image is local and the ACME state is in
    /// a named volume, so the new container is serving TLS in well under a second
    /// with no certificate re-issue and no exposure to Let's Encrypt's rate
    /// limits.</para>
    /// </summary>
    public async Task<object> RepublishAsync(CancellationToken cancellationToken = default)
    {
        var config = Store.Load();

        // Before the removal, for the same reason the install resolves it first:
        // this one is called while the proxy is serving, so failing after the
        // container is gone is an outage rather than a refusal.
        var environment = ProxyEnvironment(config, _secrets);

        var (exists, _) = await _docker.ContainerStateAsync(ContainerName);
        if (exists)
        {
            await _docker.RemoveContainerAsync(ContainerName, removeVolumes: false);
        }

        var output = await _docker.InstallProxyAsync(
            ContainerName,
            Image,
            ProxyPaths.CaddyfilePath(_directory),
            ProxyPortSet.PublishArguments(config),
            environment);

        await ReconnectAppNetworksAsync();
        _logger.LogWarning(
            "Proxy republished on ports {Ports}", string.Join(", ", ProxyPortSet.HostPorts(config)));

        return new { ok = true, output, ports = ProxyPortSet.HostPorts(config) };
    }

    /// <summary>
    /// Attaches the proxy to every app network.
    ///
    /// <para>A container's networks belong to the container, so a proxy that has
    /// just been created is on none of them — and an app on its own network would be
    /// unreachable, which is a domain going down for a reason nobody would connect
    /// to "the proxy was reinstalled". Run after every create, and idempotent, so a
    /// reinstall is not a special case.</para>
    /// </summary>
    private async Task ReconnectAppNetworksAsync()
    {
        foreach (var network in await _docker.AppNetworksAsync())
        {
            try
            {
                await _docker.ConnectIfMissingAsync(network, ContainerName);
            }
            catch (InvalidOperationException exception)
            {
                // One network that cannot be joined must not stop the others: the
                // rest of the server's domains are worth more than failing here.
                _logger.LogWarning(exception, "Could not attach the proxy to {Network}", network);
            }
        }
    }

    /// <summary>
    /// What the proxy container needs in its environment. Resolving the token here
    /// — at the moment the container is created — is what keeps it out of
    /// <c>domains.json</c> and out of the Caddyfile.
    /// </summary>
    /// <summary>
    /// The environment the proxy container is created with, resolved from the
    /// vault. Everything here can fail, which is why the install resolves it
    /// <em>before</em> it removes the running container.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ProxyEnvironment(DomainConfig config, SecretStore secrets)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(secrets);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (config.Dns is not { } dns || !dns.IsUsable())
        {
            return environment;
        }

        try
        {
            environment[DnsProviders.TokenVariable] =
                secrets.Reveal(SecretScopes.Global, dns.SecretName, version: null).Value;
        }
        catch (KeyNotFoundException exception)
        {
            // Naming a secret that is not there is a misconfiguration worth saying
            // out loud rather than a proxy that silently cannot answer a challenge.
            throw new InvalidOperationException(
                $"The DNS challenge names the secret '{dns.SecretName}', which does not exist. "
                + "Add it under Secrets first.",
                exception);
        }
        catch (ArgumentException exception)
        {
            // A name the vault will not even accept — anything outside letters,
            // digits and underscores, so the dash in "cf-token". The usability
            // check upstream only asks that a name was typed, so this is where an
            // unusable one has to become an answer instead of an unhandled failure.
            throw new InvalidOperationException(
                $"The DNS challenge names '{dns.SecretName}', which is not a usable secret name. "
                + "Use letters, digits and underscores.",
                exception);
        }

        return environment;
    }

    /// <summary>
    /// Identifies the DNS settings without including the token, so a log line or a
    /// comparison never carries the credential. The token's <em>name</em> is enough:
    /// rotating the value means re-running the install anyway.
    /// </summary>
    private static string DnsFingerprint(DnsChallenge? dns) =>
        dns is null || !dns.IsUsable() ? "none" : $"{dns.Provider}/{dns.SecretName}";

    /// <summary>
    /// Regenerates the Caddyfile and hot-reloads it (no downtime) when the proxy
    /// is running. If it is not installed yet the file is still written, so the
    /// routes are already correct the moment it is installed.
    /// </summary>
    public async Task<object> ApplyAsync()
    {
        var applied = await _gateway.Apply();
        if (applied.Failed)
        {
            // The stored config is already durable and the live Caddyfile was left
            // alone, so this reports a proxy that is still serving the previous
            // routes — not a change that was lost.
            throw new InvalidOperationException(applied.Error!);
        }

        return Described(applied);
    }

    /// <summary>
    /// The apply result as the dashboard reads it. <c>reloaded: false</c> is the
    /// documented contract for a proxy that is not running: the file is written and
    /// the routes take effect the moment it starts.
    /// </summary>
    private static object Described(ProxyApplyResult applied) => new
    {
        ok = true,
        applied.Reloaded,
        // Entries the generator refused to emit. A skipped route is one the
        // dashboard lists as enabled and Caddy never serves, so it is surfaced
        // rather than left for someone to find as a refused connection.
        skipped = applied.Skipped.Select(skip => new { skip.What, skip.Reason }).ToList(),
    };

    /// <summary>
    /// Turns edge mode on or off, refetching the CDN's address ranges each time it
    /// is turned on. Enable and refresh are the same call on purpose: a stale trust
    /// list is this feature's failure mode, and a separate refresh button is a thing
    /// people forget to press.
    /// </summary>
    public async Task<object> ConfigureEdgeAsync(
        bool enabled, int staticCacheSeconds, CancellationToken cancellationToken = default)
    {
        // Fetched before anything is written, so a fetch that fails leaves the
        // previous — working — trust list in place rather than an enabled mode that
        // trusts nothing.
        var ranges = enabled
            ? await EdgeRangeFetcher.CloudflareRanges(cancellationToken)
            : [];

        var applied = await _gateway.Update(
            config => config.Edge = new EdgeMode
            {
                Enabled = enabled,
                TrustedRanges = [.. ranges],
                RangesUpdatedAt = DateTimeOffset.UtcNow,
                StaticCacheSeconds = Math.Max(0, staticCacheSeconds),
            },
            cancellationToken);

        if (applied.Failed)
        {
            throw new InvalidOperationException(applied.Error!);
        }

        return new { ok = true, enabled, trustedRanges = ranges.Count, applied.Reloaded };
    }

    /// <summary>
    /// Advisory DNS preflight: does the domain resolve to one of this server's
    /// addresses? A mismatch is a warning (the cert will fail until DNS points
    /// here), never a hard block — a domain could sit behind a CDN.
    ///
    /// <para>When edge mode is on, an address that falls inside a trusted CDN
    /// range also matches: that is what a proxied Cloudflare record resolves to,
    /// and calling it a mismatch would tell the operator to undo the setup that
    /// is working.</para>
    /// </summary>
    public async Task<DnsCheckResult> CheckDnsAsync(string domain, bool allowWildcard = false)
    {
        var (normalized, lookup) = Preflight(domain, allowWildcard);
        var resolved = lookup ? await ResolveLocallyAsync(normalized) : [];
        return await ClassifyAsync(normalized, resolved, lookup);
    }

    /// <summary>
    /// The same verdict, but asking a public resolver when this box's own resolver
    /// has nothing.
    ///
    /// <para>Provisioning uses this instead of <see cref="CheckDnsAsync"/> because
    /// it asks the question about a record pinqops wrote seconds ago. The preflight
    /// on the way in looked the name up while it did not exist, and a caching
    /// resolver holds that NXDOMAIN for the zone's SOA minimum — five minutes on
    /// Cloudflare. Waiting ninety seconds for the local resolver to change its mind
    /// about a name it has cached as absent is a wait that cannot end well; the
    /// authority already has the record, so this asks something that can see it.</para>
    /// </summary>
    public async Task<DnsCheckResult> CheckDnsSeenPubliclyAsync(
        string domain, bool allowWildcard = false, CancellationToken cancellationToken = default)
    {
        var (normalized, lookup) = Preflight(domain, allowWildcard);
        var resolved = lookup ? await ResolveLocallyAsync(normalized) : [];
        if (lookup && resolved.Length == 0)
        {
            resolved = [.. await PublicDnsLookup.ResolveAsync(normalized, cancellationToken)];
        }

        return await ClassifyAsync(normalized, resolved, lookup);
    }

    private static async Task<string[]> ResolveLocallyAsync(string name)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(name);
            return addresses.Select(a => a.ToString()).ToArray();
        }
        catch (SocketException)
        {
            return [];
        }
    }

    private async Task<DnsCheckResult> ClassifyAsync(string normalized, string[] resolved, bool lookup)
    {
        var serverIps = LocalAddresses();
        var publicIp = await PublicIpAsync();
        if (publicIp is not null && !serverIps.Contains(publicIp))
        {
            serverIps = [.. serverIps, publicIp];
        }

        var edge = Store.Load().Edge;
        var behindCdn = edge is { Enabled: true }
            && resolved.Any(ip => IpInTrustedRanges(ip, edge.TrustedRanges));
        var matches = behindCdn || resolved.Any(r => serverIps.Contains(r));
        return new DnsCheckResult(normalized, resolved, serverIps, publicIp, matches, !lookup, behindCdn);
    }

    /// <summary>
    /// Turns edge mode on with fresh Cloudflare ranges when a proxied DNS record
    /// was just written and edge mode is still off. Idempotent when already on.
    /// </summary>
    public async Task EnsureEdgeEnabledForProxiedDnsAsync(CancellationToken cancellationToken = default)
    {
        var edge = Store.Load().Edge;
        if (edge is { Enabled: true })
        {
            return;
        }

        await ConfigureEdgeAsync(
            enabled: true,
            staticCacheSeconds: edge?.StaticCacheSeconds ?? 0,
            cancellationToken).ConfigureAwait(false);
    }

    public static bool IpInTrustedRanges(string ip, IEnumerable<string> ranges)
    {
        if (!IPAddress.TryParse(ip, out var address))
        {
            return false;
        }

        foreach (var range in ranges)
        {
            if (IPNetwork.TryParse(range.Trim(), out var network) && network.Contains(address))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The name the preflight will look up, and whether there is anything to look
    /// up at all.
    ///
    /// <para><paramref name="allowWildcard"/> has to be the same answer the caller
    /// reached. The domain route normalizes with the permission its DNS-01 settings
    /// give it and then passes the result here; normalizing again with the default
    /// refused every wildcard the route had just accepted, which is the one case
    /// DNS-01 exists for.</para>
    ///
    /// <para>A wildcard is not looked up. It has no address of its own, so the
    /// lookup can only come back empty — and an empty answer here renders as "this
    /// domain does not point at this server", which would be a warning about
    /// nothing on a correctly configured wildcard.</para>
    /// </summary>
    internal static (string Name, bool Lookup) Preflight(string domain, bool allowWildcard)
    {
        var name = DomainName.Normalize(domain, allowWildcard);
        return (name, !DomainName.IsWildcard(name));
    }

    private static void WithAcme(DomainConfig config, string? acmeEmail, bool? staging)
    {
        if (acmeEmail is not null)
        {
            config.AcmeEmail = acmeEmail.Trim();
        }

        // Null leaves staging alone: saving the DNS provider must not silently
        // turn a staging install into production (or the other way) because the
        // body omitted the field.
        if (staging is not null)
        {
            config.UseStagingCa = staging.Value;
        }
    }

    private static string[] LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .Where(address => !IPAddress.IsLoopback(address)
                && address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6
                && !address.IsIPv6LinkLocal)
            .Select(address => address.ToString())
            .Distinct()
            .ToArray();

    /// <summary>
    /// This server's public address, as the DNS preflight already works it out.
    /// Exposed so a record can be written to point at it — the two have to agree,
    /// or pinqops would create a record its own check then reports as wrong.
    /// </summary>
    public Task<string?> PublicAddressAsync() => PublicIpAsync();

    private static async Task<string?> PublicIpAsync()
    {
        if (_publicIpCache.Ip is not null && DateTimeOffset.UtcNow - _publicIpCache.At < TimeSpan.FromMinutes(10))
        {
            return _publicIpCache.Ip;
        }

        try
        {
            var ip = (await PublicIpClient.GetStringAsync("https://api.ipify.org")).Trim();
            if (IPAddress.TryParse(ip, out _))
            {
                _publicIpCache = (DateTimeOffset.UtcNow, ip);
                return ip;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // NAT'd server with no reachable metadata service — the NIC addresses
            // are the best we have.
        }

        return _publicIpCache.Ip;
    }

    // A good-enough heuristic: only root can bind privileged ports on Linux.
    private static bool IsRoot() => Environment.UserName == "root";
}

/// <summary>Advisory DNS preflight result for a domain.</summary>
public sealed record DnsCheckResult(
    string Domain,
    string[] ResolvedIps,
    string[] ServerIps,
    string? PublicIp,
    bool Matches,
    bool Wildcard = false,
    bool BehindCdn = false);
