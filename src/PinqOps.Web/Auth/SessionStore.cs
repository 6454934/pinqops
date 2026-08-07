using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace PinqOps.Web;

/// <summary>The signed-in identity a valid session token resolves to.</summary>
public sealed record SessionPrincipal(string Username, string Role);

/// <summary>In-memory bearer-token sessions for the dashboard (24h sliding).</summary>
public sealed class SessionStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    /// <summary>
    /// How long a session may live no matter how much it is used. Without this
    /// the sliding expiry renewed forever, so a stolen token stayed valid for as
    /// long as the thief kept using it.
    /// </summary>
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// Concurrent sessions per user. The cap used to be global and evicted the
    /// oldest session in the table, which let anyone holding one valid login sign
    /// everyone else out by logging in repeatedly. Per-user, that only ever
    /// evicts the user's own oldest session.
    /// </summary>
    private const int MaxSessionsPerUser = 16;

    private sealed record Session(DateTimeOffset Expiry, DateTimeOffset HardExpiry, string Username, string Role);

    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    /// <summary>Opens a session for a signed-in user and returns its bearer token.</summary>
    public string Create(string username, string role)
    {
        PruneExpired();
        EvictOldestFor(username);

        var now = DateTimeOffset.UtcNow;
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        _sessions[token] = new Session(now + Lifetime, now + MaximumLifetime, username, role);
        return token;
    }

    /// <summary>Keeps one user's session count under the cap, oldest out first.</summary>
    private void EvictOldestFor(string username)
    {
        while (true)
        {
            var owned = _sessions
                .Where(pair => string.Equals(pair.Value.Username, username, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (owned.Count < MaxSessionsPerUser)
            {
                return;
            }

            _sessions.TryRemove(owned.MinBy(pair => pair.Value.Expiry).Key, out _);
        }
    }

    /// <summary>The session's identity if the token is valid (and slides its expiry), else null.</summary>
    public SessionPrincipal? Resolve(string token)
    {
        if (!_sessions.TryGetValue(token, out var session))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (session.Expiry < now || session.HardExpiry < now)
        {
            _sessions.TryRemove(token, out _);
            return null;
        }

        // Slide the idle expiry, but never past the hard one.
        //
        // TryUpdate rather than the indexer: the indexer would re-add a token
        // that Revoke/RevokeUser removed in the window since TryGetValue, giving a
        // session an operator just killed another full lifetime. Losing the slide
        // is harmless — the next request slides it — but resurrecting a revoked
        // session is not.
        _sessions.TryUpdate(
            token,
            session with { Expiry = Min(now + Lifetime, session.HardExpiry) },
            session);
        return new SessionPrincipal(session.Username, session.Role);
    }

    public bool Validate(string token) => Resolve(token) is not null;

    public void Revoke(string token) => _sessions.TryRemove(token, out _);

    /// <summary>Signs every session out — used when the password changes.</summary>
    public void RevokeAll() => _sessions.Clear();

    /// <summary>Signs a specific user out everywhere — used when their role changes or they are removed.</summary>
    public void RevokeUser(string username)
    {
        foreach (var (token, session) in _sessions)
        {
            if (string.Equals(session.Username, username, StringComparison.OrdinalIgnoreCase))
            {
                _sessions.TryRemove(token, out _);
            }
        }
    }

    private void PruneExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (token, session) in _sessions)
        {
            if (session.Expiry < now || session.HardExpiry < now)
            {
                _sessions.TryRemove(token, out _);
            }
        }
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) =>
        left < right ? left : right;
}
