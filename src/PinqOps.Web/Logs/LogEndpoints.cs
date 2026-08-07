using System.Globalization;
using PinqOps.Logs;
using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>
/// The collected logs: what is being kept, what it costs, and searching it.
///
/// <para>Admin throughout. A container's output is whatever the application decided
/// to print, which on a bad day is a connection string, a token or somebody's
/// personal data — the live <c>/logs</c> route for one container is already gated,
/// and a searchable archive of every container is strictly more than that.</para>
/// </summary>
public static class LogEndpoints
{
    public static void MapLogEndpoints(this IEndpointRouteBuilder app, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logger);

        app.MapGet("/api/logs", async Task<object?> (LogCollector collector) =>
        {
            await Task.CompletedTask;
            var config = collector.Store.Load();
            var usage = collector.DiskUsage();

            return new
            {
                config.Enabled,
                config.Containers,
                config.RetentionDays,
                maximumContainers = LogCollectionConfig.MaximumContainers,
                usage = usage.Select(entry => new { entry.Container, entry.Bytes }),
                usedBytes = usage.Sum(entry => entry.Bytes),
                // What this could cost at worst, shown before it is turned on rather
                // than discovered when the disk fills.
                worstCaseBytes = LogCollectionConfig.WorstCaseBytes(config.Containers.Count),
                pausedBelowFreeBytes = LogCollector.MinimumFreeBytes,
            };
        });

        app.MapPost("/api/logs", async Task<object?> (HttpContext context, LogCollector collector) =>
        {
            var request = await context.Request.ReadFromJsonAsync<LogCollectionConfig>()
                ?? throw new ArgumentException("Invalid request body.");

            collector.Store.Save(request);
            logger.LogWarning(
                "Log collection is now {State} for {Count} containers",
                request.Enabled ? "on" : "off",
                request.Containers.Count);

            return new { ok = true };
        });

        app.MapGet("/api/logs/search", async Task<object?> (HttpContext context, LogCollector collector) =>
        {
            await Task.CompletedTask;

            var since = DateTimeOffset.TryParse(
                context.Request.Query["since"].ToString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed)
                ? parsed
                : (DateTimeOffset?)null;

            var container = context.Request.Query["container"].ToString();
            var query = new LogQuery(
                Container: container.Length > 0 ? container : null,
                Query: context.Request.Query["q"].ToString(),
                Regex: context.Request.Query["regex"].ToString() == "1",
                Since: since,
                Limit: int.TryParse(context.Request.Query["limit"], out var limit) ? limit : LogSearch.DefaultLimit);

            var result = LogSearch.Run(collector.Read(query.Container), query);
            return new
            {
                items = result.Lines.Select(line => new { line.Container, line.At, line.Text }),
                result.Truncated,
                result.Problem,
            };
        });
    }
}
