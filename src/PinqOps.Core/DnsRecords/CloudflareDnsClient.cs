using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace PinqOps.DnsRecords;

/// <summary>
/// Cloudflare's DNS API, enough of it to point a name at this server and take it
/// away again.
///
/// <para><b>Finding the zone.</b> Cloudflare identifies a zone by its registrable
/// name, and the domain being routed is usually a subdomain of one — so the name is
/// walked from the left (<c>app.example.com</c>, then <c>example.com</c>, then
/// <c>com</c>) until a zone matches. Asking for the whole name first means an
/// account that really does hold <c>app.example.com</c> as its own zone is found
/// before its parent, which is the answer the operator meant.</para>
///
/// <para><b>Errors are quoted, not summarised.</b> Cloudflare's messages name the
/// actual problem — a token without <c>Zone:Edit</c>, a zone on another account —
/// and paraphrasing them into "DNS update failed" is how someone ends up guessing.</para>
/// </summary>
public sealed class CloudflareDnsClient : DnsZoneClient
{
    private const string BaseAddress = "https://api.cloudflare.com/client/v4/";

    /// <summary>
    /// Cloudflare's "let us decide" TTL. Anything shorter is a number to maintain
    /// for no benefit; the record does not change once it is right.
    /// </summary>
    private const int AutomaticTtl = 1;

    private readonly HttpClient _client;
    private readonly string _apiToken;
    private readonly Action<string>? _trace;
    private readonly string? _zoneId;
    private readonly string? _accountId;

    /// <summary>
    /// The <see cref="HttpClient"/> is shared and never mutated here — the token
    /// goes on each request instead of on the client's default headers, so one
    /// client can serve callers holding different tokens without them leaking into
    /// each other.
    /// </summary>
    /// <param name="trace">
    /// Optional operator-facing step lines (no secrets). Used so journalctl can
    /// show zone lookup / write progress when the API is slow.
    /// </param>
    /// <param name="zoneId">
    /// When set, <see cref="ZoneFor"/> returns this id without calling
    /// <c>GET /zones</c> — skips a flaky lookup; does not replace DNS Edit on the token.
    /// </param>
    /// <param name="accountId">
    /// When set and <paramref name="zoneId"/> is empty, zone name queries add
    /// <c>account.id</c> so a multi-account token finds the right zone.
    /// </param>
    public CloudflareDnsClient(
        HttpClient client,
        string apiToken,
        Action<string>? trace = null,
        string? zoneId = null,
        string? accountId = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiToken);

        _client = client;
        _apiToken = apiToken;
        _trace = trace;
        _zoneId = string.IsNullOrWhiteSpace(zoneId) ? null : zoneId.Trim();
        _accountId = string.IsNullOrWhiteSpace(accountId) ? null : accountId.Trim();
    }

    public string Provider => "cloudflare";

    public async Task<IReadOnlyList<DnsRecord>> Find(string name, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(name);
        var zoneId = await ZoneFor(normalized, cancellationToken).ConfigureAwait(false);
        return await RecordsIn(zoneId, normalized, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DnsRecord> Point(
        string name, string address, bool proxied = true, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        var normalized = Normalize(name);
        _trace?.Invoke($"Cloudflare: looking up zone for '{normalized}'");
        var zoneId = await ZoneFor(normalized, cancellationToken).ConfigureAwait(false);
        _trace?.Invoke($"Cloudflare: zone id {zoneId} — listing existing A records for '{normalized}'");
        var existing = await RecordsIn(zoneId, normalized, cancellationToken).ConfigureAwait(false);

        var body = new
        {
            type = "A",
            name = normalized,
            content = address,
            // Proxied records refuse a custom TTL; Cloudflare decides. DNS-only can
            // keep the automatic TTL the same way — one code path for both.
            ttl = proxied ? AutomaticTtl : AutomaticTtl,
            // Orange cloud by default: that is what operators mean by "point this
            // through Cloudflare". DNS-only is the escape hatch when the origin must
            // be reached without the CDN. Proxied terminates visitor TLS at Cloudflare;
            // the origin still needs a certificate (Let's Encrypt or a custom one) for
            // Full / Full Strict, and edge mode has to be on so this server sees the
            // visitor rather than the CDN.
            proxied,
        };

        // Replaced rather than added: two A records for one name is a round-robin
        // nobody asked for, and half the requests would land nowhere.
        // Find stores ids as zone/record (see ReadRecord); Cloudflare's PUT path
        // wants only the record id — putting the composite in the URL produces
        // HTTP 405 method_not_allowed (code 1001).
        string path;
        HttpMethod method;
        if (existing.Count > 0)
        {
            var composite = existing[0].Id;
            var separator = composite.IndexOf('/', StringComparison.Ordinal);
            var recordId = separator > 0 ? composite[(separator + 1)..] : composite;
            path = $"zones/{zoneId}/dns_records/{recordId}";
            method = HttpMethod.Put;
        }
        else
        {
            path = $"zones/{zoneId}/dns_records";
            method = HttpMethod.Post;
        }

        _trace?.Invoke(
            existing.Count > 0
                ? $"Cloudflare: updating A record for '{normalized}' → {address} (proxied={proxied})"
                : $"Cloudflare: creating A record for '{normalized}' → {address} (proxied={proxied})");
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        var result = await Send(request, cancellationToken).ConfigureAwait(false);
        _trace?.Invoke($"Cloudflare: A record for '{normalized}' written");

        return ReadRecord(result)
            ?? throw new DnsProviderException("Cloudflare accepted the record but did not describe it.");
    }

    public async Task Remove(string recordId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);

        // The zone is part of the delete path, and the id alone does not carry it —
        // so the caller's record has to have come from Find, which knows both.
        var separator = recordId.IndexOf('/', StringComparison.Ordinal);
        if (separator <= 0)
        {
            throw new DnsProviderException("That is not a record this client can remove.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Delete, $"zones/{recordId[..separator]}/dns_records/{recordId[(separator + 1)..]}");
        await Send(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ZoneFor(string name, CancellationToken cancellationToken)
    {
        if (_zoneId is not null)
        {
            _trace?.Invoke($"Cloudflare: using configured zone id {_zoneId}");
            return _zoneId;
        }

        var labels = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var start = 0; start < labels.Length - 1; start++)
        {
            var candidate = string.Join('.', labels[start..]);
            var query = $"zones?name={Uri.EscapeDataString(candidate)}";
            if (_accountId is not null)
            {
                query += $"&account.id={Uri.EscapeDataString(_accountId)}";
            }

            _trace?.Invoke(
                _accountId is null
                    ? $"Cloudflare: trying zone name '{candidate}'"
                    : $"Cloudflare: trying zone name '{candidate}' (account {_accountId})");
            using var request = new HttpRequestMessage(HttpMethod.Get, query);
            var result = await Send(request, cancellationToken).ConfigureAwait(false);

            if (result.TryGetProperty("result", out var zones)
                && zones.ValueKind == JsonValueKind.Array
                && zones.GetArrayLength() > 0
                && zones[0].TryGetProperty("id", out var id))
            {
                return id.GetString() ?? string.Empty;
            }
        }

        throw new DnsProviderException(
            $"No Cloudflare zone covers '{name}'. Check that the domain is on this Cloudflare account"
            + (_accountId is null ? "." : $" (account {_accountId})."));
    }

    private async Task<IReadOnlyList<DnsRecord>> RecordsIn(
        string zoneId, string name, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"zones/{zoneId}/dns_records?type=A&name={Uri.EscapeDataString(name)}");
        var result = await Send(request, cancellationToken).ConfigureAwait(false);

        if (!result.TryGetProperty("result", out var records) || records.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var found = new List<DnsRecord>();
        foreach (var record in records.EnumerateArray())
        {
            if (ReadRecord(record, zoneId) is { } parsed)
            {
                found.Add(parsed);
            }
        }

        return found;
    }

    /// <summary>
    /// The record id is stored as <c>zone/record</c> so a later delete carries the
    /// zone it belongs to — Cloudflare's delete path needs both, and re-resolving
    /// the zone at delete time could land on a different one if the account changed.
    /// </summary>
    private static DnsRecord? ReadRecord(JsonElement payload, string? zoneId = null)
    {
        var record = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("result", out var inner)
            ? inner
            : payload;

        if (record.ValueKind != JsonValueKind.Object
            || !record.TryGetProperty("id", out var id)
            || !record.TryGetProperty("name", out var name))
        {
            return null;
        }

        var zone = zoneId ?? (record.TryGetProperty("zone_id", out var zoneProperty) ? zoneProperty.GetString() : null);
        var address = record.TryGetProperty("content", out var content) ? content.GetString() : null;

        return new DnsRecord(
            $"{zone}/{id.GetString()}", name.GetString() ?? string.Empty, address ?? string.Empty);
    }

    private async Task<JsonElement> Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);

        // Relative paths are resolved here rather than through the client's
        // BaseAddress, so a caller cannot repoint the API by handing over a
        // differently-configured client.
        if (!request.RequestUri!.IsAbsoluteUri)
        {
            request.RequestUri = new Uri(new Uri(BaseAddress), request.RequestUri);
        }

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new DnsProviderException(
                $"Could not reach Cloudflare ({request.Method} {request.RequestUri?.AbsolutePath}): {exception.Message}",
                exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout surfaces as TaskCanceledException, not HttpRequestException.
            var seconds = _client.Timeout.TotalSeconds;
            var path = request.RequestUri?.AbsolutePath ?? "?";
            throw new DnsProviderException(
                $"Cloudflare API timed out on {request.Method} {path} after {seconds:0}s. "
                + "Outbound HTTPS to api.cloudflare.com may be slow or blocked from this server.",
                exception);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            JsonElement payload;
            try
            {
                payload = JsonDocument.Parse(body).RootElement.Clone();
            }
            catch (JsonException exception)
            {
                throw new DnsProviderException("Cloudflare returned something that is not JSON.", exception);
            }

            var succeeded = payload.TryGetProperty("success", out var success)
                && success.ValueKind == JsonValueKind.True;

            if (!response.IsSuccessStatusCode || !succeeded)
            {
                throw new DnsProviderException(
                    $"Cloudflare refused the request: {FirstError(payload, response.StatusCode, body)}");
            }

            return payload;
        }
    }

    /// <summary>
    /// Cloudflare's own wording — a token without <c>Zone:Edit</c>, a zone on
    /// another account. Paraphrasing it is how someone ends up guessing. When the
    /// JSON has no useful <c>errors[].message</c>, the HTTP status (and a short
    /// body snippet) still beat "no reason given".
    /// </summary>
    private static string FirstError(JsonElement payload, System.Net.HttpStatusCode status, string body)
    {
        if (payload.TryGetProperty("errors", out var errors)
            && errors.ValueKind == JsonValueKind.Array
            && errors.GetArrayLength() > 0)
        {
            var first = errors[0];
            var code = first.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Number
                ? codeEl.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null;
            var message = first.TryGetProperty("message", out var messageEl)
                ? messageEl.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(message))
            {
                var detail = code is null ? message : $"{message} (code {code})";
                return code == "10000" ? AppendAuthGuidance(detail) : detail;
            }

            if (code is not null)
            {
                var detail = $"error code {code} (HTTP {(int)status})";
                return code == "10000" ? AppendAuthGuidance(detail) : detail;
            }
        }

        if (payload.TryGetProperty("messages", out var messages)
            && messages.ValueKind == JsonValueKind.Array
            && messages.GetArrayLength() > 0)
        {
            var text = messages[0].ValueKind == JsonValueKind.String
                ? messages[0].GetString()
                : messages[0].TryGetProperty("message", out var nested)
                    ? nested.GetString()
                    : null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        var snippet = (body ?? string.Empty).Trim();
        if (snippet.Length > 160)
        {
            snippet = snippet[..160] + "…";
        }

        return string.IsNullOrWhiteSpace(snippet)
            ? $"HTTP {(int)status}"
            : $"HTTP {(int)status}: {snippet}";
    }

    /// <summary>
    /// Code 10000 is almost always a wrong credential type or missing DNS Edit —
    /// Zone ID alone does not fix it, and a Global API Key must not be sent as Bearer.
    /// </summary>
    private static string AppendAuthGuidance(string detail) =>
        detail + ". API Token must allow Zone DNS Edit (and Zone Read). "
        + "Do not paste a Global API Key as a Bearer token — create a Custom Token "
        + "(template: Edit zone DNS). Zone ID alone does not grant DNS write.";

    private static string Normalize(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.Trim().TrimEnd('.').ToLowerInvariant();
    }
}
