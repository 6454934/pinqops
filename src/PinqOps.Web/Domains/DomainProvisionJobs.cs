using System.Collections.Concurrent;

namespace PinqOps.Web;

/// <summary>
/// In-memory Cloudflare HTTPS provision jobs. Provision can take minutes
/// (DNS wait + ACME); the UI polls until <c>done</c> or <c>error</c>.
/// </summary>
public sealed class DomainProvisionJobs
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(15);

    private const int MaxJobs = 200;

    private readonly ConcurrentDictionary<string, Job> _jobs = new();

    private readonly object _startGate = new();

    public sealed class Job
    {
        private readonly CancellationTokenSource _cts = new();

        public required string Id { get; init; }

        public required string Domain { get; init; }

        public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

        private volatile object? _completedAt;

        public DateTimeOffset? CompletedAt => (DateTimeOffset?)_completedAt;

        private volatile string _phase = DomainProvisionPhases.Saving;

        public string Phase
        {
            get => _phase;
            set
            {
                _phase = value;
                if (value is DomainProvisionPhases.Done or DomainProvisionPhases.Error)
                {
                    _completedAt ??= DateTimeOffset.UtcNow;
                }
            }
        }

        public volatile string? Error;

        public CloudflareHttpsProvisionResult? Result { get; set; }

        public CancellationToken CancellationToken => _cts.Token;

        public bool Finished => Phase is DomainProvisionPhases.Done or DomainProvisionPhases.Error;

        public void Cancel()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void DisposeToken()
        {
            try
            {
                _cts.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>
    /// Starts a job for <paramref name="domain"/>, or returns null when one is
    /// already running for that name.
    /// </summary>
    public Job? TryStart(string domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        Prune();
        lock (_startGate)
        {
            if (_jobs.Values.Any(job => !job.Finished
                    && string.Equals(job.Domain, domain, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            var job = new Job
            {
                Id = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)),
                Domain = domain,
            };
            _jobs[job.Id] = job;
            return job;
        }
    }

    /// <summary>
    /// Whether a provision is in flight for <paramref name="domain"/>. The retry
    /// scheduler asks before reporting a domain as due, so a tick that lands during
    /// a two-minute ACME wait does not queue a second attempt behind it.
    /// </summary>
    public bool IsRunning(string domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        return _jobs.Values.Any(job => !job.Finished
            && string.Equals(job.Domain, domain, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Cancels every unfinished job for <paramref name="domain"/>. Returns how
    /// many were cancelled.
    /// </summary>
    public int CancelDomain(string domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        var count = 0;
        foreach (var job in _jobs.Values)
        {
            if (!job.Finished
                && string.Equals(job.Domain, domain, StringComparison.OrdinalIgnoreCase))
            {
                job.Cancel();
                job.Error ??= "Provisioning cancelled.";
                job.Phase = DomainProvisionPhases.Error;
                count++;
            }
        }

        return count;
    }

    public Job? Find(string jobId)
    {
        Prune();
        return _jobs.GetValueOrDefault(jobId);
    }

    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow - Retention;
        foreach (var (id, job) in _jobs)
        {
            if (job.Finished && (job.CompletedAt ?? job.StartedAt) < cutoff)
            {
                if (_jobs.TryRemove(id, out var removed))
                {
                    removed.DisposeToken();
                }
            }
        }

        // Cap only by dropping finished jobs — never delete an in-flight provision.
        while (_jobs.Count > MaxJobs)
        {
            var oldestFinished = _jobs
                .Where(pair => pair.Value.Finished)
                .OrderBy(pair => pair.Value.CompletedAt ?? pair.Value.StartedAt)
                .FirstOrDefault();
            if (oldestFinished.Key is null || !_jobs.TryRemove(oldestFinished.Key, out var removed))
            {
                break;
            }

            removed.DisposeToken();
        }
    }
}

/// <summary>Phase strings reported to the dashboard during Cloudflare HTTPS provision.</summary>
public static class DomainProvisionPhases
{
    public const string Saving = "saving";

    public const string WritingDns = "writingDns";

    public const string WaitingDns = "waitingDns";

    public const string Applying = "applying";

    public const string WaitingCert = "waitingCert";

    public const string Proxying = "proxying";

    public const string Done = "done";

    public const string Error = "error";
}
