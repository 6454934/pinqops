using PinqOps.Databases;
using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>
/// The managed databases: what is installed, what it connects with, and moving one
/// to a newer version.
///
/// <para>Admin throughout, reads included — a connection string is a credential, and
/// the listing says which databases this server holds.</para>
/// </summary>
public static class DatabaseEndpoints
{
    public static void MapDatabaseEndpoints(this IEndpointRouteBuilder app, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logger);

        app.MapGet("/api/databases", async Task<object?> (DatabaseService databases) =>
            new { items = await databases.ListAsync() });

        app.MapGet("/api/databases/{id}/connection", async Task<IResult> (
            string id, HttpContext context, DatabaseService databases) =>
        {
            await Task.CompletedTask;
            var database = context.Request.Query["database"].ToString();
            var connection = databases.ConnectionStringFor(id, database.Length > 0 ? database : null);
            return connection is null
                ? Error(404, "pinqops has no connection details for that database.")
                : Results.Json(new { connection });
        });

        app.MapPost("/api/databases/{id}/upgrade", async Task<IResult> (
            string id, HttpContext context, DatabaseService databases) =>
        {
            var request = await context.Request.ReadFromJsonAsync<DatabaseUpgradeRequest>();
            if (request?.Version is not { Length: > 0 } version)
            {
                return Error(400, "A version is required.");
            }

            // Long: a dump, a pull, a start and a restore, each of which can be
            // minutes on a database worth upgrading.
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));

            var failure = await databases.UpgradeAsync(id, version, timeout.Token);
            if (failure is not null)
            {
                return Error(400, failure);
            }

            logger.LogWarning("{Database} upgraded to {Version}", id, version);
            return Results.Json(new { ok = true });
        });
    }

    private sealed record DatabaseUpgradeRequest(string? Version);
}
