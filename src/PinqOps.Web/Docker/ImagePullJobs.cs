using System.Collections.Concurrent;

namespace PinqOps.Web;

/// <summary>
/// Pulls run in the background and the page polls them.
///
/// <para>A pull is the one docker operation with no useful upper bound: a small
/// image on a fast link is seconds, a machine-learning base image on a domestic
/// uplink is half an hour. Holding the request open for that means a proxy timeout
/// somewhere in the middle and a dashboard that reports failure for a pull that is
/// still going perfectly well.</para>
///
/// <para>Deliberately not <c>AppInstallJobs</c> with a flag. That registry is keyed
/// by app and environment and refuses a second install of the same app — correct
/// there, wrong here, where pulling two images at once is an ordinary thing to
/// want.</para>
/// </summary>
public sealed class ImagePullJobs
{
    /// <summary>How long a finished job stays pollable.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(15);

    /// <summary>
    /// A ceiling on the table, as a backstop for a job that never finishes — the
    /// retention window only applies once one is done. Oldest out first.
    /// </summary>
    private const int MaxJobs = 100;

    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly object _startGate = new();

    public sealed class Job
    {
        public required string Id { get; init; }

        public required string Image { get; init; }

        public required string EnvironmentId { get; init; }

        public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

        private volatile object? _completedAt;

        public DateTimeOffset? CompletedAt => (DateTimeOffset?)_completedAt;

        private volatile string _phase = "pulling";

        /// <summary><c>pulling</c> → <c>done</c> | <c>error</c>.</summary>
        public string Phase
        {
            get => _phase;
            set
            {
                _phase = value;
                if (value is "done" or "error")
                {
                    // Measured from completion, not from the start: a thirty-minute
                    // pull would otherwise be pruned by the very poll that should
                    // have reported it succeeded.
                    _completedAt ??= DateTimeOffset.UtcNow;
                }
            }
        }

        public volatile string? Output;

        public volatile string? Error;

        public bool Done => Phase is "done" or "error";
    }

    /// <summary>
    /// Starts a pull, or returns the job already pulling this image on this
    /// environment. Two people asking for the same image at the same moment is one
    /// pull, not two — docker would serialise them anyway, and the second would
    /// report progress nobody could match to their own request.
    /// </summary>
    public Job Start(string image, string environmentId, Func<Job, Task> run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        ArgumentNullException.ThrowIfNull(run);

        lock (_startGate)
        {
            var existing = _jobs.Values.FirstOrDefault(job =>
                !job.Done
                && string.Equals(job.Image, image, StringComparison.Ordinal)
                && string.Equals(job.EnvironmentId, environmentId, StringComparison.Ordinal));
            if (existing is not null)
            {
                return existing;
            }

            Prune();

            var created = new Job
            {
                Id = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)),
                Image = image,
                EnvironmentId = environmentId,
            };
            _jobs[created.Id] = created;

            _ = Task.Run(async () =>
            {
                try
                {
                    await run(created).ConfigureAwait(false);
                    created.Phase = "done";
                }
                catch (Exception exception)
                {
                    created.Error = exception.Message;
                    created.Phase = "error";
                }
            });

            return created;
        }
    }

    public Job? Find(string id) => _jobs.GetValueOrDefault(id);

    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, job) in _jobs)
        {
            if (job.CompletedAt is { } completed && now - completed > Retention)
            {
                _jobs.TryRemove(id, out _);
            }
        }

        while (_jobs.Count >= MaxJobs)
        {
            var oldest = _jobs.Values.OrderBy(job => job.StartedAt).FirstOrDefault();
            if (oldest is null || !_jobs.TryRemove(oldest.Id, out _))
            {
                break;
            }
        }
    }
}
