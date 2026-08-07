using System.Text.Json;

namespace PinqOps.Notifications;

/// <summary>
/// Notification settings shared by the CLI (which sends after deploys on the
/// runner) and the dashboard (which edits them). Stored as
/// <c>.pinqops/notify.json</c> next to the compose file, 0600 — it can hold a
/// bot token.
/// </summary>
public sealed class NotificationConfig
{
    public EventToggles Events { get; set; } = new();
    public WebhookChannel Webhook { get; set; } = new();
    public SlackChannel Slack { get; set; } = new();
    public TelegramChannel Telegram { get; set; } = new();

    public sealed class EventToggles
    {
        public bool DeploySucceeded { get; set; } = true;
        public bool DeployFailed { get; set; } = true;
        public bool HealthCheckFailed { get; set; } = true;
        public bool RolledBack { get; set; } = true;
    }

    public bool IsEventEnabled(string eventName) => eventName switch
    {
        NotificationEvents.DeploySucceeded => Events.DeploySucceeded,
        NotificationEvents.DeployFailed => Events.DeployFailed,
        NotificationEvents.HealthCheckFailed => Events.HealthCheckFailed,
        NotificationEvents.RolledBack => Events.RolledBack,
        _ => false,
    };
}

// The three channel shapes are top-level rather than nested in
// NotificationConfig because the server-global alert config stores exactly the
// same three. The property names on both configs are unchanged, so the JSON on
// disk is unchanged too — this is a move, not a migration.

// The string setters normalise null away: a property initializer only runs when
// the member is absent from the JSON, so an explicit `"url": null` in a
// hand-edited config would otherwise reach the notifier and throw.

public sealed class WebhookChannel
{
    private string _url = string.Empty;

    public bool Enabled { get; set; }

    public string Url { get => _url; set => _url = value ?? string.Empty; }
}

public sealed class SlackChannel
{
    private string _webhookUrl = string.Empty;

    public bool Enabled { get; set; }

    public string WebhookUrl { get => _webhookUrl; set => _webhookUrl = value ?? string.Empty; }
}

/// <summary>
/// Where alert mail goes. It carries no credentials of its own — the relay it
/// travels through is a server-wide setting, so this is only the recipient list.
/// </summary>
public sealed class EmailChannel
{
    private string _to = string.Empty;

    public bool Enabled { get; set; }

    /// <summary>One or more addresses, comma-separated, as an operator types them.</summary>
    public string To { get => _to; set => _to = value ?? string.Empty; }
}

public sealed class TelegramChannel
{
    private string _botToken = string.Empty;
    private string _chatId = string.Empty;

    public bool Enabled { get; set; }

    public string BotToken { get => _botToken; set => _botToken = value ?? string.Empty; }

    public string ChatId { get => _chatId; set => _chatId = value ?? string.Empty; }
}

/// <summary>Loads and saves <see cref="NotificationConfig"/> (camelCase JSON, 0600).</summary>
public sealed class NotificationConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;

    public NotificationConfigStore(string composeFilePath)
    {
        _path = PinqOpsStatePaths.NotifyConfigFile(composeFilePath);
    }

    public string Path_ => _path;

    public NotificationConfig Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<NotificationConfig>(SecureFile.ReadAllText(_path), SerializerOptions)
                    ?? new NotificationConfig();
            }
        }
        catch (JsonException)
        {
            // A corrupt config means "no notifications", never a failed deploy.
        }

        return new NotificationConfig();
    }

    public void Save(NotificationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // Atomic + owner-only (0600 from the first byte): this file can hold a
        // Telegram bot token, and File.WriteAllText would both expose it during
        // the create-then-chmod window and — on any save after the first — leave
        // the mode unfixed, as well as risk a torn write that Load reads as
        // corrupt and silently turns notifications off.
        SecureFile.WriteAllText(_path, JsonSerializer.Serialize(config, SerializerOptions));
    }
}
