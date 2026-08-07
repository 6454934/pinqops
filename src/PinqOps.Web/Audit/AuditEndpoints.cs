using PinqOps;
using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>
/// The audit trail and its hash-chain verification (admin only).
/// </summary>
public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/audit", async Task<object?> (HttpContext context, AuditLog audit) =>
        {
            await Task.CompletedTask;
            var limit = int.TryParse(context.Request.Query["limit"], out var requested) ? requested : 100;
            var offset = int.TryParse(context.Request.Query["offset"], out var skip) ? skip : 0;
            var user = context.Request.Query["user"].ToString();
            var action = context.Request.Query["action"].ToString();

            // total is what makes a pager possible: without it the page can only
            // guess whether there is anything after the window it was given.
            var page = audit.ReadPage(limit, offset, user, action);
            return new { items = page.Items, total = page.Total, offset = Math.Max(offset, 0), limit };
        });

        // Re-walks the trail's hash chain. A broken link means the file was edited,
        // truncated or had a line inserted after it was written — see AuditLog for what
        // this does and does not prove.
        app.MapGet("/api/audit/verify", async Task<object?> (HttpContext context, AuditLog audit) =>
        {
            await Task.CompletedTask;
            var result = audit.Verify();
            return new
            {
                ok = result.Ok,
                entries = result.Entries,
                verified = result.Verified,
                firstBrokenIndex = result.FirstBrokenIndex,
            };
        });
    }
}
