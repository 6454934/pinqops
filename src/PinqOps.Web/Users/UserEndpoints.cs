using PinqOps;
using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>
/// User and role management (admin only).
/// </summary>
public static class UserEndpoints
{
    public static void MapUserEndpoints(this IEndpointRouteBuilder app, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logger);

        app.MapGet("/api/users", async Task<object?> (HttpContext context, UiConfigStore store) =>
        {
            await Task.CompletedTask;
            var me = context.Items["user"] as string;
            return new
            {
                items = store.Current.Users.Select(u => new
                {
                    u.Username,
                    u.Role,
                    isSelf = string.Equals(u.Username, me, StringComparison.OrdinalIgnoreCase),
                }),
            };
        });

        app.MapPost("/api/users", async Task<object?> (HttpContext context, UiConfigStore store) =>
        {
            var request = await context.Request.ReadFromJsonAsync<UserRequest>();
            var username = (request?.Username ?? string.Empty).Trim();
            if (UsernamePolicy.Validate(username) is { } nameRejection)
            {
                throw new ArgumentException(nameRejection);
            }

            var password = request?.Password ?? string.Empty;
            if (PasswordPolicy.Validate(password) is { } rejection)
            {
                throw new ArgumentException(rejection);
            }

            if (request?.Role is not { } role || !UserRoles.IsValid(role))
            {
                throw new ArgumentException("Role must be viewer, deployer, or admin.");
            }

            // Checked inside Update so two concurrent creates cannot both pass
            // against the same snapshot and save a duplicate username.
            store.Update(config =>
            {
                if (config.Users.Any(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException($"A user named '{username}' already exists.");
                }

                config.Users.Add(new UserAccount
                {
                    Username = username,
                    PasswordHash = PasswordHasher.Hash(password),
                    Role = role,
                });
            });
            logger.LogWarning("User '{User}' created with role {Role}", username, role);
            return new { ok = true, username, role };
        });

        app.MapPost("/api/users/{name}/password", async Task<object?> (string name, HttpContext context, UiConfigStore store, SessionStore sessions) =>
        {
            var request = await context.Request.ReadFromJsonAsync<UserPasswordRequest>();
            var password = request?.Password ?? string.Empty;
            if (PasswordPolicy.Validate(password) is { } rejection)
            {
                throw new ArgumentException(rejection);
            }

            var target = store.Current.Users.FirstOrDefault(u => string.Equals(u.Username, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"No user named '{name}'.");

            store.Update(config =>
            {
                var stored = config.Users.First(u => string.Equals(u.Username, target.Username, StringComparison.OrdinalIgnoreCase));
                stored.PasswordHash = PasswordHasher.Hash(password);
            });
            sessions.RevokeUser(target.Username); // force a fresh login with the new password
            logger.LogWarning("Password reset for user '{User}'", target.Username);
            return new { ok = true };
        });

        app.MapDelete("/api/users/{name}", async Task<object?> (
            string name,
            HttpContext context,
            UiConfigStore store,
            SessionStore sessions,
            ApiTokenStore tokens,
            TeamStore teams) =>
        {
            await Task.CompletedTask;
            var me = context.Items["user"] as string;
            if (string.Equals(name, me, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("You cannot delete your own account.");
            }

            // Looked up and guarded inside Update: two concurrent removals judged
            // against the same snapshot could each see two admins, pass the guard,
            // and together remove every admin.
            var removed = string.Empty;
            store.Update(config =>
            {
                var target = config.Users.FirstOrDefault(u => string.Equals(u.Username, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"No user named '{name}'.");

                // The last admin must never be removed, or no one could ever
                // administer the dashboard again.
                if (target.Role == UserRoles.Admin
                    && config.Users.Count(u => u.Role == UserRoles.Admin) <= 1)
                {
                    throw new InvalidOperationException("Cannot remove the last admin.");
                }

                removed = target.Username;
                config.Users.RemoveAll(u => string.Equals(u.Username, target.Username, StringComparison.OrdinalIgnoreCase));
            });
            // Both credentials, not just the browser one. A token is a second key the
            // same person holds, it can carry admin scope with no expiry, and it
            // authenticates as a synthetic principal with no account behind it — so
            // nothing about losing the account touched it, and somebody removed from
            // the server kept full API access indefinitely.
            RevokeEverything(sessions, tokens, removed, logger);

            // The name goes back into the pool the moment the account is gone, and a
            // team membership is keyed by the name. Left behind, it made whoever took
            // the name next — an invitee given no team at all — a member of every team
            // the removed user belonged to, and so a holder of every grant those teams
            // hold. Demotion deliberately does not do this: a demoted user keeps their
            // account and their teams, they simply have less scope.
            teams.Update<object?>(directory =>
            {
                foreach (var team in directory.Teams)
                {
                    team.Members.RemoveAll(member =>
                        string.Equals(member.Principal, removed, StringComparison.OrdinalIgnoreCase));
                }

                return null;
            });

            logger.LogWarning("User '{User}' removed", removed);
            return new { ok = true };
        });

        // Role change is a small, separate action so the UI can inline it in the table.
        app.MapPost("/api/users/{name}/role", async Task<object?> (
            string name, HttpContext context, UiConfigStore store, SessionStore sessions, ApiTokenStore tokens) =>
        {
            var request = await context.Request.ReadFromJsonAsync<UserRequest>();
            if (!UserRoles.IsValid(request?.Role))
            {
                throw new ArgumentException("Role must be viewer, deployer, or admin.");
            }

            // Looked up and guarded inside Update: two concurrent demotions judged
            // against the same snapshot could each see two admins, pass the guard,
            // and together demote every admin.
            var changed = string.Empty;
            store.Update(config =>
            {
                var target = config.Users.FirstOrDefault(u => string.Equals(u.Username, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"No user named '{name}'.");

                // Demoting the last admin would lock everyone out of administration.
                if (target.Role == UserRoles.Admin && request!.Role != UserRoles.Admin
                    && config.Users.Count(u => u.Role == UserRoles.Admin) <= 1)
                {
                    throw new InvalidOperationException("Cannot demote the last admin.");
                }

                changed = target.Username;
                target.Role = request!.Role!;
            });
            // The new role takes effect on the next login — and on the next token,
            // because a token minted at the old role would otherwise keep granting it.
            RevokeEverything(sessions, tokens, changed, logger);
            logger.LogWarning("User '{User}' role changed to {Role}", changed, request!.Role);
            return new { ok = true, username = changed, role = request.Role };
        });
    }

    /// <summary>
    /// Withdraws every credential one person holds: their sessions and the API
    /// tokens they minted. Kept in one place so the two callers cannot withdraw
    /// different sets — which is how one of them came to withdraw only the sessions.
    /// </summary>
    private static void RevokeEverything(
        SessionStore sessions, ApiTokenStore tokens, string username, ILogger logger)
    {
        sessions.RevokeUser(username);
        var revoked = tokens.DeleteCreatedBy(username);
        if (revoked > 0)
        {
            logger.LogWarning("{Count} API token(s) created by '{User}' revoked", revoked, username);
        }
    }
}
