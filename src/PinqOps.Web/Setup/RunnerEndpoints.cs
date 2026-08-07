using PinqOps;
using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>
/// The local self-hosted runner's status and logs.
/// </summary>
public static class RunnerEndpoints
{
    public static void MapRunnerEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/runner/local", async Task<object?> (HttpContext context, UiConfigStore store, LocalRunnerService runner) =>
            await runner.GetStatusAsync(ResolveApp(store, context).RunnerDirectory));

        app.MapGet("/api/runner/logs", async Task<object?> (HttpContext context, LocalRunnerService runner) =>
        {
            var unit = context.Request.Query["unit"].ToString();
            if (string.IsNullOrWhiteSpace(unit))
            {
                throw new ArgumentException("A runner unit is required.");
            }

            return new { unit, logs = await runner.GetLogsAsync(unit, 100) };
        });
    }
}
