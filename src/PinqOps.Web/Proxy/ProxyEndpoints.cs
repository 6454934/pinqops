using PinqOps;
using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>
/// The reverse-proxy status and install routes (Caddy).
/// </summary>
public static class ProxyEndpoints
{
    public static void MapProxyEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/proxy/status", async Task<object?> (ProxyService proxy) => await proxy.StatusAsync());

        app.MapPost("/api/proxy/install", async Task<object?> (HttpContext context, ProxyService proxy) =>
        {
            ProxyInstallRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<ProxyInstallRequest>();
            }
            catch (System.Text.Json.JsonException)
            {
                request = null;
            }

            // Staging is nullable on purpose: a DNS-provider save that omits it
            // must leave the current CA choice alone rather than force production.
            return await proxy.InstallAsync(
                request?.AcmeEmail, request?.Staging, request?.Force ?? false, request?.Dns);
        });

        // Enrolling is one sequence, not a set of steps a client drives: the app has
        // to release the port before the proxy can bind it, so the middle is a window
        // where nothing is listening and every step after it has to be undoable.
        app.MapPost("/api/proxy/enroll", async Task<object?> (
            HttpContext context, UiConfigStore store, AppPortEnrollment enrollment) =>
        {
            var result = await enrollment.EnrollAsync(ResolveApp(store, context), context.RequestAborted);
            return result.Failed
                ? Error(500, $"Taking over the port failed, so nothing was changed. {result.RolledBackBecause}")
                : new { ok = true, result.HostPort, result.Alias };
        });

        app.MapPost("/api/proxy/unenroll", async Task<object?> (
            HttpContext context, UiConfigStore store, AppPortEnrollment enrollment) =>
        {
            // Reported, not discarded: unenrolling can now put the app back on the
            // proxy instead of throwing, and answering ok to that would tell the
            // operator the port is theirs again when it is not.
            var result = await enrollment.UnenrollAsync(ResolveApp(store, context), context.RequestAborted);
            return result.Failed
                ? Error(500, $"Giving the port back failed, so the proxy still publishes it. {result.RolledBackBecause}")
                : new { ok = true };
        });

        // Separate from enrolling because it is the answer to drift rather than a
        // change of intent: the config already says which ports the proxy should
        // publish, and this makes the container match it.
        app.MapPost("/api/proxy/republish", async Task<object?> (HttpContext context, ProxyService proxy) =>
            await proxy.RepublishAsync(context.RequestAborted));

        // The exit for a port entry whose target app was removed from the
        // dashboard. Both enroll routes resolve an app, so without this the stale
        // entry kept its port published forever, the watchdog kept counting it,
        // and no other app could enrol on the port — only a hand-edit of
        // domains.json recovered.
        app.MapPost("/api/proxy/ports/release", async Task<object?> (
            HttpContext context, UiConfigStore store, ProxyService proxy) =>
        {
            var request = await context.Request.ReadFromJsonAsync<PortReleaseRequest>()
                ?? throw new ArgumentException("Invalid request body.");
            if (!HostPort.IsValid(request.HostPort))
            {
                throw new ArgumentException($"'{request.HostPort}' is not a valid host port (1-65535).");
            }

            var entry = proxy.Store.Load().Ports.Find(candidate => candidate.HostPort == request.HostPort)
                ?? throw new ArgumentException($"The proxy does not publish port {request.HostPort}.");

            // Only stale targets. A port whose app still exists has a proper exit —
            // unenrolling — that also rewrites the app's compose file; releasing it
            // here would leave the app publishing nothing.
            var stillExists = store.Current.Apps.Any(app =>
                    string.Equals(app.Id, entry.Target, StringComparison.OrdinalIgnoreCase))
                || (entry.Target.StartsWith("catalog:", StringComparison.Ordinal)
                    && AppCatalog.Find(entry.Target["catalog:".Length..]) is not null);
            if (stillExists)
            {
                throw new ArgumentException(
                    $"Port {request.HostPort} is enrolled for '{entry.Target}', which still exists — "
                    + "hand the port back from that app instead.");
            }

            await proxy.ReleasePortAsync(request.HostPort, context.RequestAborted);
            await proxy.RepublishAsync(context.RequestAborted);
            return new { ok = true };
        });

        // Turning edge mode on refetches the CDN's ranges, so the same call is both
        // "enable" and "refresh" — a stale trust list is the failure this feature
        // has, and making the fix a separate button people forget about is how it
        // stays stale.
        app.MapPost("/api/proxy/edge", async Task<object?> (HttpContext context, ProxyService proxy) =>
        {
            EdgeModeRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<EdgeModeRequest>();
            }
            catch (System.Text.Json.JsonException)
            {
                request = null;
            }

            return await proxy.ConfigureEdgeAsync(
                request?.Enabled ?? false, request?.StaticCacheSeconds ?? 0, context.RequestAborted);
        });
    }
}
