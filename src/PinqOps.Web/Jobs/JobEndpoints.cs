using PinqOps.Scheduling;
using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>
/// The scheduled jobs: what they are, when they ran, and a way to run one now.
///
/// <para>Admin throughout. A job is an arbitrary command in a container of the
/// operator's choosing — there is no narrower thing it could be, and no useful
/// version of "a deployer may edit one".</para>
/// </summary>
public static class JobEndpoints
{
    public static void MapJobEndpoints(this IEndpointRouteBuilder app, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logger);

        app.MapGet("/api/jobs", async Task<object?> (JobService jobs) =>
        {
            await Task.CompletedTask;
            var runs = jobs.History();
            return new
            {
                items = jobs.Store.Load().Select(job => new
                {
                    job.Id,
                    job.Name,
                    job.Enabled,
                    job.Cron,
                    job.Kind,
                    job.Target,
                    job.Command,
                    job.TimeoutSeconds,
                    job.Retries,
                    job.NotifyOnFailure,
                    running = jobs.IsRunning(job.Id),
                    // The last outcome beside the job, so the list answers "is this
                    // working" without a second click per row.
                    last = runs.FirstOrDefault(run => run.JobId == job.Id),
                }),
            };
        });

        app.MapGet("/api/jobs/runs", async Task<object?> (HttpContext context, JobService jobs) =>
        {
            await Task.CompletedTask;
            var jobId = context.Request.Query["jobId"].ToString();
            return new { items = jobs.History(jobId.Length > 0 ? jobId : null) };
        });

        app.MapPost("/api/jobs", async (HttpContext context, JobService jobs) =>
        {
            var request = await ReadJobAsync(context);
            if (request.Error is not null)
            {
                return Error(400, request.Error);
            }

            var job = request.Job!;
            job.Id = ScheduledJobStore.NewId();
            job.CreatedAt = DateTimeOffset.UtcNow;
            jobs.Store.Update(stored =>
            {
                stored.Add(job.Normalized());
                return 0;
            });

            logger.LogWarning("Scheduled job '{Name}' created ({Cron})", job.Name, job.Cron);
            return Results.Json(new { id = job.Id });
        });

        app.MapPost("/api/jobs/{id}", async (string id, HttpContext context, JobService jobs) =>
        {
            var request = await ReadJobAsync(context);
            if (request.Error is not null)
            {
                return Error(400, request.Error);
            }

            var replaced = jobs.Store.Update(stored =>
            {
                var index = stored.FindIndex(job => string.Equals(job.Id, id, StringComparison.Ordinal));
                if (index < 0)
                {
                    return false;
                }

                var job = request.Job!;
                job.Id = id;
                job.CreatedAt = stored[index].CreatedAt;
                stored[index] = job.Normalized();
                return true;
            });

            if (!replaced)
            {
                return Error(404, "Unknown job.");
            }

            logger.LogWarning("Scheduled job '{Name}' edited", request.Job!.Name);
            return Results.Json(new { ok = true });
        });

        app.MapDelete("/api/jobs/{id}", (string id, JobService jobs) =>
        {
            var removed = jobs.Store.Update(stored =>
                stored.RemoveAll(job => string.Equals(job.Id, id, StringComparison.Ordinal)));

            if (removed == 0)
            {
                return Error(404, "Unknown job.");
            }

            logger.LogWarning("Scheduled job {Id} deleted", id);
            return Results.Json(new { ok = true });
        });

        app.MapPost("/api/jobs/{id}/run", async (string id, JobService jobs) =>
        {
            var job = jobs.Store.Load().Find(stored => string.Equals(stored.Id, id, StringComparison.Ordinal));
            if (job is null)
            {
                return Error(404, "Unknown job.");
            }

            // Runs to completion rather than in the background: a run-now is a
            // person waiting to see what happened, and the timeout already bounds
            // how long that can be.
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(job.Normalized().TimeoutSeconds * (job.Normalized().Retries + 1) + 10));

            var run = await jobs.RunGuardedAsync(job, timeout.Token);
            return run is null
                ? Error(409, "This job is already running.")
                : Results.Json(run);
        });
    }

    private static async Task<(ScheduledJobDefinition? Job, string? Error)> ReadJobAsync(HttpContext context)
    {
        ScheduledJobDefinition? job;
        try
        {
            job = await context.Request.ReadFromJsonAsync<ScheduledJobDefinition>();
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, "Invalid request body.");
        }

        if (job is null)
        {
            return (null, "Invalid request body.");
        }

        // Validated before it is stored, not before it is run: a job that only fails
        // at 3 a.m. fails in a log nobody is reading.
        return ScheduledJobValidator.Validate(job) is { } problem ? (null, problem) : (job, null);
    }
}
