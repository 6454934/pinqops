using static PinqOps.Web.EndpointHelpers;
using static System.Globalization.CultureInfo;

namespace PinqOps.Web;

/// <summary>
/// The <c>/api/2fa</c> routes: setting a second factor up on your own account,
/// turning it off, and replacing the recovery codes.
///
/// <para><b>Your own account, always.</b> Every route here acts on whoever is
/// signed in. An admin cannot turn two-factor <em>on</em> for somebody else — the
/// secret has to reach their phone, and there is no path from this server to it —
/// and the one thing an admin can do to another account is remove it, which is the
/// break-glass for a lost phone and is written to the audit trail.</para>
/// </summary>
public static class TwoFactorEndpoints
{
    public static void MapTwoFactorEndpoints(this IEndpointRouteBuilder app, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logger);

        app.MapGet("/api/2fa", async Task<object?> (HttpContext context, UiConfigStore store, TwoFactorService twoFactor) =>
        {
            await Task.CompletedTask;
            var self = Self(context);
            var account = twoFactor.Find(self);
            return new
            {
                username = self,
                enabled = account?.TwoFactorEnabled ?? false,
                // "Started but not finished" is a state worth showing: it is what an
                // abandoned setup looks like, and the answer is to start again.
                pending = account is { TwoFactorEnabled: false, TwoFactorSecret.Length: > 0 },
                recoveryCodesLeft = account?.RecoveryCodeHashes.Count ?? 0,
                required = store.Current.RequireTwoFactor,
                // Who else has one is an admin's business — it is the list of which
                // accounts are the soft targets, which is exactly what somebody
                // choosing where to aim would want.
                users = string.Equals(context.Items["scope"] as string, "admin", StringComparison.Ordinal)
                    ? store.Current.Users
                        .Select(user => new { user.Username, twoFactor = user.TwoFactorEnabled })
                        .ToList()
                    : [],
            };
        });

        app.MapPost("/api/2fa/setup", async Task<object?> (HttpContext context, TwoFactorService twoFactor) =>
        {
            await Task.CompletedTask;
            var (secret, uri, svg) = twoFactor.Begin(Self(context));

            // The secret in plain text as well as the QR: a camera that will not
            // focus, or a desktop authenticator with no camera at all, is the
            // ordinary case rather than the exotic one.
            return new { secret, uri, svg };
        });

        app.MapPost("/api/2fa/enable", async Task<object?> (HttpContext context, TwoFactorService twoFactor) =>
        {
            var request = await context.Request.ReadFromJsonAsync<TwoFactorCodeRequest>();
            var codes = twoFactor.Confirm(Self(context), request?.Code);

            // The only time these are ever readable. They are hashed on the way in,
            // so a second look is a new set.
            return new { ok = true, recoveryCodes = codes };
        });

        app.MapPost("/api/2fa/disable", async Task<IResult> (
            HttpContext context, UiConfigStore store, TwoFactorService twoFactor, LoginThrottle throttle) =>
        {
            var request = await context.Request.ReadFromJsonAsync<TwoFactorDisableRequest>();
            var self = Self(context);
            var target = string.IsNullOrWhiteSpace(request?.Username) ? self : request!.Username!.Trim();
            var isSelf = string.Equals(target, self, StringComparison.OrdinalIgnoreCase);

            if (!isSelf && !string.Equals(context.Items["scope"] as string, "admin", StringComparison.Ordinal))
            {
                return Error(403, "Only an admin can remove two-factor from another account.");
            }

            if (twoFactor.Find(target) is null)
            {
                return Error(404, $"No user named '{target}'.");
            }

            // Your own second factor comes off only against a current code or a
            // recovery code. Without that, a borrowed session — an unlocked laptop —
            // is enough to strip the protection off the account it is signed in to.
            //
            // Throttled on the same buckets as the sign-in step, and for the same
            // reason: six digits is a million combinations and three of them are
            // current at any moment, so a guess that costs nothing is a way through.
            // Only this branch — an admin removing somebody else's factor verifies no
            // code, and locking that would take away the break-glass for a lost phone.
            var verifies = isSelf && twoFactor.IsEnabledFor(target);
            if (verifies && LockedOut(context, throttle, target) is { } locked)
            {
                return locked;
            }

            if (verifies && twoFactor.Verify(target, request?.Code) is TwoFactorResult.Wrong or TwoFactorResult.NotEnrolled)
            {
                throttle.RecordFailure(ClientKey(context), target);
                await Task.Delay(WrongCodeDelay);

                // 403 and not 401: the caller is signed in — that is how they reached
                // this route — so the refusal is of the second factor, not of the
                // session. The dashboard reads any 401 outside the sign-in routes as
                // an expired session and drops to the lock screen, which threw away
                // both the message and the page someone was on for a mistyped code.
                // It also matches the sibling route, which fails through the exception
                // filter rather than naming a status itself.
                return Error(403, "That code is not right.");
            }

            if (verifies)
            {
                throttle.RecordSuccess(ClientKey(context), target);
            }

            twoFactor.Disable(target);
            if (!isSelf)
            {
                logger.LogWarning("Two-factor removed from '{User}' by '{Actor}'", target, self);
            }

            return Results.Json(new { ok = true });
        });

        app.MapPost("/api/2fa/recovery-codes", async Task<object?> (
            HttpContext context, TwoFactorService twoFactor, LoginThrottle throttle) =>
        {
            var request = await context.Request.ReadFromJsonAsync<TwoFactorCodeRequest>();
            var self = Self(context);

            // Throttled like the route above, and this one needs it twice over: a
            // wrong guess here also costs the server ten PBKDF2 hashes, so an
            // unthrottled one is a way to spend its CPU as well as to guess.
            if (LockedOut(context, throttle, self) is { } locked)
            {
                return locked;
            }

            // Same reasoning as turning it off: a fresh set invalidates the old one,
            // so it is a change to the account's credentials.
            if (twoFactor.Verify(self, request?.Code) is TwoFactorResult.Wrong or TwoFactorResult.NotEnrolled)
            {
                throttle.RecordFailure(ClientKey(context), self);
                await Task.Delay(WrongCodeDelay);
                throw new UnauthorizedAccessException("That code is not right.");
            }

            throttle.RecordSuccess(ClientKey(context), self);
            return new { ok = true, recoveryCodes = twoFactor.RegenerateRecoveryCodes(self) };
        });

        app.MapPost("/api/2fa/require", async Task<object?> (HttpContext context, UiConfigStore store) =>
        {
            var request = await context.Request.ReadFromJsonAsync<TwoFactorRequireRequest>();
            var required = request?.Required ?? false;
            store.Update(config => config.RequireTwoFactor = required);
            logger.LogWarning("Two-factor is now {State} for every account", required ? "required" : "optional");
            return new { ok = true, required };
        });
    }

    /// <summary>
    /// How long a rejected code is held before the answer goes back, matching the
    /// sign-in step. It is not the defence — the throttle is — but it takes the
    /// cheapest attempts off the table between lockouts.
    /// </summary>
    private static readonly TimeSpan WrongCodeDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The 429 to answer with when this account has run out of attempts, or null
    /// when it has not.
    /// </summary>
    private static IResult? LockedOut(HttpContext context, LoginThrottle throttle, string account)
    {
        if (throttle.RetryAfter(ClientKey(context), account) is not { } wait)
        {
            return null;
        }

        context.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(InvariantCulture);
        return Error(429, $"Too many failed attempts — try again in {(int)Math.Ceiling(wait.TotalMinutes)} minute(s).");
    }

    /// <summary>
    /// Whoever is signed in. An API token authenticates as a synthetic principal
    /// with no account behind it, so it has no second factor to manage — the
    /// handlers below find no user and say so.
    /// </summary>
    private static string Self(HttpContext context) =>
        context.Items["user"] as string ?? UserRoles.LegacyAdmin;
}
