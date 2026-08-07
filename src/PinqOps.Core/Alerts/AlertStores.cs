using System.Text.Json;
using System.Text.Json.Serialization;
using PinqOps.Notifications;

namespace PinqOps.Alerts;

/// <summary>
/// The alert rules for this server. Server-global rather than per-app: no app
/// owns "host CPU above 90%", and the evaluator runs in the dashboard process
/// rather than on the runner.
/// </summary>
public sealed class AlertConfig
{
    private List<AlertRule> _rules = [];

    /// <summary>Never null, even for a literal <c>"rules": null</c> on disk.</summary>
    public List<AlertRule> Rules
    {
        get => _rules;
        set => _rules = value is null ? [] : [.. value.Where(rule => rule is not null)];
    }
}

/// <summary>
/// Where alerts are delivered. The same three channel shapes the per-app deploy
/// notifications use, minus the deploy-event toggles — kept separate because
/// these settings belong to the server, not to whichever repository happens to
/// be connected. On a fresh install there is no app at all, which is exactly
/// when host alerting matters most.
/// </summary>
public sealed class AlertChannelConfig
{
    private WebhookChannel _webhook = new();
    private SlackChannel _slack = new();
    private TelegramChannel _telegram = new();
    private EmailChannel _email = new();

    public WebhookChannel Webhook { get => _webhook; set => _webhook = value ?? new(); }

    public SlackChannel Slack { get => _slack; set => _slack = value ?? new(); }

    public TelegramChannel Telegram { get => _telegram; set => _telegram = value ?? new(); }

    /// <summary>
    /// Only here, not on the per-app deploy config. Sending mail needs the server's
    /// relay credentials, and the deploy notifications are sent by the runner from
    /// beside a repository's compose file — which is the last place those belong.
    /// </summary>
    public EmailChannel Email { get => _email; set => _email = value ?? new(); }
}

/// <summary>Shared JSON conventions for the alert stores: camelCase, indented, forgiving on read.</summary>
internal static class AlertJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

/// <summary>Loads and saves <see cref="AlertConfig"/> (camelCase JSON, 0600).</summary>
public sealed class AlertRuleStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public AlertRuleStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public string Path_ => _path;

    public AlertConfig Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<AlertConfig>(SecureFile.ReadAllText(_path), AlertJson.Options)
                    ?? new AlertConfig();
            }
        }
        catch (JsonException)
        {
            // A corrupt rule file means "no alert rules", never a crashed
            // dashboard — the same contract every other config store here keeps.
        }

        return new AlertConfig();
    }

    public void Save(AlertConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        lock (_gate)
        {
            SecureFile.WriteAllText(_path, JsonSerializer.Serialize(config, AlertJson.Options));
        }
    }

    /// <summary>
    /// Loads, mutates and saves under one lock, returning whatever
    /// <paramref name="mutate"/> produced.
    ///
    /// Every caller that edits a rule has to go through this rather than
    /// load-then-save: two requests arriving together would otherwise both read
    /// the old file and the second would write the first one's change away. That
    /// is a rule silently vanishing right after someone added it — the kind of
    /// bug nobody reports because it looks like they mis-clicked.
    ///
    /// If <paramref name="mutate"/> throws (a validation failure), nothing is
    /// written.
    /// </summary>
    public T Update<T>(Func<AlertConfig, T> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_gate)
        {
            var config = Load();
            var result = mutate(config);
            SecureFile.WriteAllText(_path, JsonSerializer.Serialize(config, AlertJson.Options));
            return result;
        }
    }
}

/// <summary>Loads and saves <see cref="AlertChannelConfig"/> (0600 — it holds a bot token).</summary>
public sealed class AlertChannelStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public AlertChannelStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public string Path_ => _path;

    public AlertChannelConfig Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<AlertChannelConfig>(SecureFile.ReadAllText(_path), AlertJson.Options)
                    ?? new AlertChannelConfig();
            }
        }
        catch (JsonException)
        {
            // A corrupt channel file means "nowhere to send", never a crash.
        }

        return new AlertChannelConfig();
    }

    public void Save(AlertChannelConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        lock (_gate)
        {
            SecureFile.WriteAllText(_path, JsonSerializer.Serialize(config, AlertJson.Options));
        }
    }

    /// <summary>
    /// Loads, mutates and saves under one lock — see
    /// <see cref="AlertRuleStore.Update{T}"/>. It matters more here: the partial
    /// update keeps a stored bot token when the request leaves it blank, so a
    /// racing save could write back a token it never saw.
    /// </summary>
    public void Update(Action<AlertChannelConfig> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_gate)
        {
            var config = Load();
            mutate(config);
            SecureFile.WriteAllText(_path, JsonSerializer.Serialize(config, AlertJson.Options));
        }
    }
}

/// <summary>
/// Persists the evaluator's state between ticks and across restarts. Without it
/// a restart would re-announce every alert that is already firing, and would
/// forget every "for" window in progress.
/// </summary>
public sealed class AlertStateStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public AlertStateStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public string Path_ => _path;

    /// <summary>
    /// Load, mutate and save under one lock.
    ///
    /// Two writers touch this file: the evaluation tick, and the rule-delete
    /// endpoint sweeping out a deleted rule's series. Without a lock spanning the
    /// whole sequence, a delete that read the map before the tick saved would write
    /// the tick's transitions away — restoring a fired alert's pre-fire state, so it
    /// fires and pages again on the next tick. This is the same guard
    /// <see cref="AlertRuleStore.Update"/> exists for.
    /// </summary>
    public void Update(Func<Dictionary<string, AlertSeriesState>, Dictionary<string, AlertSeriesState>> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_gate)
        {
            var updated = mutate(LoadUnlocked());
            ArgumentNullException.ThrowIfNull(updated);
            SecureFile.WriteAllText(_path, JsonSerializer.Serialize(updated, AlertJson.Options));
        }
    }

    public Dictionary<string, AlertSeriesState> Load()
    {
        lock (_gate)
        {
            return LoadUnlocked();
        }
    }

    private Dictionary<string, AlertSeriesState> LoadUnlocked()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, AlertSeriesState>>(
                    SecureFile.ReadAllText(_path), AlertJson.Options);
                if (loaded is not null)
                {
                    // A null value here would reach the evaluator and throw; drop
                    // those entries rather than the whole map.
                    var states = new Dictionary<string, AlertSeriesState>(StringComparer.Ordinal);
                    foreach (var (key, state) in loaded)
                    {
                        if (key is not null && state is not null)
                        {
                            states[key] = state;
                        }
                    }

                    return states;
                }
            }
        }
        catch (JsonException)
        {
            // Losing the state map costs one re-announcement, which is far better
            // than a worker that will not start.
        }

        return new Dictionary<string, AlertSeriesState>(StringComparer.Ordinal);
    }

    public void Save(IReadOnlyDictionary<string, AlertSeriesState> states)
    {
        ArgumentNullException.ThrowIfNull(states);

        lock (_gate)
        {
            SecureFile.WriteAllText(_path, JsonSerializer.Serialize(states, AlertJson.Options));
        }
    }
}
