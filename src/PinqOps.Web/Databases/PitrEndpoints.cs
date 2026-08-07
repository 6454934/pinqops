using System.Globalization;
using PinqOps.Databases;
using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>
/// Point-in-time recovery for PostgreSQL. Admin throughout — this reads and writes
/// the archive an entire database can be rebuilt from.
/// </summary>
public static class PitrEndpoints
{
    public static void MapPitrEndpoints(this IEndpointRouteBuilder app, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logger);

        app.MapGet("/api/pitr", async Task<object?> (PitrService pitr) =>
        {
            var config = pitr.Store.Load();
            var (backups, lastArchivedAt) = await pitr.StateAsync();
            var window = PointInTimeRecovery.Window(backups, lastArchivedAt, DateTimeOffset.UtcNow);

            return new
            {
                config.Enabled,
                config.Container,
                config.KeepBaseBackups,
                config.LastBaseBackupAt,
                lastArchivedAt,
                // The window a recovery can actually land in, so the page offers a
                // range rather than a free-text box that mostly answers "no".
                from = window?.From,
                to = window?.To,
                baseBackups = backups.Select(backup => new { backup.Name, backup.TakenAt, backup.SizeBytes }),
                // The settings pinqops would add, shown rather than described: an
                // operator who is going to let something edit postgresql.conf is
                // entitled to read the diff first.
                archiveSettings = PointInTimeRecovery.ArchiveSettings(),
            };
        });

        app.MapPost("/api/pitr", async Task<object?> (HttpContext context, PitrService pitr) =>
        {
            var request = await context.Request.ReadFromJsonAsync<PitrConfig>()
                ?? throw new ArgumentException("Invalid request body.");

            pitr.Store.Save(request);
            logger.LogWarning(
                "Point-in-time recovery is now {State} for {Container}",
                request.Enabled ? "on" : "off",
                request.Container);

            return new { ok = true };
        });

        app.MapPost("/api/pitr/basebackup", async Task<IResult> (PitrService pitr) =>
        {
            var failure = await pitr.TakeBaseBackupAsync();
            return failure is null ? Results.Json(new { ok = true }) : Error(400, failure);
        });

        app.MapGet("/api/pitr/plan", async Task<IResult> (HttpContext context, PitrService pitr) =>
        {
            if (!DateTimeOffset.TryParse(
                context.Request.Query["target"].ToString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var target))
            {
                return Error(400, "A moment to recover to is required.");
            }

            var (verdict, from, settings) = await pitr.PlanAsync(target);
            return Results.Json(new
            {
                possible = verdict.Possible,
                blockers = verdict.Blockers,
                from = from is null ? null : new { from.Name, from.TakenAt },
                settings,
            });
        });
    }
}
