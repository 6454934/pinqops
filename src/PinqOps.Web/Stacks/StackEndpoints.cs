using PinqOps.Stacks;
using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>
/// Hand-written compose projects. Admin throughout: a stack file is arbitrary
/// containers, arbitrary bind mounts and arbitrary published ports on this server.
/// </summary>
public static class StackEndpoints
{
    public static void MapStackEndpoints(this IEndpointRouteBuilder app, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logger);

        app.MapGet("/api/stacks", async Task<object?> (StackService stacks) =>
        {
            var items = new List<object>();
            foreach (var name in stacks.List())
            {
                var services = await stacks.StatusAsync(name);
                items.Add(new
                {
                    name,
                    services = services.Count,
                    running = services.Count(service =>
                        service.TryGetProperty("State", out var state)
                        && string.Equals(state.GetString(), "running", StringComparison.OrdinalIgnoreCase)),
                });
            }

            return new { items };
        });

        app.MapGet("/api/stacks/{id}", async Task<IResult> (string id, StackService stacks) =>
        {
            var name = id;
            if (!StackName.IsValid(name))
            {
                return Error(400, $"'{id}' is not a stack name.");
            }

            var (yaml, env) = stacks.Read(name);
            return yaml is null
                ? Error(404, "Unknown stack.")
                : Results.Json(new { name, yaml, env, services = await stacks.StatusAsync(name) });
        }).RequireResourceAccess(ResourceKinds.Stack, ResourceIdSource.RouteId, GrantAccess.View);

        app.MapPost("/api/stacks/{id}", async Task<IResult> (string id, HttpContext context, StackService stacks) =>
        {
            var name = id;
            StackRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<StackRequest>();
            }
            catch (System.Text.Json.JsonException)
            {
                return Error(400, "Invalid request body.");
            }

            if (request?.Yaml is null)
            {
                return Error(400, "A compose file is required.");
            }

            var result = await stacks.SaveAsync(name, request.Yaml, request.Env);
            return result.Saved ? Results.Json(new { ok = true }) : Error(400, result.Error!);
        }).RequireResourceAccess(ResourceKinds.Stack, ResourceIdSource.RouteId, GrantAccess.Manage);

        app.MapPost("/api/stacks/{id}/up", async Task<IResult> (string id, StackService stacks) =>
        {
            var name = id;
            try
            {
                return Results.Json(new { ok = true, output = await stacks.UpAsync(name) });
            }
            catch (KeyNotFoundException)
            {
                return Error(404, "Unknown stack.");
            }
        }).RequireResourceAccess(ResourceKinds.Stack, ResourceIdSource.RouteId, GrantAccess.Manage);

        app.MapPost("/api/stacks/{id}/down", async Task<IResult> (string id, StackService stacks) =>
        {
            var name = id;
            try
            {
                return Results.Json(new { ok = true, output = await stacks.DownAsync(name) });
            }
            catch (KeyNotFoundException)
            {
                return Error(404, "Unknown stack.");
            }
        }).RequireResourceAccess(ResourceKinds.Stack, ResourceIdSource.RouteId, GrantAccess.Manage);

        app.MapPost("/api/stacks/{id}/pull", async Task<IResult> (string id, StackService stacks) =>
        {
            var name = id;
            try
            {
                return Results.Json(new { ok = true, output = await stacks.PullAsync(name) });
            }
            catch (KeyNotFoundException)
            {
                return Error(404, "Unknown stack.");
            }
        }).RequireResourceAccess(ResourceKinds.Stack, ResourceIdSource.RouteId, GrantAccess.Manage);

        app.MapDelete("/api/stacks/{id}", async Task<IResult> (string id, StackService stacks) =>
        {
            var name = id;
            if (!StackName.IsValid(name))
            {
                return Error(400, $"'{id}' is not a stack name.");
            }

            try
            {
                await stacks.RemoveAsync(name);
                return Results.Json(new { ok = true });
            }
            catch (KeyNotFoundException)
            {
                return Error(404, "Unknown stack.");
            }
        }).RequireResourceAccess(ResourceKinds.Stack, ResourceIdSource.RouteId, GrantAccess.Manage);
    }

    private sealed record StackRequest(string? Yaml, string? Env);
}
