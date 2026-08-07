using System.Globalization;
using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>
/// The traffic summary. Admin: it names every domain this server answers for and
/// every route within them.
/// </summary>
public static class TrafficEndpoints
{
    /// <summary>How far back the page looks by default.</summary>
    public const int DefaultHours = 24;

    public static void MapTrafficEndpoints(this IEndpointRouteBuilder app, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logger);

        app.MapGet("/api/traffic", async Task<object?> (HttpContext context, TrafficService traffic) =>
        {
            await Task.CompletedTask;

            var hours = int.TryParse(context.Request.Query["hours"], out var asked)
                ? Math.Clamp(asked, 1, 24 * 30)
                : DefaultHours;

            var since = DateTimeOffset.UtcNow.AddHours(-hours);
            return new
            {
                traffic.Enabled,
                hours,
                since,
                items = traffic.Enabled ? traffic.Summarise(since) : [],
            };
        });

        app.MapPost("/api/traffic", async Task<IResult> (HttpContext context, TrafficService traffic) =>
        {
            var request = await context.Request.ReadFromJsonAsync<TrafficRequest>();
            var failure = await traffic.SetEnabledAsync(request?.Enabled ?? false);
            return failure is null ? Results.Json(new { ok = true }) : Error(400, failure);
        });
    }

    private sealed record TrafficRequest(bool Enabled);
}
