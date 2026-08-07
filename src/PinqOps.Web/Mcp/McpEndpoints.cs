using System.Text.Json;
using PinqOps.Mcp;
using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>
/// Remote MCP over Streamable HTTP at <c>/mcp</c>. Cursor (and any MCP client)
/// connects with just a URL + <c>Authorization: Bearer pot_…</c> — no local
/// <c>pinqops</c> binary required.
/// </summary>
public static class McpEndpoints
{
    public static void MapMcp(this WebApplication app)
    {
        // Any valid API token opens the session; each tool call reuses that
        // bearer against /api/*, so deploy/admin scopes still apply per route.
        app.MapMethods("/mcp", new[] { HttpMethods.Post, HttpMethods.Get, HttpMethods.Delete }, HandleAsync)
            .RequireAuthorization(ApiAuthorization.ReadPolicy);
    }

    private static async Task HandleAsync(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method)
            || HttpMethods.IsDelete(context.Request.Method))
        {
            // Streamable HTTP allows GET for server-push SSE; we are
            // request/response only. DELETE session termination is optional.
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = "POST";
            return;
        }

        // Non-browser agents (Cursor) often omit Origin. When Origin is present,
        // only the dashboard's own host is accepted — DNS-rebinding pages on
        // other origins cannot drive /mcp even if they somehow obtain a cookie;
        // the bearer token is still required by authorization above.
        if (context.Request.Headers.Origin is { Count: > 0 } origin
            && !IsTrustedOrigin(context, origin.ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Origin not allowed for /mcp." })
                .ConfigureAwait(false);
            return;
        }

        using var document = await JsonDocument.ParseAsync(context.Request.Body).ConfigureAwait(false);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Object
            && !root.TryGetProperty("id", out _)
            && root.TryGetProperty("method", out _))
        {
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            return;
        }

        var token = ReadBearerToken(context);
        if (string.IsNullOrWhiteSpace(token))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized." }).ConfigureAwait(false);
            return;
        }

        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
        using var http = McpProtocol.CreateClient(baseUrl, token);

        if (root.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var piece = await McpProtocol.HandleAsync(item, http).ConfigureAwait(false);
                if (piece is not null)
                {
                    parts.Add(piece);
                }
            }

            if (parts.Count == 0)
            {
                context.Response.StatusCode = StatusCodes.Status202Accepted;
                return;
            }

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("[" + string.Join(',', parts) + "]").ConfigureAwait(false);
            return;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "Expected a JSON-RPC object." })
                .ConfigureAwait(false);
            return;
        }

        var response = await McpProtocol.HandleAsync(root, http).ConfigureAwait(false);
        if (response is null)
        {
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            return;
        }

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(response).ConfigureAwait(false);
    }

    private static bool IsTrustedOrigin(HttpContext context, string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Host, context.Request.Host.Host, StringComparison.OrdinalIgnoreCase);
    }
}
