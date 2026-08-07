using System.Collections.Concurrent;
using System.Security.Cryptography;
using PinqOps.TwoFactor;

namespace PinqOps.Web;

/// <summary>What the second step made of a code.</summary>
public enum TwoFactorResult
{
    /// <summary>The code was right, and has now been spent.</summary>
    Accepted,

    /// <summary>A recovery code was right; it has been struck off the list.</summary>
    AcceptedRecoveryCode,

    Wrong,

    /// <summary>The account has no second factor, so there was nothing to check.</summary>
    NotEnrolled,
}

/// <summary>
/// The half-finished sign-ins waiting for a code.
///
/// <para>In memory, like the sessions themselves: a challenge is worth less than a
/// session and lives for minutes, so persisting it would be storing a credential
/// to solve a problem a restart already solves. A restart during the second step
/// costs one re-entered password.</para>
///
/// <para><b>Holding one is not being signed in.</b> It resolves to a username and
/// nothing else — no scope, no role, no token the API would accept. What it saves
/// is having to send the password again, which is the only thing it is for.</para>
/// </summary>
public sealed class TwoFactorChallengeStore
{
    /// <summary>
    /// Long enough to find a phone, short enough that a challenge left on a screen
    /// is not a standing invitation.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private const int MaximumOutstanding = 1000;

    private readonly ConcurrentDictionary<string, (string Username, DateTimeOffset Expiry)> _challenges = new();

    public string Create(string username)
    {
        Prune();
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        _challenges[token] = (username, DateTimeOffset.UtcNow + Lifetime);
        return token;
    }

    /// <summary>
    /// The account a challenge belongs to, without spending it. A wrong code must
    /// not cost the challenge — otherwise a mistyped digit means entering the
    /// password again, and the throttle is what limits the guessing.
    /// </summary>
    public string? Resolve(string? token)
    {
        if (token is null || !_challenges.TryGetValue(token, out var challenge))
        {
            return null;
        }

        if (challenge.Expiry < DateTimeOffset.UtcNow)
        {
            _challenges.TryRemove(token, out _);
            return null;
        }

        return challenge.Username;
    }

    /// <summary>Spends a challenge, so the token that just signed somebody in cannot do it twice.</summary>
    public void Consume(string token) => _challenges.TryRemove(token, out _);

    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (token, challenge) in _challenges)
        {
            if (challenge.Expiry < now)
            {
                _challenges.TryRemove(token, out _);
            }
        }

        // A backstop against somebody holding the door open with correct passwords
        // they are not going to finish using.
        if (_challenges.Count > MaximumOutstanding)
        {
            foreach (var (token, _) in _challenges.OrderBy(entry => entry.Value.Expiry).Take(_challenges.Count - MaximumOutstanding))
            {
                _challenges.TryRemove(token, out _);
            }
        }
    }
}

/// <summary>
/// Enrolling an account in two-factor, and checking the code at the second step.
///
/// <para><b>Enrolment is two steps on purpose.</b> Starting it writes a secret and
/// nothing else; the account is not protected, and nothing about signing in
/// changes. It only takes effect once a code that secret produced has been typed
/// back — which proves the authenticator app really has it. Doing it in one step
/// would mean a QR that failed to scan, or scanned into the wrong app, locks
/// somebody out of their own server with no way back.</para>
/// </summary>
public sealed class TwoFactorService
{
    /// <summary>The name shown beside the code in the authenticator app.</summary>
    public const string Issuer = "pinqops";

    private readonly UiConfigStore _store;
    private readonly ILogger<TwoFactorService> _logger;

    public TwoFactorService(UiConfigStore store, ILogger<TwoFactorService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _logger = logger;
    }

    public UserAccount? Find(string? username) =>
        username is null
            ? null
            : _store.Current.Users.FirstOrDefault(user =>
                string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether an account has to pass a second step to sign in.</summary>
    public bool IsEnabledFor(string? username) => Find(username) is { TwoFactorEnabled: true };

    /// <summary>
    /// Starts enrolment: a fresh secret, the URI an app reads, and the QR drawn as
    /// SVG. Nothing about signing in changes until <see cref="Confirm"/>.
    /// </summary>
    public (string Secret, string Uri, string Svg) Begin(string username)
    {
        var account = Find(username) ?? throw new KeyNotFoundException($"No user named '{username}'.");
        if (account.TwoFactorEnabled)
        {
            throw new InvalidOperationException("Two-factor is already on for this account — turn it off first.");
        }

        var secret = Base32.Encode(Totp.NewSecret());
        _store.Update(config =>
        {
            var stored = config.Users.FirstOrDefault(user =>
                string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase));
            if (stored is not null)
            {
                // A new attempt replaces whatever the last one wrote, so an
                // abandoned enrolment leaves nothing behind that would still work.
                stored.TwoFactorSecret = secret;
                stored.LastTotpCounter = -1;
            }
        });

        Base32.TryDecode(secret, out var bytes);
        var uri = Totp.Uri(Issuer, account.Username, bytes);
        return (secret, uri, QrSvg.Render(QrCode.Encode(uri)));
    }

    /// <summary>
    /// Finishes enrolment once a code from the new secret checks out, and returns
    /// the recovery codes — the only time they are ever readable.
    /// </summary>
    public IReadOnlyList<string> Confirm(string username, string? code)
    {
        var account = Find(username) ?? throw new KeyNotFoundException($"No user named '{username}'.");
        if (account.TwoFactorEnabled)
        {
            throw new InvalidOperationException("Two-factor is already on for this account — turn it off first.");
        }

        if (!Base32.TryDecode(account.TwoFactorSecret, out var secret) || secret.Length == 0)
        {
            throw new InvalidOperationException("Start setting up two-factor first — there is no secret yet.");
        }

        var matched = Totp.Verify(secret, code, DateTimeOffset.UtcNow, account.LastTotpCounter)
            ?? throw new ArgumentException("That code is not right — check the clock on the phone and try the next one.");

        var codes = RecoveryCode.Generate();
        var hashes = codes.Select(RecoveryCode.Hash).ToList();

        _store.Update(config =>
        {
            var stored = config.Users.FirstOrDefault(user =>
                string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase));
            if (stored is not null)
            {
                stored.TwoFactorEnabled = true;
                stored.LastTotpCounter = matched;
                stored.RecoveryCodeHashes = hashes;
            }
        });

        _logger.LogWarning("Two-factor turned on for '{User}'", account.Username);
        return codes;
    }

    /// <summary>Turns it off and forgets the secret and the recovery codes.</summary>
    public void Disable(string username)
    {
        _store.Update(config =>
        {
            var stored = config.Users.FirstOrDefault(user =>
                string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase));
            if (stored is not null)
            {
                stored.TwoFactorEnabled = false;
                stored.TwoFactorSecret = null;
                stored.RecoveryCodeHashes = [];
                stored.LastTotpCounter = -1;
            }
        });

        _logger.LogWarning("Two-factor turned off for '{User}'", username);
    }

    /// <summary>A fresh set of recovery codes, replacing whatever was left of the old ones.</summary>
    public IReadOnlyList<string> RegenerateRecoveryCodes(string username)
    {
        var account = Find(username) ?? throw new KeyNotFoundException($"No user named '{username}'.");
        if (!account.TwoFactorEnabled)
        {
            throw new InvalidOperationException("Two-factor is not on for this account.");
        }

        var codes = RecoveryCode.Generate();
        var hashes = codes.Select(RecoveryCode.Hash).ToList();
        _store.Update(config =>
        {
            var stored = config.Users.FirstOrDefault(user =>
                string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase));
            if (stored is not null)
            {
                stored.RecoveryCodeHashes = hashes;
            }
        });

        _logger.LogWarning("Recovery codes replaced for '{User}'", account.Username);
        return codes;
    }

    /// <summary>
    /// Checks a code at the second step, and records what it cost: the step it used
    /// so it cannot be replayed, or the recovery code it spent so it cannot be used
    /// again.
    /// </summary>
    public TwoFactorResult Verify(string username, string? code)
    {
        var account = Find(username);
        if (account is null || !account.TwoFactorEnabled)
        {
            return TwoFactorResult.NotEnrolled;
        }

        if (Base32.TryDecode(account.TwoFactorSecret, out var secret)
            && Totp.Verify(secret, code, DateTimeOffset.UtcNow, account.LastTotpCounter) is { } matched)
        {
            _store.Update(config =>
            {
                var stored = config.Users.FirstOrDefault(user =>
                    string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase));
                if (stored is not null)
                {
                    stored.LastTotpCounter = matched;
                }
            });

            return TwoFactorResult.Accepted;
        }

        // Only after the code fails, so a recovery code is never spent by somebody
        // who typed their six digits correctly.
        var used = account.RecoveryCodeHashes.FirstOrDefault(hash => RecoveryCode.Verify(code ?? string.Empty, hash));
        if (used is null)
        {
            return TwoFactorResult.Wrong;
        }

        _store.Update(config =>
        {
            var stored = config.Users.FirstOrDefault(user =>
                string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase));
            stored?.RecoveryCodeHashes.Remove(used);
        });

        _logger.LogWarning(
            "'{User}' signed in with a recovery code; {Left} left", account.Username, account.RecoveryCodeHashes.Count - 1);
        return TwoFactorResult.AcceptedRecoveryCode;
    }
}
