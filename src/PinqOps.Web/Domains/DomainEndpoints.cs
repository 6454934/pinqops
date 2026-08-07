using PinqOps.DnsRecords;
using PinqOps.Proxy;
using PinqOps;
using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>
/// The <c>/api/domains</c> routes: list, add, toggle, delete and DNS check.
/// </summary>
public static class DomainEndpoints
{
    public static void MapDomainEndpoints(this IEndpointRouteBuilder app, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logger);

        app.MapGet("/api/domains", async Task<object?> (
            HttpContext context,
            UiConfigStore store,
            ProxyService proxy,
            DockerService docker,
            ResourceVisibility visibility) =>
        {
            var config = proxy.Store.Load();
            var appConfig = store.Current;
            var items = new List<object>();

            // A domain is not claimed by a team in its own right — it belongs to
            // whatever it points at, so its visibility is that of its target. One
            // fewer field to keep in step, and a route cannot end up visible to
            // people who cannot see the app it reaches.
            bool CanSee(DomainEntry entry)
            {
                if (PreviewManager.IsPreviewMarker(entry.Target))
                {
                    // A preview route belongs to a pull request rather than to an
                    // app, and its hostname is already advertised on the PR.
                    return true;
                }

                return entry.Target.StartsWith("catalog:", StringComparison.Ordinal)
                    ? visibility.CanView(
                        context,
                        ResourceKinds.Container,
                        ProtectedResource.ContainerForApp(entry.Target["catalog:".Length..]))
                    : visibility.CanView(context, ResourceKinds.App, entry.Target);
            }

            foreach (var entry in config.Domains)
            {
                if (!CanSee(entry))
                {
                    continue;
                }

                var (_, running) = await docker.ContainerStateAsync(entry.TargetContainer);

                // A preview route's target is "preview:<project>:<n>", which
                // ResolveDomainTarget does not (and should not) understand — teaching it
                // that shape would also make it a legal POST /api/domains target. Its
                // container and port come from the preview's lifecycle, not from the app
                // config, so there is nothing here to drift against: the check used to
                // throw for every one of them and permanently flag the row as pointing at
                // the wrong container.
                var isPreview = PreviewManager.IsPreviewMarker(entry.Target);
                var drift = false;
                if (!isPreview)
                {
                    try
                    {
                        // A port the operator chose explicitly is supposed to differ
                        // from the app's default — comparing it against the default
                        // flagged such a domain as drifted forever, with re-adding
                        // unable to clear it.
                        var (container, port) = ResolveDomainTarget(appConfig, entry.Target, null);
                        drift = container != entry.TargetContainer
                            || (entry.TargetPortExplicit != true && port != entry.TargetPort);
                    }
                    catch (ArgumentException)
                    {
                        drift = true; // the target app no longer exists
                    }
                }

                items.Add(new
                {
                    entry.Domain,
                    entry.Target,
                    entry.TargetContainer,
                    entry.TargetPort,
                    entry.Enabled,
                    running,
                    drift,
                    // Held out of the Caddyfile until Cloudflare provision succeeds.
                    // Without this the row looks healthy while the domain answers
                    // nothing at all, which is the state a slow DNS record leaves.
                    pendingDns = entry.ProxyDeferred,
                    // Managed by the preview lifecycle rather than by hand, so the UI can
                    // label it instead of inviting the operator to remove it.
                    managedBy = isPreview ? "preview" : "user",
                    url = $"https://{entry.Domain}",
                    tlsMode = DomainTlsModes.IsCustom(entry.TlsMode) ? DomainTlsModes.Custom : DomainTlsModes.Acme,
                    // Null on file means the defaults, so what is reported is what
                    // the proxy actually sends rather than what happens to be
                    // stored — otherwise every existing domain would read as having
                    // no headers while sending four.
                    security = entry.Security ?? new SecurityHeaders(),
                    rateLimit = entry.RateLimit ?? new RateLimit(),
                    upstream = entry.Upstream ?? new UpstreamOptions(),
                });
            }

            return new { items };
        });

        // Writing the record and checking it are separate on purpose: the check has
        // always worked without a provider and still does, and it is what tells the
        // operator whether a record — however it was added — has propagated.
        app.MapPost("/api/domains/{domain}/dns", async Task<object?> (
            string domain,
            HttpContext context,
            ProxyService proxy,
            DnsRecordService dns,
            CloudflareHttpsProvisioner provisioner,
            DomainProvisionJobs jobs,
            CancellationToken cancellationToken) =>
        {
            // Default proxied: that is what "point through Cloudflare" means. Omitting
            // the body (or the field) must not fall back to DNS-only.
            var proxied = true;
            if (context.Request.ContentLength is > 0)
            {
                var body = await context.Request.ReadFromJsonAsync<DomainDnsPointRequest>();
                if (body?.Proxied is not null)
                {
                    proxied = body.Proxied.Value;
                }
            }

            logger.LogInformation("Point-here requested for {Domain} (proxied={Proxied})", domain, proxied);

            // DNS-only is a direct write. Proxied runs the full provision path so ACME
            // is not raced behind an orange cloud with no origin certificate.
            if (!proxied)
            {
                var record = await dns.Point(domain, proxied: false, cancellationToken);
                logger.LogInformation(
                    "DNS record for {Domain} points at {Address} (proxied=False)",
                    record.Name, record.Address);
                return new
                {
                    ok = true,
                    record.Name,
                    record.Address,
                    proxied = false,
                    dnsOnlyOk = true,
                    dnsReady = true,
                    certReady = false,
                    provisioning = false,
                };
            }

            var alreadyCert = (await DomainTlsService.ProbeAsync(domain, cancellationToken)).Ok;
            var dnsCheck = await proxy.CheckDnsAsync(
                domain, allowWildcard: DomainName.IsWildcard(domain));
            if (alreadyCert && (dnsCheck.Matches || dnsCheck.BehindCdn))
            {
                var record = await dns.Point(domain, proxied: true, cancellationToken);
                logger.LogInformation(
                    "DNS record for {Domain} points at {Address} (proxied=True, cert already live)",
                    record.Name, record.Address);
                return new
                {
                    ok = true,
                    record.Name,
                    record.Address,
                    proxied = true,
                    dnsOnlyOk = true,
                    dnsReady = true,
                    certReady = true,
                    provisioning = false,
                };
            }

            var job = DomainProvisionRunner.Start(jobs, provisioner, proxy, logger, domain);
            if (job is null)
            {
                return Error(409, "HTTPS provisioning is already in progress for this domain.");
            }

            return new
            {
                ok = true,
                name = domain,
                domain,
                jobId = job.Id,
                provisioning = true,
                phase = job.Phase,
            };
        });

        app.MapGet("/api/domains/provision/{jobId}", (string jobId, DomainProvisionJobs jobs) =>
        {
            var job = jobs.Find(jobId);
            if (job is null)
            {
                return Error(404, "Unknown provision job.");
            }

            return DescribeProvisionJob(job);
        });

        app.MapGet("/api/domains/{domain}/tls", async Task<object?> (string domain, DomainTlsService tls) =>
            await tls.StatusAsync(domain));

        app.MapPost("/api/domains/{domain}/tls/csr", async Task<object?> (string domain, DomainTlsService tls) =>
        {
            await Task.CompletedTask;
            return tls.CreateCsr(domain);
        });

        app.MapPost("/api/domains/{domain}/tls/custom", async Task<object?> (
            string domain, HttpContext context, DomainTlsService tls) =>
        {
            var request = await context.Request.ReadFromJsonAsync<DomainCustomTlsRequest>()
                ?? throw new ArgumentException("Invalid request body.");
            return await tls.InstallCustomAsync(domain, request.FullChain ?? string.Empty, request.PrivateKey);
        });

        app.MapPost("/api/domains/{domain}/tls/acme", async Task<object?> (string domain, DomainTlsService tls) =>
            await tls.RevertToAcmeAsync(domain));

        app.MapDelete("/api/domains/{domain}/dns", async Task<object?> (string domain, DnsRecordService dns) =>
        {
            var removed = await dns.Remove(domain);
            logger.LogWarning("Removed {Count} DNS record(s) for {Domain}", removed, domain);
            return new { ok = true, removed };
        });

        // The same wildcard rule the add route applies, so the check the page runs
        // while someone types cannot refuse a name the add would accept.
        app.MapGet("/api/domains/check", async Task<object?> (HttpContext context, ProxyService proxy) =>
            await proxy.CheckDnsAsync(
                context.Request.Query["domain"].ToString(),
                allowWildcard: proxy.Store.Load().Dns?.IsUsable() ?? false));

        app.MapPost("/api/domains", async Task<object?> (
            HttpContext context,
            UiConfigStore store,
            ProxyService proxy,
            DnsRecordService dnsRecords,
            CloudflareHttpsProvisioner provisioner,
            DomainProvisionJobs jobs,
            CancellationToken cancellationToken) =>
        {
            var request = await context.Request.ReadFromJsonAsync<DomainRequest>()
                ?? throw new ArgumentException("Invalid request body.");
            // A wildcard is only accepted once a DNS provider is configured: without
            // one the certificate genuinely cannot be issued, and taking the name
            // anyway would move the failure to issuance where nobody sees it.
            var domain = DomainName.Normalize(
                request.Domain, allowWildcard: proxy.Store.Load().Dns?.IsUsable() ?? false);
            if (string.IsNullOrWhiteSpace(request.Target))
            {
                throw new ArgumentException("A target is required.");
            }

            var (container, port) = ResolveDomainTarget(store.Current, request.Target, request.TargetPort);
            logger.LogInformation(
                "Domain add: {Domain} → {Container}:{Port} (target {Target})",
                domain, container, port, request.Target);

            // The same permission the name was accepted under: the preflight
            // normalizes again, and with the default it would refuse the wildcard
            // this route had just allowed.
            var dns = await proxy.CheckDnsAsync(domain, allowWildcard: DomainName.IsWildcard(domain));
            logger.LogInformation(
                "Domain add DNS preflight for {Domain}: matches={Matches}, behindCdn={BehindCdn}, resolved=[{Resolved}]",
                domain, dns.Matches, dns.BehindCdn, string.Join(", ", dns.ResolvedIps));

            // Update so the read and the write are one step; ApplyAsync stays outside
            // the lock so a slow caddy reload cannot block another config edit.
            var (canPoint, whyNot) = dnsRecords.Availability();

            proxy.Store.Update(config =>
            {
                // Re-adding a domain keeps its header settings and TLS mode unless
                // the caller sends new ones: the routing target, the response headers
                // and the certificate source are separate decisions.
                var previous = config.Domains
                    .Find(d => string.Equals(d.Domain, domain, StringComparison.Ordinal));

                config.Domains.RemoveAll(d => string.Equals(d.Domain, domain, StringComparison.Ordinal));
                config.Domains.Add(new DomainEntry
                {
                    Domain = domain,
                    Target = request.Target,
                    TargetContainer = container,
                    TargetPort = port,
                    TargetPortExplicit = request.TargetPort is not null,
                    Enabled = true,
                    // Cloudflare provision waits for DNS before releasing the route to
                    // Caddy — otherwise a foreign Apply during WaitingDns starts ACME
                    // on NXDOMAIN.
                    ProxyDeferred = canPoint,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Security = request.Security ?? previous?.Security,
                    TlsMode = previous?.TlsMode ?? DomainTlsModes.Acme,
                    RateLimit = previous?.RateLimit,
                    Upstream = previous?.Upstream,
                });
                return true;
            });

            if (canPoint)
            {
                // Background job: DNS-only → wait → Apply/ACME → Proxied. The UI polls
                // phases so a multi-minute wait is not a frozen "Working…" button.
                logger.LogInformation(
                    "Domain add: starting HTTPS provision job for {Domain}", domain);
                var job = DomainProvisionRunner.Start(jobs, provisioner, proxy, logger, domain);
                if (job is null)
                {
                    return Error(409, "HTTPS provisioning is already in progress for this domain.");
                }

                logger.LogInformation("Domain {Domain} → {Container}:{Port} (provision job {JobId})",
                    domain, container, port, job.Id);
                return new
                {
                    ok = true,
                    domain,
                    dnsMatches = dns.Matches,
                    behindCdn = dns.BehindCdn,
                    resolvedIps = dns.ResolvedIps,
                    serverIps = dns.ServerIps,
                    jobId = job.Id,
                    provisioning = true,
                    phase = job.Phase,
                };
            }

            logger.LogInformation(
                "Domain add: applying Caddy for {Domain} (no Cloudflare auto-point: {Reason})",
                domain, whyNot);
            await proxy.ApplyAsync();

            logger.LogInformation("Domain {Domain} → {Container}:{Port}", domain, container, port);
            return new
            {
                ok = true,
                domain,
                dnsMatches = dns.Matches,
                behindCdn = dns.BehindCdn,
                resolvedIps = dns.ResolvedIps,
                serverIps = dns.ServerIps,
                pointed = (bool?)null,
                provisioning = false,
            };
        });

        // Separate from POST /api/domains because the routing target and what the
        // proxy does with a request are separate decisions: changing a header should
        // not re-resolve the app, re-run the DNS preflight, or risk repointing the
        // route as a side effect.
        app.MapPost("/api/domains/{domain}/settings", async Task<object?> (
            string domain, HttpContext context, ProxyService proxy) =>
        {
            var request = await context.Request.ReadFromJsonAsync<DomainSettingsRequest>()
                ?? throw new ArgumentException("Invalid request body.");
            var lookup = DomainName.NormalizeForLookup(domain);

            var found = proxy.Store.Update(config =>
            {
                var entry = config.Domains.Find(d =>
                    string.Equals(DomainName.NormalizeForLookup(d.Domain), lookup, StringComparison.Ordinal));
                if (entry is null)
                {
                    return false;
                }

                // Each half is left alone when it is not sent, so a client that only
                // knows about one of them cannot wipe the other.
                if (request.Security is not null)
                {
                    entry.Security = request.Security;
                }

                if (request.RateLimit is not null)
                {
                    entry.RateLimit = request.RateLimit;
                }

                if (request.Upstream is not null)
                {
                    entry.Upstream = request.Upstream;
                }

                return true;
            });

            if (!found)
            {
                throw new KeyNotFoundException($"No domain '{domain}' is routed here.");
            }

            var applied = await proxy.ApplyAsync();
            logger.LogWarning("Domain {Domain} settings changed", lookup);
            return applied;
        });

        // Both of these fold the caller's spelling with DomainName.NormalizeForLookup —
        // the same fold POST /api/domains stored the entry under. Trimming and
        // lower-casing alone missed the trailing dot Normalize strips, so a domain
        // addressed by its absolute form ("app.example.com.") matched nothing on file.
        app.MapPost("/api/domains/{domain}/toggle", async Task<object?> (string domain, ProxyService proxy) =>
        {
            var normalized = DomainName.NormalizeForLookup(domain);
            var enabled = proxy.Store.Update(config =>
            {
                var entry = config.Domains.FirstOrDefault(d => string.Equals(d.Domain, normalized, StringComparison.Ordinal))
                    ?? throw new KeyNotFoundException($"Unknown domain '{domain}'.");
                entry.Enabled = !entry.Enabled;
                return entry.Enabled;
            });
            await proxy.ApplyAsync();
            return new { ok = true, enabled };
        });

        app.MapPost("/api/domains/{domain}/delete", async Task<object?> (
            string domain, ProxyService proxy, DomainProvisionJobs jobs) =>
        {
            var normalized = DomainName.NormalizeForLookup(domain);
            jobs.CancelDomain(normalized);
            var removed = proxy.Store.Update(config =>
                config.Domains.RemoveAll(d => string.Equals(d.Domain, normalized, StringComparison.Ordinal)));

            // A delete that matched nothing used to answer ok:true and reload caddy
            // anyway, so a mistyped (or differently spelled) domain read as "removed"
            // while the route it was meant to take down carried on serving. Toggle
            // already refused an unknown domain; delete now agrees with it.
            if (removed == 0)
            {
                throw new KeyNotFoundException($"Unknown domain '{domain}'.");
            }

            await proxy.ApplyAsync();
            return new { ok = true };
        });
    }

    private static object DescribeProvisionJob(DomainProvisionJobs.Job job)
    {
        var result = job.Result;
        var error = job.Error ?? result?.Error;
        var succeeded = job.Phase == DomainProvisionPhases.Done
            && string.IsNullOrWhiteSpace(error)
            && result is { CertReady: true };
        return new
        {
            jobId = job.Id,
            domain = job.Domain,
            phase = job.Phase,
            error,
            provisioning = !job.Finished,
            pointed = result is null ? (bool?)null : result.DnsOnlyOk || result.Proxied,
            pointAddress = result?.Address,
            pointError = result?.Error,
            certReady = result?.CertReady,
            proxied = result?.Proxied,
            dnsReady = result?.DnsReady,
            dnsOnlyOk = result?.DnsOnlyOk,
            address = result?.Address,
            ok = succeeded,
        };
    }
}
