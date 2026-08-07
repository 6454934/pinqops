using System.Text;
using System.Text.Json;

namespace PinqOps.Notifications;

/// <summary>POSTs the full notification as JSON to a user-supplied URL.</summary>
public sealed class WebhookNotifier : INotifier, INotificationChannel
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _httpClient;
    private readonly string _url;

    public WebhookNotifier(string url, HttpClient httpClient)
    {
        _url = ValidateHttpUrl(url);
        _httpClient = httpClient;
    }

    public string Channel => "webhook";

    public Task<bool> SendAsync(DeployNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return SendAsync(notification.ToMessage(), cancellationToken);
    }

    public async Task<bool> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            // Serialize by the payload's runtime type. The generic overload would
            // see the static type `object` and emit "{}" — an empty body that
            // every receiver would accept without complaint.
            var json = JsonSerializer.Serialize(message.Payload, message.Payload.GetType(), SerializerOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(_url, content, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public static string ValidateHttpUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            // No parameter name: .NET appends it to the message, and this message is
            // read by whoever typed the URL into the alert channels form, not by
            // whoever called this method. They were being told, after the sentence,
            // that the parameter was called "url".
            throw new ArgumentException($"'{url}' is not a valid http(s) URL.");
        }

        return url;
    }
}

/// <summary>
/// Sends a Slack incoming-webhook message (<c>{"text": ...}</c>). The same
/// payload shape works for Discord's <c>/slack</c> endpoint and Mattermost.
/// </summary>
public sealed class SlackNotifier : INotifier, INotificationChannel
{
    private readonly HttpClient _httpClient;
    private readonly string _webhookUrl;

    public SlackNotifier(string webhookUrl, HttpClient httpClient)
    {
        _webhookUrl = WebhookNotifier.ValidateHttpUrl(webhookUrl);
        _httpClient = httpClient;
    }

    public string Channel => "slack";

    public Task<bool> SendAsync(DeployNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return SendAsync(notification.ToMessage(), cancellationToken);
    }

    public async Task<bool> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(new { text = message.Text }), Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(_webhookUrl, content, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}

/// <summary>Sends a Telegram bot message via <c>sendMessage</c>.</summary>
public sealed class TelegramNotifier : INotifier, INotificationChannel
{
    private readonly HttpClient _httpClient;
    private readonly string _botToken;
    private readonly string _chatId;

    public TelegramNotifier(string botToken, string chatId, HttpClient httpClient)
    {
        // The token goes into a URL path; restrict it to the documented
        // <digits>:<base64url> shape instead of escaping (the ':' must stay
        // literal for the Bot API).
        if (string.IsNullOrWhiteSpace(botToken)
            || !botToken.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '_' or '-'))
        {
            throw new ArgumentException("Telegram bot token has an unexpected format.", nameof(botToken));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);
        _botToken = botToken;
        _chatId = chatId;
        _httpClient = httpClient;
    }

    public string Channel => "telegram";

    public Task<bool> SendAsync(DeployNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return SendAsync(notification.ToMessage(), cancellationToken);
    }

    public async Task<bool> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(new { chat_id = _chatId, text = message.Text }),
                Encoding.UTF8,
                "application/json");
            using var response = await _httpClient
                .PostAsync($"https://api.telegram.org/bot{_botToken}/sendMessage", content, cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
