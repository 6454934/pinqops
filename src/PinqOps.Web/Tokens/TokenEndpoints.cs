using System.Globalization;
using static System.Globalization.CultureInfo;
using PinqOps;
using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>
/// API tokens that let agents and scripts drive the REST API.
/// </summary>
public static class TokenEndpoints
{
    public static void MapTokenEndpoints(this IEndpointRouteBuilder app, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logger);

        app.MapGet("/api/tokens", async Task<object?> (ApiTokenStore tokens) =>
        {
            await Task.CompletedTask;
            return new
            {
                items = tokens.List().Select(t => new
                {
                    t.Id, t.Name, t.Scope, t.Last4, t.CreatedAt, t.CreatedBy, t.LastUsedAt, t.ExpiresAt,
                    expired = t.IsExpired(DateTimeOffset.UtcNow),
                }),
            };
        });

        app.MapPost("/api/tokens", async Task<object?> (HttpContext context, ApiTokenStore tokens) =>
        {
            var request = await context.Request.ReadFromJsonAsync<TokenCreateRequest>();
            var scope = request?.Scope is "read" or "deploy" or "admin" ? request.Scope : "read";
            var (token, plaintext) = tokens.Create(
                request?.Name ?? "token",
                scope,
                DateTimeOffset.UtcNow,
                request?.ExpiresInDays,
                // Captured here because it can only be captured here: who minted a
                // token is not recoverable afterwards.
                context.Items["user"] as string);
            logger.LogWarning(
                "API token '{Name}' created with scope {Scope} by {Actor}, expires {Expiry}",
                token.Name, token.Scope, token.CreatedBy, token.ExpiresAt?.ToString("u", InvariantCulture) ?? "never");
            // The plaintext is returned exactly once, here.
            return new { ok = true, id = token.Id, token = plaintext, scope = token.Scope, expiresAt = token.ExpiresAt };
        });

        app.MapDelete("/api/tokens/{id}", async Task<object?> (string id, ApiTokenStore tokens) =>
        {
            await Task.CompletedTask;
            tokens.Delete(id);
            return new { ok = true };
        });
    }
}
