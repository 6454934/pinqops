using PinqOps;
using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>
/// Host system info (memory, disk, load, uptime, docker).
/// </summary>
public static class SystemEndpoints
{
    public static void MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/system", (SystemInfoService system) => Results.Json(system.GetInfo()));

        // Reading the zone is a plain read; setting it falls to the default
        // "admin" write scope, which is right — it changes the host.
        app.MapGet("/api/system/timezone", async Task<object?> (
            HostTimeZoneService timeZones, CancellationToken cancellationToken) =>
            await timeZones.GetAsync(cancellationToken));

        app.MapPost("/api/system/timezone", async Task<object?> (
            TimeZoneRequest request, HostTimeZoneService timeZones, CancellationToken cancellationToken) =>
            await timeZones.SetAsync(request?.Zone, cancellationToken));
    }
}

/// <summary>The body of a set-time-zone request.</summary>
/// <param name="Zone">An IANA zone name, e.g. <c>Europe/Istanbul</c>.</param>
public sealed record TimeZoneRequest(string? Zone);
