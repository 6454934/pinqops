using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace PinqOps.Web;

/// <summary>
/// "Sign in with GitHub" via the OAuth device flow: the browser shows a short
/// code, the user confirms it on github.com, and polling yields a token — no
/// client secret and no inbound callback needed. Requires an OAuth App client
/// id (Settings or PINQOPS_GITHUB_CLIENT_ID) with device flow enabled. The
/// sensitive device_code never leaves the server; the browser only holds an
/// opaque handle.
/// </summary>
public sealed class GitHubDeviceFlow : IDisposable
{
    private sealed record Pending(string ClientId, string DeviceCode, int IntervalSeconds, DateTimeOffset ExpiresAt);

    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, Pending> _pending = new();

    public GitHubDeviceFlow(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
    }

    public async Task<object> StartAsync(string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        // The scope must cover everything the dashboard later does with this
        // token: repo reads, committing .github/workflows/deploy.yml
        // (`workflow`), and pulling private GHCR images (`read:packages`).
        var payload = await PostAsync(
                "https://github.com/login/device/code",
                new Dictionary<string, string> { ["client_id"] = clientId, ["scope"] = "repo workflow read:packages" })
            .ConfigureAwait(false);

        if (GetString(payload, "device_code") is not { } deviceCode
            || GetString(payload, "user_code") is not { } userCode)
        {
            throw new InvalidOperationException(
                GetString(payload, "error_description")
                ?? "GitHub did not return a device code — check the OAuth App client id and that device flow is enabled.");
        }

        var interval = payload.TryGetProperty("interval", out var i) && i.TryGetInt32(out var seconds) ? seconds : 5;
        var expiresIn = payload.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var exp) ? exp : 900;

        // Sweep abandoned handles (started but never polled to completion) so a
        // client that only ever calls start cannot grow the table without bound.
        PruneExpired();

        var handle = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        _pending[handle] = new Pending(clientId, deviceCode, interval, DateTimeOffset.UtcNow.AddSeconds(expiresIn));

        return new
        {
            handle,
            userCode,
            verificationUri = GetString(payload, "verification_uri") ?? "https://github.com/login/device",
            intervalSeconds = interval,
            expiresInSeconds = expiresIn,
        };
    }

    /// <summary>
    /// One poll step. Status: pending | success | denied | expired. The
    /// returned interval is the number of seconds the caller must wait before
    /// the next poll — it grows when GitHub answers <c>slow_down</c>.
    /// </summary>
    public async Task<(string Status, string? Token, int IntervalSeconds)> PollAsync(string handle)
    {
        if (!_pending.TryGetValue(handle, out var pending))
        {
            return ("expired", null, 5);
        }

        if (pending.ExpiresAt < DateTimeOffset.UtcNow)
        {
            _pending.TryRemove(handle, out _);
            return ("expired", null, pending.IntervalSeconds);
        }

        var payload = await PostAsync(
                "https://github.com/login/oauth/access_token",
                new Dictionary<string, string>
                {
                    ["client_id"] = pending.ClientId,
                    ["device_code"] = pending.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                })
            .ConfigureAwait(false);

        if (GetString(payload, "access_token") is { Length: > 0 } token)
        {
            _pending.TryRemove(handle, out _);
            return ("success", token, pending.IntervalSeconds);
        }

        switch (GetString(payload, "error"))
        {
            case "authorization_pending":
                return ("pending", null, pending.IntervalSeconds);
            case "slow_down":
                // Spec: add 5 seconds to the interval and keep going.
                var slowed = pending with { IntervalSeconds = pending.IntervalSeconds + 5 };
                _pending[handle] = slowed;
                return ("pending", null, slowed.IntervalSeconds);
            case "access_denied":
                _pending.TryRemove(handle, out _);
                return ("denied", null, pending.IntervalSeconds);
            default:
                _pending.TryRemove(handle, out _);
                return ("expired", null, pending.IntervalSeconds);
        }
    }

    private const int MaxPending = 256;

    private void PruneExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, pending) in _pending)
        {
            if (pending.ExpiresAt < now)
            {
                _pending.TryRemove(key, out _);
            }
        }

        // Hard cap as a backstop: evict the entries nearest to expiry.
        while (_pending.Count >= MaxPending)
        {
            var oldest = _pending.MinBy(pair => pair.Value.ExpiresAt);
            if (!_pending.TryRemove(oldest.Key, out _))
            {
                break;
            }
        }
    }

    private async Task<JsonElement> PostAsync(string url, Dictionary<string, string> form)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("pinqops-ui");

        using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub device-flow request failed ({(int)response.StatusCode}).");
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public void Dispose() => _httpClient.Dispose();
}
