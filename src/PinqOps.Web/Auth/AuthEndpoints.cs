using System.Security.Cryptography;
using System.Text;
using PinqOps;
using static PinqOps.Web.EndpointHelpers;
using static System.Globalization.CultureInfo;

namespace PinqOps.Web;

/// <summary>
/// The <c>/api/auth</c> handshake and <c>/api/me</c>. These routes are the ones
/// reachable without a session (state/setup/login) plus the few that run right
/// after it, so — like the inline versions before them — they sit outside
/// <c>Safe()</c> and answer a malformed body with their own 400. The setup code
/// and the logger travel in as parameters, exactly what the composition root
/// captured them as.
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(
        this IEndpointRouteBuilder app,
        ILogger logger,
        string setupCode,
        SetupCodeStore? setupCodes = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(setupCode);

        app.MapGet("/api/auth/state", (UiConfigStore store) => Results.Json(new
        {
            needsSetup = store.Current.Users.Count == 0,
            // The lock screen only shows a username field once there is more than one
            // user; a lone migrated admin logs in with just a password, as before.
            multiUser = store.Current.Users.Count > 1,
            // The lock screen renders the version before anyone is authenticated, so it
            // must come from this anonymous endpoint (settings is auth-gated).
            version = PinqOpsVersion.Current,
        }));

        app.MapPost("/api/auth/setup", async (HttpContext context, UiConfigStore store, SessionStore sessions, LoginThrottle throttle) =>
        {
            var client = ClientKey(context);
            if (throttle.RetryAfter(client) is { } wait)
            {
                context.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(InvariantCulture);
                return Error(429, $"Too many failed attempts — try again in {(int)Math.Ceiling(wait.TotalMinutes)} minute(s).");
            }

            // Safe() turns a malformed body into a 400 for every route it wraps, but the
            // auth handshake runs outside it — so a bad body on the three routes reachable
            // without a session was an unhandled 500 and a stack trace in the log, for
            // input as ordinary as an empty POST, from a caller that need not be signed in.
            SetupRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<SetupRequest>();
            }
            catch (System.Text.Json.JsonException)
            {
                return Error(400, "Invalid request body.");
            }

            if (store.Current.Users.Count > 0)
            {
                return Error(409, "A password is already set — log in instead.");
            }

            var offered = request?.SetupCode?.Trim().ToLowerInvariant() ?? "";
            if (offered.Length != setupCode.Length
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(offered),
                    Encoding.UTF8.GetBytes(setupCode)))
            {
                throttle.RecordFailure(client);
                logger.LogWarning("Dashboard setup attempt with a wrong setup code from {Client}", client);
                await Task.Delay(500);
                return Error(
                    401,
                    "Wrong setup code — use the latest `first-run setup code` line from the server "
                    + "(journalctl -u pinqops-ui -n 5 --no-pager). Older lines are stale.");
            }

            var password = request?.Password ?? string.Empty;
            if (PasswordPolicy.Validate(password) is { } rejection)
            {
                return Error(400, rejection);
            }

            throttle.RecordSuccess(client);
            store.Update(config => config.Users.Add(new UserAccount
            {
                Username = UserRoles.LegacyAdmin,
                PasswordHash = PasswordHasher.Hash(password),
                Role = UserRoles.Admin,
            }));
            setupCodes?.Clear();
            logger.LogWarning("Dashboard admin created from {Client}", client);
            return Results.Json(new { token = sessions.Create(UserRoles.LegacyAdmin, UserRoles.Admin) });
        });

        app.MapPost("/api/auth/login", async (
            HttpContext context,
            UiConfigStore store,
            SessionStore sessions,
            LoginThrottle throttle,
            TwoFactorChallengeStore challenges,
            TwoFactorService twoFactor) =>
        {
            var client = ClientKey(context);
            if (throttle.RetryAfter(client) is { } wait)
            {
                context.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(InvariantCulture);
                return Error(429, $"Too many failed attempts — try again in {(int)Math.Ceiling(wait.TotalMinutes)} minute(s).");
            }

            // See /api/auth/setup: this route is anonymous, so a malformed or empty body
            // must be a 400 rather than an unhandled 500 nobody is authenticated to cause.
            PasswordRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<PasswordRequest>();
            }
            catch (System.Text.Json.JsonException)
            {
                return Error(400, "Invalid request body.");
            }

            if (store.Current.Users.Count == 0)
            {
                return Error(409, "No password set yet — create one first.");
            }

            // A missing username means the sole account when exactly one exists — the
            // lock screen hides the username field then, and after adding accounts and
            // deleting the shared "admin" the lone user is not necessarily the legacy
            // one — and the legacy admin otherwise, so old clients keep working.
            var username = string.IsNullOrWhiteSpace(request?.Username)
                ? (store.Current.Users.Count == 1 ? store.Current.Users[0].Username : UserRoles.LegacyAdmin)
                : request!.Username!.Trim();

            // The per-account lockout needs the username, so it is checked here rather
            // than with the client-wide one above. Without it, an attacker holding one
            // valid credential could clear the client bucket every fifth attempt and
            // guess another account's password forever.
            if (throttle.RetryAfter(client, username) is { } accountWait)
            {
                context.Response.Headers.RetryAfter = ((int)Math.Ceiling(accountWait.TotalSeconds)).ToString(InvariantCulture);
                return Error(429, $"Too many failed attempts — try again in {(int)Math.Ceiling(accountWait.TotalMinutes)} minute(s).");
            }

            var account = store.Current.Users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));

            if (account is null || request?.Password is not { } password || !PasswordHasher.Verify(password, account.PasswordHash))
            {
                if (account is null)
                {
                    // Spend the same PBKDF2 time as a real (failed) verify so login
                    // timing can't be used to tell a valid username from an invalid one.
                    PasswordHasher.SpendVerificationTime();
                }

                throttle.RecordFailure(client, username);
                logger.LogWarning("Failed dashboard login for '{User}' from {Client}", username, client);
                // Name the account that was tried so the audit line shows which login is
                // being guessed, not just that something failed.
                context.Items["user"] = username;
                await Task.Delay(500); // keep failures slow even before the lockout kicks in
                return Error(401, "Wrong username or password.");
            }

            throttle.RecordSuccess(client, account.Username);
            context.Items["user"] = account.Username;
            if (PasswordHasher.NeedsRehash(account.PasswordHash))
            {
                store.Update(config =>
                {
                    var stored = config.Users.FirstOrDefault(u => string.Equals(u.Username, account.Username, StringComparison.OrdinalIgnoreCase));
                    if (stored is not null)
                    {
                        stored.PasswordHash = PasswordHasher.Hash(password);
                    }
                });
            }

            // The password was right. If the account has a second factor, that is
            // all it was — a challenge stands in for the session until a code
            // proves the phone is there too.
            if (twoFactor.IsEnabledFor(account.Username))
            {
                return Results.Json(new
                {
                    twoFactorRequired = true,
                    challenge = challenges.Create(account.Username),
                    username = account.Username,
                });
            }

            return Results.Json(new
            {
                token = sessions.Create(account.Username, account.Role),
                username = account.Username,
                role = account.Role,
                // Switching the requirement on does not lock anybody out: an account
                // without a second factor still signs in, and is told to finish
                // setting one up before it does anything else.
                enrolTwoFactor = store.Current.RequireTwoFactor,
            });
        });

        // The second step. It is its own route rather than a second field on the
        // login body so that the password is sent once: a client that retried the
        // whole login on a mistyped digit would put the password back on the wire
        // for every attempt.
        app.MapPost("/api/auth/login/2fa", async (
            HttpContext context,
            UiConfigStore store,
            SessionStore sessions,
            LoginThrottle throttle,
            TwoFactorChallengeStore challenges,
            TwoFactorService twoFactor) =>
        {
            var client = ClientKey(context);

            TwoFactorRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<TwoFactorRequest>();
            }
            catch (System.Text.Json.JsonException)
            {
                return Error(400, "Invalid request body.");
            }

            if (challenges.Resolve(request?.Challenge) is not { } username)
            {
                // 410 rather than 401, so the lock screen can tell "the password
                // has to be sent again" from "that was the wrong code" without
                // reading the message — which is translated, and would match in
                // one language only.
                return Error(410, "That sign-in has expired — start again.");
            }

            // The same throttle as the password step, on the same account bucket.
            // Six digits is a million combinations, which is not many when a
            // machine is doing the typing.
            if (throttle.RetryAfter(client, username) is { } wait)
            {
                context.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(InvariantCulture);
                return Error(429, $"Too many failed attempts — try again in {(int)Math.Ceiling(wait.TotalMinutes)} minute(s).");
            }

            context.Items["user"] = username;
            var result = twoFactor.Verify(username, request?.Code);
            if (result is TwoFactorResult.Wrong or TwoFactorResult.NotEnrolled)
            {
                throttle.RecordFailure(client, username);
                logger.LogWarning("Failed two-factor step for '{User}' from {Client}", username, client);
                await Task.Delay(500);
                return Error(401, "That code is not right.");
            }

            var account = twoFactor.Find(username)!;
            throttle.RecordSuccess(client, username);
            challenges.Consume(request!.Challenge!);
            return Results.Json(new
            {
                token = sessions.Create(account.Username, account.Role),
                username = account.Username,
                role = account.Role,
                usedRecoveryCode = result == TwoFactorResult.AcceptedRecoveryCode,
                recoveryCodesLeft = account.RecoveryCodeHashes.Count,
            });
        });

        // Who the current session/token is, so the UI can gate admin-only views. Runs
        // after the auth middleware, so the identity is already resolved.
        app.MapGet("/api/me", (HttpContext context) => Results.Json(new
        {
            user = context.Items["user"] as string ?? "",
            scope = context.Items["scope"] as string ?? "read",
        }));

        app.MapPost("/api/auth/logout", (HttpContext context, SessionStore sessions) =>
        {
            if (ReadBearerToken(context) is { } token)
            {
                sessions.Revoke(token);
            }

            return Results.Json(new { ok = true });
        });

        app.MapPost("/api/auth/change-password", async (HttpContext context, UiConfigStore store, SessionStore sessions, LoginThrottle throttle) =>
        {
            var client = ClientKey(context);

            // change-password runs after auth, so the caller's identity is known — a
            // user changes their OWN password (admins set others' via /api/users).
            var self = context.Items["user"] as string ?? UserRoles.LegacyAdmin;
            if (throttle.RetryAfter(client, self) is { } wait)
            {
                context.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(InvariantCulture);
                return Error(429, $"Too many failed attempts — try again in {(int)Math.Ceiling(wait.TotalMinutes)} minute(s).");
            }

            // An API token authenticates as a synthetic principal, not an account, so
            // there is no "own password" for it to change. Say so rather than failing the
            // account lookup below with a misleading "current password is wrong".
            if (ApiTokenStore.IsTokenPrincipal(self))
            {
                return Error(403, "An API token has no password to change — sign in as a user.");
            }

            // Same as the two routes above: this one runs outside Safe() too, so it needs
            // its own guard to answer 400 instead of 500 for a body it cannot read.
            ChangePasswordRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<ChangePasswordRequest>();
            }
            catch (System.Text.Json.JsonException)
            {
                return Error(400, "Invalid request body.");
            }

            var account = store.Current.Users.FirstOrDefault(u => string.Equals(u.Username, self, StringComparison.OrdinalIgnoreCase));
            if (account is null || request?.CurrentPassword is not { } current || !PasswordHasher.Verify(current, account.PasswordHash))
            {
                if (account is null)
                {
                    PasswordHasher.SpendVerificationTime();
                }

                throttle.RecordFailure(client, self);
                logger.LogWarning("Failed password change (wrong current password) for '{User}' from {Client}", self, client);
                await Task.Delay(500);
                return Error(401, "Current password is wrong.");
            }

            var fresh = request.NewPassword ?? string.Empty;
            if (PasswordPolicy.Validate(fresh) is { } rejectedNew)
            {
                return Error(400, rejectedNew);
            }

            throttle.RecordSuccess(client, self);
            store.Update(config =>
            {
                var stored = config.Users.FirstOrDefault(u => string.Equals(u.Username, self, StringComparison.OrdinalIgnoreCase));
                if (stored is not null)
                {
                    stored.PasswordHash = PasswordHasher.Hash(fresh);
                }
            });
            sessions.RevokeUser(account.Username); // sign this user's other devices out
            logger.LogWarning("Password changed for '{User}' from {Client}", self, client);
            return Results.Json(new { ok = true });
        });
    }
}
