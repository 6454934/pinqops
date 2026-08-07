using PinqOps.Registries;
using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>
/// The private registries this server can pull from.
///
/// <para>Admin throughout, reads included — the same stance <c>/api/secrets</c>
/// takes and for the same reason: the list alone says which registries this server
/// holds credentials for and under which account, which is most of what somebody
/// needs to know where to aim.</para>
/// </summary>
public static class RegistryEndpoints
{
    public static void MapRegistryEndpoints(this IEndpointRouteBuilder app, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logger);

        app.MapGet("/api/registries", async Task<object?> (RegistryService registries) =>
        {
            await Task.CompletedTask;
            return new
            {
                items = registries.Store.Load().Select(registry => new
                {
                    registry.Id,
                    registry.Host,
                    registry.Username,
                    registry.SecretName,
                    registry.CreatedAt,
                    registry.LastLoginAt,
                }),
            };
        });

        app.MapPost("/api/registries", async (HttpContext context, RegistryService registries) =>
        {
            var request = await ReadAsync(context);
            if (request.Error is not null)
            {
                return Error(400, request.Error);
            }

            var registry = request.Registry!;
            registry.Id = RegistryStore.NewId();
            registry.Host = RegistryValidator.Normalize(registry.Host);
            registry.CreatedAt = DateTimeOffset.UtcNow;

            var duplicate = registries.Store.Update(stored =>
            {
                // One entry per host: docker keeps one credential per registry, so a
                // second entry would silently be whichever was signed in last.
                if (stored.Exists(entry => string.Equals(entry.Host, registry.Host, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }

                stored.Add(registry);
                return false;
            });

            if (duplicate)
            {
                return Error(409, $"There is already a registry for {registry.Host}.");
            }

            logger.LogWarning("Registry {Host} added as {User}", registry.Host, registry.Username);
            return Results.Json(new { id = registry.Id });
        });

        app.MapPost("/api/registries/{id}/login", async (string id, RegistryService registries) =>
        {
            try
            {
                var failure = await registries.LoginAsync(id);
                return failure is null
                    ? Results.Json(new { ok = true })
                    : Error(400, failure);
            }
            catch (KeyNotFoundException)
            {
                return Error(404, "Unknown registry.");
            }
        });

        app.MapDelete("/api/registries/{id}", async (string id, RegistryService registries) =>
        {
            var removed = registries.Store.Update(stored =>
            {
                var entry = stored.Find(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
                if (entry is not null)
                {
                    stored.Remove(entry);
                }

                return entry;
            });

            if (removed is null)
            {
                return Error(404, "Unknown registry.");
            }

            // Signed out as well, so "removed" does not mean only "removed from this
            // list" while the daemon keeps pulling with the same credential.
            await registries.LogoutAsync(removed.Host);
            logger.LogWarning("Registry {Host} removed", removed.Host);
            return Results.Json(new { ok = true });
        });
    }

    private static async Task<(Registry? Registry, string? Error)> ReadAsync(HttpContext context)
    {
        Registry? registry;
        try
        {
            registry = await context.Request.ReadFromJsonAsync<Registry>();
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, "Invalid request body.");
        }

        if (registry is null)
        {
            return (null, "Invalid request body.");
        }

        return RegistryValidator.Validate(registry) is { } problem ? (null, problem) : (registry, null);
    }
}
