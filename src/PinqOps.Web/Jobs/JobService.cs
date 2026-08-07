using PinqOps.Alerts;
using PinqOps.Scheduling;

namespace PinqOps.Web;

/// <summary>
/// Runs the scheduled jobs and keeps what each run produced.
///
/// <para><b>Two containers, one shape.</b> An <c>exec</c> job runs its command in a
/// container that is already up; a <c>run</c> job starts a throwaway one from an
/// image. Both go to docker as a discrete argument list with <c>--</c> before the
/// first caller-supplied value, so nothing in a command can be read as a flag.</para>
///
/// <para><b>One run of a job at a time.</b> A job that takes eleven minutes on a
/// ten-minute schedule would otherwise pile up until the host gives out, and the
/// second copy of a database dump is not a second dump — it is two processes writing
/// one file.</para>
/// </summary>
public sealed class JobService
{
    private const string DockerExecutable = "docker";

    /// <summary>How many runs are kept. Enough to see a pattern, bounded so it cannot grow forever.</summary>
    public const int HistoryLines = 500;

    /// <summary>Reported when the job outlived its timeout, the way <c>timeout(1)</c> does.</summary>
    private const int TimedOutExitCode = 124;

    /// <summary>Reported when the command could not be started, the way a shell does.</summary>
    private const int CouldNotStartExitCode = 127;

    private readonly IProcessRunner _processRunner;
    private readonly ScheduledJobStore _jobs;
    private readonly RotatingJsonLog _history;
    private readonly AlertDispatcher _dispatcher;
    private readonly ILogger<JobService> _logger;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _running =
        new(StringComparer.Ordinal);

    public JobService(
        IProcessRunner processRunner,
        ScheduledJobStore jobs,
        RotatingJsonLog history,
        AlertDispatcher dispatcher,
        ILogger<JobService> logger)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(logger);
        _processRunner = processRunner;
        _jobs = jobs;
        _history = history;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public ScheduledJobStore Store => _jobs;

    public bool IsRunning(string jobId) => _running.ContainsKey(jobId);

    /// <summary>The runs recorded so far, newest first.</summary>
    public IReadOnlyList<JobRun> History(string? jobId = null)
    {
        var runs = new List<JobRun>();
        foreach (var line in _history.ReadLines(oldestFirst: false))
        {
            try
            {
                if (System.Text.Json.JsonSerializer.Deserialize<JobRun>(line, JsonOptions) is { } run
                    && (jobId is null || string.Equals(run.JobId, jobId, StringComparison.Ordinal)))
                {
                    runs.Add(run);
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // One unreadable line must not hide the rest of the history.
            }
        }

        return runs;
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Runs a job, retrying on failure, and records the outcome. Returns null when
    /// the job is already running — which is not an error, it is the guard doing its
    /// job.
    /// </summary>
    public async Task<JobRun?> RunGuardedAsync(
        ScheduledJobDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (!_running.TryAdd(definition.Id, 0))
        {
            _logger.LogInformation("Job {Job} is still running from its last turn; skipping this one", definition.Name);
            return null;
        }

        try
        {
            var run = await RunAsync(definition, cancellationToken).ConfigureAwait(false);
            _history.Append(System.Text.Json.JsonSerializer.Serialize(run, JsonOptions));

            if (run.Result != JobResults.Succeeded)
            {
                _logger.LogWarning(
                    "Job {Job} {Result} after {Attempts} attempt(s), exit {Exit}",
                    definition.Name,
                    run.Result,
                    run.Attempts,
                    run.ExitCode);

                if (definition.NotifyOnFailure)
                {
                    await NotifyAsync(definition, run, cancellationToken).ConfigureAwait(false);
                }
            }

            return run;
        }
        finally
        {
            _running.TryRemove(definition.Id, out _);
        }
    }

    private async Task<JobRun> RunAsync(ScheduledJobDefinition definition, CancellationToken cancellationToken)
    {
        var job = definition.Normalized();
        var startedAt = DateTimeOffset.UtcNow;
        var arguments = Arguments(job);

        ProcessResult result = new(0, string.Empty, string.Empty);
        var timedOut = false;
        var attempts = 0;

        for (var attempt = 0; attempt <= job.Retries; attempt++)
        {
            attempts = attempt + 1;
            (result, timedOut) = await AttemptAsync(arguments, job.TimeoutSeconds, cancellationToken)
                .ConfigureAwait(false);
            if (result.Succeeded)
            {
                break;
            }

            if (attempt < job.Retries)
            {
                _logger.LogInformation("Job {Job} failed; retrying ({Attempt} of {Retries})", job.Name, attempt + 1, job.Retries);
            }
        }

        return new JobRun
        {
            Id = ScheduledJobStore.NewId(),
            JobId = job.Id,
            StartedAt = startedAt,
            DurationSeconds = Math.Round((DateTimeOffset.UtcNow - startedAt).TotalSeconds, 1),
            Result = result.Succeeded
                ? JobResults.Succeeded
                : timedOut ? JobResults.TimedOut : JobResults.Failed,
            ExitCode = result.ExitCode,
            Attempts = attempts,
            Output = Truncate(result),
        };
    }

    /// <summary>
    /// One attempt, under its own timeout. The timeout is a separate token from the
    /// caller's so a job that runs long is reported as timed out rather than as the
    /// dashboard shutting down.
    /// </summary>
    private async Task<(ProcessResult Result, bool TimedOut)> AttemptAsync(
        IReadOnlyList<string> arguments, int timeoutSeconds, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var result = await _processRunner
                .RunAsync(DockerExecutable, arguments, workingDirectory: null, timeout.Token)
                .ConfigureAwait(false);
            return (result, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (new ProcessResult(TimedOutExitCode, string.Empty, $"the job was still running after {timeoutSeconds}s"), true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The command could not be started at all — docker missing from the
            // path, not executable, refused by the OS. Left to escape, this went
            // past the retry, past the history entry and past the failure
            // notification, so the job simply stopped happening and the page went on
            // showing it as enabled with no runs. It is a failed attempt like any
            // other, and what went wrong is the only thing that says what to fix.
            _logger.LogWarning(exception, "A job's command could not be started");
            return (new ProcessResult(CouldNotStartExitCode, string.Empty, exception.Message), false);
        }
    }

    /// <summary>
    /// The docker arguments for one job. <c>--</c> before the first caller-supplied
    /// value, so a container named <c>-it</c> or a command starting with a dash is a
    /// value rather than a flag.
    /// </summary>
    internal static IReadOnlyList<string> Arguments(ScheduledJobDefinition job)
    {
        var arguments = job.Kind == JobKinds.Exec
            ? new List<string> { "exec", "--", job.Target.Trim() }
            // --rm so a job that runs every hour does not leave an exited container
            // behind every hour.
            : ["run", "--rm", "--", job.Target.Trim()];

        arguments.AddRange(job.Command);
        return arguments;
    }

    /// <summary>
    /// What the run printed, bounded. A job that prints in a loop must not be able
    /// to fill the disk between two ticks, and the end is what says how it finished.
    /// </summary>
    private static string Truncate(ProcessResult result)
    {
        var output = (result.StandardOutput + result.StandardError).Trim();
        if (output.Length <= ScheduledJobValidator.MaximumOutputCharacters)
        {
            return output;
        }

        return "…(earlier output dropped)…\n" + output[^ScheduledJobValidator.MaximumOutputCharacters..];
    }

    private async Task NotifyAsync(ScheduledJobDefinition job, JobRun run, CancellationToken cancellationToken)
    {
        var host = Environment.MachineName;
        await _dispatcher.SendNoticeAsync(
            FailedEvent,
            $"pinqops @ {host}: scheduled job '{job.Name}' {run.Result.Replace('_', ' ')} "
            + $"after {run.Attempts} attempt(s), exit {run.ExitCode}.",
            new
            {
                @event = FailedEvent,
                host,
                job = job.Name,
                jobId = job.Id,
                result = run.Result,
                exitCode = run.ExitCode,
                attempts = run.Attempts,
                timestamp = run.StartedAt,
            },
            AlertSeverity.Warning,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Distinct from an alert rule's events, so a receiver can route it.</summary>
    public const string FailedEvent = "job_failed";
}
