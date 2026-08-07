using System.Collections.Concurrent;

namespace PinqOps.Web;

/// <summary>
/// In-memory registry of app-install jobs. Installs run in the background
/// (docker pull can take minutes) and the UI polls a job until it's done, so
/// progress shows without a page refresh. Jobs are pruned after a retention
/// window; at most one job runs per app at a time.
/// </summary>
public sealed class AppInstallJobs
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(15);

    /// <summary>
    /// A hard ceiling on the table, as a backstop for a job that never completes
    /// (the retention window only applies once one is done). Oldest out first.
    /// </summary>
    private const int MaxJobs = 200;

    private readonly ConcurrentDictionary<string, Job> _jobs = new();

    // Guards the "no second job for the same app" check-then-add in TryStart;
    // the dictionary alone can't make that scan-plus-insert atomic.
    private readonly object _startGate = new();

    public sealed class Job
    {
        public required string Id { get; init; }
        public required string AppId { get; init; }

        /// <summary>The environment being installed to; jobs are per-environment.</summary>
        public required string EnvironmentId { get; init; }
        public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// When the job reached a terminal phase, which is what the retention window
        /// is measured from. Measuring from <see cref="StartedAt"/> meant any install
        /// longer than the window was pruned the instant it finished — by the very
        /// poll that should have reported success — so the UI showed "unknown install
        /// job" for an install that had worked. A large image pull is given 30
        /// minutes, i.e. twice the window, so this was the normal case on a slow link.
        /// </summary>
        private volatile object? _completedAt;

        public DateTimeOffset? CompletedAt => (DateTimeOffset?)_completedAt;

        // "pulling" → "starting" → "done" | "error"
        private volatile string _phase = "pulling";

        public string Phase
        {
            get => _phase;
            set
            {
                _phase = value;
                if (value is "done" or "error")
                {
                    _completedAt ??= DateTimeOffset.UtcNow;
                }
            }
        }

        public volatile string? Output;
        public volatile string? Error;

        public bool Done => Phase is "done" or "error";
    }

    /// <summary>
    /// Starts tracking a new job for <paramref name="appId"/> on
    /// <paramref name="environmentId"/>, or returns null when an install for that
    /// app is already running <em>there</em>. The pair is what makes it "one at a
    /// time": the same app installing on two different hosts is not a conflict,
    /// and treating it as one would block the second host for no reason.
    /// </summary>
    public Job? TryStart(string environmentId, string appId)
    {
        Prune();
        lock (_startGate)
        {
            if (_jobs.Values.Any(job => !job.Done
                    && string.Equals(job.AppId, appId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(job.EnvironmentId, environmentId, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            var job = new Job
            {
                Id = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)),
                AppId = appId,
                EnvironmentId = environmentId,
            };
            _jobs[job.Id] = job;
            return job;
        }
    }

    public Job? Find(string jobId)
    {
        Prune();
        return _jobs.GetValueOrDefault(jobId);
    }

    /// <summary>
    /// App ids with an install currently in flight on this environment (for list
    /// badges). Scoped so a install running on one host does not show as
    /// "installing" on another.
    /// </summary>
    public IReadOnlyList<string> ActiveAppIds(string environmentId)
    {
        Prune();
        return _jobs.Values
            .Where(job => !job.Done && string.Equals(job.EnvironmentId, environmentId, StringComparison.OrdinalIgnoreCase))
            .Select(job => job.AppId)
            .ToList();
    }

    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow - Retention;
        foreach (var (id, job) in _jobs)
        {
            // CompletedAt, not StartedAt: the window is "how long a finished job stays
            // readable", and a job is only finished once it says so.
            if (job.Done && (job.CompletedAt ?? job.StartedAt) < cutoff)
            {
                _jobs.TryRemove(id, out _);
            }
        }

        // Backstop for jobs that never reach a terminal phase, which the window above
        // can never remove.
        while (_jobs.Count > MaxJobs)
        {
            var oldest = _jobs.OrderBy(pair => pair.Value.StartedAt).FirstOrDefault();
            if (oldest.Key is null || !_jobs.TryRemove(oldest.Key, out _))
            {
                break;
            }
        }
    }
}
