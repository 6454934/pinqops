using PinqOps;
using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>
/// Preview environments — one per open pull request — and their manual
/// teardown.
/// </summary>
public static class PreviewEndpoints
{
    public static void MapPreviewEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Preview environments across every app the caller can see: what is on disk
        // and whether it is up. Filtered here, like every other listing — an endpoint
        // filter guards one resource and this returns a set.
        app.MapGet("/api/previews", async Task<object?> (
            HttpContext context, UiConfigStore store, ResourceVisibility visibility, PreviewService previews) =>
        {
            var visible = visibility.Visible(context, ResourceKinds.App, store.Current.Apps, connection => connection.Id);
            return new { items = await previews.ListAsync(visible) };
        });

        // Manual teardown — the fallback for a PR that closed while the runner was
        // offline (the workflow's preview-teardown normally handles it).
        app.MapPost("/api/previews/{appId}/{pr:int}/teardown", async Task<object?> (
            string appId, int pr, HttpContext context, UiConfigStore store, PreviewService previews) =>
        {
            var connection = ResolveApp(store, context, appId);
            return await previews.TeardownAsync(connection, pr);
        });
    }
}
