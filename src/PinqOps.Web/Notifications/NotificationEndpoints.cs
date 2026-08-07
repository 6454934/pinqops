using PinqOps;
using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>
/// Per-app deploy notifications (success, failure, health, rollback).
/// </summary>
public static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/notifications", async Task<object?> (HttpContext context, UiConfigStore store) =>
        {
            await Task.CompletedTask;
            var config = new PinqOps.Notifications.NotificationConfigStore(ResolveApp(store, context).ComposeFile).Load();
            return new
            {
                events = new
                {
                    deploySucceeded = config.Events.DeploySucceeded,
                    deployFailed = config.Events.DeployFailed,
                    healthCheckFailed = config.Events.HealthCheckFailed,
                    rolledBack = config.Events.RolledBack,
                },
                webhook = new { enabled = config.Webhook.Enabled, url = config.Webhook.Url },
                slack = new { enabled = config.Slack.Enabled, webhookUrl = config.Slack.WebhookUrl },
                telegram = new
                {
                    enabled = config.Telegram.Enabled,
                    botTokenMasked = config.Telegram.BotToken is { Length: > 4 } token ? $"••••••••{token[^4..]}" : null,
                    chatId = config.Telegram.ChatId,
                },
            };
        });

        app.MapPost("/api/notifications", async Task<object?> (HttpContext context, UiConfigStore store) =>
        {
            var request = await context.Request.ReadFromJsonAsync<NotificationsRequest>()
                ?? throw new ArgumentException("Invalid request body.");

            var configStore = new PinqOps.Notifications.NotificationConfigStore(ResolveApp(store, context).ComposeFile);
            var config = configStore.Load();

            if (request.Events is { } events)
            {
                config.Events.DeploySucceeded = events.DeploySucceeded ?? config.Events.DeploySucceeded;
                config.Events.DeployFailed = events.DeployFailed ?? config.Events.DeployFailed;
                config.Events.HealthCheckFailed = events.HealthCheckFailed ?? config.Events.HealthCheckFailed;
                config.Events.RolledBack = events.RolledBack ?? config.Events.RolledBack;
            }

            if (request.Webhook is { } webhook)
            {
                config.Webhook.Enabled = webhook.Enabled ?? config.Webhook.Enabled;
                if (webhook.Url is not null)
                {
                    config.Webhook.Url = webhook.Url.Trim();
                }
            }

            if (request.Slack is { } slack)
            {
                config.Slack.Enabled = slack.Enabled ?? config.Slack.Enabled;
                if (slack.WebhookUrl is not null)
                {
                    config.Slack.WebhookUrl = slack.WebhookUrl.Trim();
                }
            }

            if (request.Telegram is { } telegram)
            {
                config.Telegram.Enabled = telegram.Enabled ?? config.Telegram.Enabled;
                // An absent/blank token means "keep the stored one" (it is masked in GET).
                if (!string.IsNullOrWhiteSpace(telegram.BotToken))
                {
                    config.Telegram.BotToken = telegram.BotToken.Trim();
                }

                if (telegram.ChatId is not null)
                {
                    config.Telegram.ChatId = telegram.ChatId.Trim();
                }
            }

            // URLs are validated eagerly so a typo is a 400 now, not a silent
            // delivery failure later.
            if (config.Webhook.Enabled && config.Webhook.Url.Length > 0)
            {
                PinqOps.Notifications.WebhookNotifier.ValidateHttpUrl(config.Webhook.Url);
            }

            if (config.Slack.Enabled && config.Slack.WebhookUrl.Length > 0)
            {
                PinqOps.Notifications.WebhookNotifier.ValidateHttpUrl(config.Slack.WebhookUrl);
            }

            configStore.Save(config);
            return new { ok = true };
        });

        app.MapPost("/api/notifications/test", async Task<object?> (HttpContext context, UiConfigStore store) =>
        {
            var request = await context.Request.ReadFromJsonAsync<NotificationTestRequest>();
            if (request?.Channel is not { Length: > 0 } channel)
            {
                throw new ArgumentException("A channel is required.");
            }

            using var dispatcher = new PinqOps.Notifications.NotificationDispatcher(ResolveApp(store, context).ComposeFile);
            var delivered = await dispatcher.SendTestAsync(channel);
            return new { ok = delivered, delivered };
        });
    }
}
