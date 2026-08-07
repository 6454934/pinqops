using PinqOps.Proxy;

namespace PinqOps.Web;

/// <summary>
/// Starts a background Cloudflare HTTPS provision for one domain.
///
/// <para>This lives outside <see cref="DomainEndpoints"/> because a provision is
/// no longer only something an operator asks for: a domain whose DNS had not
/// propagated when it was added stays <c>ProxyDeferred</c> — invisible to Caddy —
/// until a provision succeeds, and <see cref="DomainProvisionRetryWorkSource"/>
/// retries it on a timer so that recovery does not depend on someone noticing and
/// pressing Point here.</para>
/// </summary>
public static class DomainProvisionRunner
{
    /// <summary>
    /// Starts the job, or returns null when one is already running for that name.
    /// The work runs detached: callers get the job to poll, not the outcome.
    /// </summary>
    public static DomainProvisionJobs.Job? Start(
        DomainProvisionJobs jobs,
        CloudflareHttpsProvisioner provisioner,
        ProxyService proxy,
        ILogger logger,
        string domain)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(provisioner);
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(logger);

        var job = jobs.TryStart(domain);
        if (job is null)
        {
            return null;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var progress = new Progress<string>(phase =>
                {
                    if (!job.Finished)
                    {
                        job.Phase = phase;
                    }
                });
                var result = await provisioner
                    .ProvisionAsync(domain, preferProxied: true, progress, job.CancellationToken)
                    .ConfigureAwait(false);
                job.Result = result;
                if (!string.IsNullOrWhiteSpace(result.Error))
                {
                    job.Error = result.Error;
                    job.Phase = DomainProvisionPhases.Error;
                }
                else
                {
                    job.Phase = DomainProvisionPhases.Done;
                }

                logger.LogInformation(
                    "HTTPS provision job {JobId} for {Domain}: dnsOnly={DnsOnly} cert={Cert} proxied={Proxied} error={Error}",
                    job.Id, domain, result.DnsOnlyOk, result.CertReady, result.Proxied, result.Error);
            }
            catch (OperationCanceledException)
            {
                job.Error ??= "Provisioning cancelled.";
                job.Phase = DomainProvisionPhases.Error;
                logger.LogInformation(
                    "HTTPS provision job {JobId} for {Domain} cancelled", job.Id, domain);
            }
            catch (Exception exception)
            {
                job.Error = exception.Message;
                job.Phase = DomainProvisionPhases.Error;
                logger.LogWarning(
                    exception,
                    "HTTPS provision job {JobId} for {Domain} failed: {Message}",
                    job.Id, domain, exception.Message);
                try
                {
                    // Domain is already in the store — keep the Caddy route even if
                    // Cloudflare provision blew up. Skip Apply when the domain was
                    // deleted (cancel + remove) so we do not resurrect a deferred entry.
                    if (proxy.Store.Load().Domains.Exists(entry =>
                            string.Equals(
                                DomainName.NormalizeForLookup(entry.Domain),
                                DomainName.NormalizeForLookup(domain),
                                StringComparison.Ordinal)))
                    {
                        CloudflareHttpsProvisioner.ReleaseProxyDeferred(proxy, domain);
                        await proxy.ApplyAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception applyException)
                {
                    logger.LogWarning(
                        applyException,
                        "HTTPS provision job {JobId}: Apply after failure also failed",
                        job.Id);
                }
            }
            finally
            {
                job.DisposeToken();
            }
        });

        return job;
    }
}
