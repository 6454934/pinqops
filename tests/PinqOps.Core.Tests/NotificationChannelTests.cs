using System.Net;
using PinqOps.Alerts;
using PinqOps.Notifications;
using PinqOps.Tests.Fakes;
using Xunit;

namespace PinqOps.Tests;

/// <summary>
/// The generic channel path, which alerts use. The deploy-shaped path is covered
/// by <see cref="NotifierTests"/>, and the fact that those assertions still hold
/// unchanged is the proof that generalizing the channels did not alter the JSON
/// existing webhook consumers parse.
/// </summary>
public class NotificationChannelTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private static NotificationMessage AlertMessageFor(AlertTransitionKind kind = AlertTransitionKind.Firing)
    {
        var rule = new AlertRule
        {
            Id = "abcd1234",
            Name = "Disk almost full",
            Metric = AlertMetrics.HostDisk,
            Threshold = 90,
            Severity = AlertSeverity.Critical,
        };
        var transition = new AlertTransition
        {
            Rule = rule,
            Series = string.Empty,
            Kind = kind,
            At = Now,
            Value = 93.4,
        };

        return new NotificationMessage
        {
            Event = AlertMessage.EventName(kind),
            Text = AlertMessage.Text(transition, "web-01"),
            Timestamp = Now,
            Payload = AlertMessage.Payload(transition, "web-01"),
            Severity = rule.Severity,
        };
    }

    [Fact]
    public async Task Webhook_SerializesThePayloadsRuntimeType()
    {
        // NotificationMessage.Payload is declared `object`. The generic
        // Serialize<T> overload would bind T to object and post "{}" — a body
        // every receiver accepts and nobody can act on.
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.OK, "");
        using var client = new HttpClient(handler);

        var delivered = await new WebhookNotifier("https://example.com/hook", client)
            .SendAsync(AlertMessageFor());

        Assert.True(delivered);
        var body = handler.LastRequestBody!;
        Assert.NotEqual("{}", body);
        Assert.Contains("\"event\":\"alert_firing\"", body, StringComparison.Ordinal);
        Assert.Contains("\"ruleId\":\"abcd1234\"", body, StringComparison.Ordinal);
        Assert.Contains("\"rule\":\"Disk almost full\"", body, StringComparison.Ordinal);
        Assert.Contains("\"metric\":\"host.disk\"", body, StringComparison.Ordinal);
        Assert.Contains("\"condition\":\"\\u003E 90%\"", body, StringComparison.Ordinal);
        Assert.Contains("\"value\":93.4", body, StringComparison.Ordinal);
        Assert.Contains("\"host\":\"web-01\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Webhook_ResolvedAlert_CarriesTheResolvedEvent()
    {
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.OK, "");
        using var client = new HttpClient(handler);

        await new WebhookNotifier("https://example.com/hook", client)
            .SendAsync(AlertMessageFor(AlertTransitionKind.Resolved));

        Assert.Contains("\"event\":\"alert_resolved\"", handler.LastRequestBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Slack_PostsTheAlertText()
    {
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.OK, "");
        using var client = new HttpClient(handler);

        var delivered = await new SlackNotifier("https://hooks.slack.com/services/x", client)
            .SendAsync(AlertMessageFor());

        Assert.True(delivered);
        var body = handler.LastRequestBody!;
        Assert.Contains("\"text\":", body, StringComparison.Ordinal);
        Assert.Contains("Disk almost full", body, StringComparison.Ordinal);
        Assert.Contains("93.4%", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Telegram_PostsTheAlertTextToTheChat()
    {
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.OK, "");
        using var client = new HttpClient(handler);

        var delivered = await new TelegramNotifier("123:abc", "-100200", client).SendAsync(AlertMessageFor());

        Assert.True(delivered);
        Assert.Equal(
            "https://api.telegram.org/bot123:abc/sendMessage",
            handler.LastRequest!.RequestUri!.ToString());
        Assert.Contains("\"chat_id\":\"-100200\"", handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.Contains("Disk almost full", handler.LastRequestBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeployNotification_AndItsMessage_ProduceTheSameWebhookBody()
    {
        var notification = new DeployNotification
        {
            Event = NotificationEvents.DeploySucceeded,
            Tag = "sha-abc123",
            Host = "server1",
            Timestamp = DateTimeOffset.UnixEpoch,
        };

        var viaDeploy = new RecordingHttpMessageHandler(HttpStatusCode.OK, "");
        using var deployClient = new HttpClient(viaDeploy);
        await new WebhookNotifier("https://example.com/hook", deployClient).SendAsync(notification);

        var viaMessage = new RecordingHttpMessageHandler(HttpStatusCode.OK, "");
        using var messageClient = new HttpClient(viaMessage);
        await new WebhookNotifier("https://example.com/hook", messageClient).SendAsync(notification.ToMessage());

        Assert.Equal(viaDeploy.LastRequestBody, viaMessage.LastRequestBody);
    }
}

public class ChannelFactoryTests
{
    private static readonly NotificationMessage Message = new()
    {
        Event = "alert_firing",
        Text = "something is on fire",
        Timestamp = DateTimeOffset.UnixEpoch,
        Payload = new { hello = "world" },
    };

    [Fact]
    public void Build_SkipsDisabledChannels()
    {
        using var client = new HttpClient();

        var channels = ChannelFactory.Build(
            new WebhookChannel { Enabled = false, Url = "https://example.com/hook" },
            new SlackChannel { Enabled = true, WebhookUrl = "https://hooks.slack.com/services/x" },
            new TelegramChannel(),
            client);

        Assert.Equal("slack", Assert.Single(channels).Channel);
    }

    [Fact]
    public void Build_IncludeDisabled_ReturnsAnythingConfigured()
    {
        using var client = new HttpClient();

        var channels = ChannelFactory.Build(
            new WebhookChannel { Url = "https://example.com/hook" },
            new SlackChannel(),
            new TelegramChannel(),
            client,
            includeDisabled: true);

        Assert.Equal("webhook", Assert.Single(channels).Channel);
    }

    [Fact]
    public void Build_OneBadUrl_DoesNotTakeTheOthersDownWithIt()
    {
        using var client = new HttpClient();
        var logged = new List<string>();

        var channels = ChannelFactory.Build(
            new WebhookChannel { Enabled = true, Url = "example.com/hook" }, // no scheme
            new SlackChannel { Enabled = true, WebhookUrl = "https://hooks.slack.com/services/x" },
            new TelegramChannel(),
            client,
            log: logged.Add);

        Assert.Equal("slack", Assert.Single(channels).Channel);
        Assert.Contains(logged, line => line.Contains("skipping the webhook", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendAsync_ReportsFailure_WithoutThrowing()
    {
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.InternalServerError, "");
        using var client = new HttpClient(handler);
        var logged = new List<string>();

        var delivered = await ChannelFactory.SendAsync(
            new WebhookNotifier("https://example.com/hook", client),
            Message,
            TimeSpan.FromSeconds(5),
            logged.Add,
            CancellationToken.None);

        Assert.False(delivered);
        Assert.Contains(logged, line => line.Contains("failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendAsync_DoesNotLeakTheChannelUrl()
    {
        // A Slack incoming-webhook URL is itself the credential; it must never
        // reach a log file.
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.Forbidden, "");
        using var client = new HttpClient(handler);
        var logged = new List<string>();

        await ChannelFactory.SendAsync(
            new SlackNotifier("https://hooks.slack.com/services/SECRET", client),
            Message,
            TimeSpan.FromSeconds(5),
            logged.Add,
            CancellationToken.None);

        Assert.DoesNotContain(logged, line => line.Contains("SECRET", StringComparison.Ordinal));
    }
}
