using System.Collections.Concurrent;

namespace PinqOps.Web;

/// <summary>
/// Brute-force protection for password endpoints: after <see cref="MaxFailures"/>
/// failed attempts within the window, further attempts are refused for
/// <see cref="Lockout"/>.
///
/// Two buckets are counted, and either one locking is enough to refuse. The
/// client bucket stops one host working through many accounts. The per-account
/// bucket (<c>client|user</c>) is what survives a legitimate login: a success
/// clears only the account that actually verified, so someone holding one valid
/// credential cannot reset the counter on their guesses at <c>admin</c> by
/// signing in as themselves every fifth attempt.
/// </summary>
public sealed class LoginThrottle
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);

    private sealed record Entry(int Failures, DateTimeOffset FirstFailureAt, DateTimeOffset? LockedUntil);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    /// <summary>
    /// The bucket for one account as seen from one client. Usernames are matched
    /// case-insensitively at login, so the key is lower-cased to match — otherwise
    /// varying the casing would mint a fresh counter per attempt.
    /// </summary>
    private static string AccountKey(string clientKey, string username) =>
        $"{clientKey}|{username.Trim().ToLowerInvariant()}";

    /// <summary>Time the caller must still wait, or null if attempts are allowed.</summary>
    /// <param name="clientKey">The client bucket (an address).</param>
    /// <param name="username">
    /// The account being attempted, when the endpoint knows it. Null means only
    /// the client bucket applies — first-run setup names no account.
    /// </param>
    public TimeSpan? RetryAfter(string clientKey, string? username = null)
    {
        var client = RetryAfterFor(clientKey);
        if (username is null || string.IsNullOrWhiteSpace(username))
        {
            return client;
        }

        var account = RetryAfterFor(AccountKey(clientKey, username));
        return (client, account) switch
        {
            (null, null) => null,
            ({ } left, null) => left,
            (null, { } right) => right,
            var (left, right) => left > right ? left : right,
        };
    }

    private TimeSpan? RetryAfterFor(string key)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (entry.LockedUntil is { } lockedUntil)
        {
            if (lockedUntil > now)
            {
                return lockedUntil - now;
            }

            _entries.TryRemove(key, out _);
            return null;
        }

        if (now - entry.FirstFailureAt > Window)
        {
            _entries.TryRemove(key, out _);
        }

        return null;
    }

    public void RecordFailure(string clientKey, string? username = null)
    {
        var now = DateTimeOffset.UtcNow;
        RecordFailureFor(clientKey, now);
        if (username is not null && !string.IsNullOrWhiteSpace(username))
        {
            RecordFailureFor(AccountKey(clientKey, username), now);
        }

        // Opportunistic cleanup so the table cannot grow without bound.
        if (_entries.Count > 10_000)
        {
            foreach (var (key, entry) in _entries)
            {
                if (now - entry.FirstFailureAt > Window && (entry.LockedUntil is null || entry.LockedUntil < now))
                {
                    _entries.TryRemove(key, out _);
                }
            }
        }
    }

    private void RecordFailureFor(string key, DateTimeOffset now) =>
        _entries.AddOrUpdate(
            key,
            _ => new Entry(1, now, null),
            (_, entry) =>
            {
                if (now - entry.FirstFailureAt > Window)
                {
                    return new Entry(1, now, null);
                }

                var failures = entry.Failures + 1;
                return failures >= MaxFailures
                    ? new Entry(failures, entry.FirstFailureAt, now + Lockout)
                    : new Entry(failures, entry.FirstFailureAt, null);
            });

    /// <summary>
    /// Clears the counters a successful attempt should forgive. When the account is
    /// known, only that account's bucket is cleared — the client bucket keeps the
    /// failures accumulated against every <em>other</em> account, which is the
    /// whole point of counting it separately.
    /// </summary>
    public void RecordSuccess(string clientKey, string? username = null)
    {
        if (username is null || string.IsNullOrWhiteSpace(username))
        {
            _entries.TryRemove(clientKey, out _);
            return;
        }

        _entries.TryRemove(AccountKey(clientKey, username), out _);
    }
}
