using System.Net;
using PinqOps.Notifications;
using PinqOps.Tests.Fakes;
using Xunit;

namespace PinqOps.Tests;

public class NotifierTests
{
    private static DeployNotification Notification(string eventName = NotificationEvents.DeploySucceeded) => new()
    {
        Event = eventName,
        Tag = "sha-abc123",
        PreviousTag = "sha-old",
        Host = "server1",
        Timestamp = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task Webhook_PostsFullJsonPayload()
    {
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.OK, "");
        using var client = new HttpClient(handler);
        var notifier = new WebhookNotifier("https://example.com/hook", client);

        var delivered = await notifier.SendAsync(Notification());

        Assert.True(delivered);
        Assert.Equal("https://example.com/hook", handler.LastRequest!.RequestUri!.ToString());
        var body = handler.LastRequestBody!;
        Assert.Contains("\"event\":\"deploy_succeeded\"", body);
        Assert.Contains("\"tag\":\"sha-abc123\"", body);
        Assert.Contains("\"host\":\"server1\"", body);
    }

    [Fact]
    public async Task Webhook_Non2xx_ReturnsFalse()
    {
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.InternalServerError, "");
        using var client = new HttpClient(handler);

        Assert.False(await new WebhookNotifier("https://example.com/hook", client).SendAsync(Notification()));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/x")]
    [InlineData("")]
    public void Webhook_RejectsInvalidUrls(string url)
    {
        using var client = new HttpClient();
        Assert.Throws<ArgumentException>(() => new WebhookNotifier(url, client));
    }

    [Fact]
    public async Task Slack_PostsTextSummary()
    {
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.OK, "");
        using var client = new HttpClient(handler);
        var notifier = new SlackNotifier("https://hooks.slack.com/services/x", client);

        var delivered = await notifier.SendAsync(Notification(NotificationEvents.RolledBack));

        Assert.True(delivered);
        var body = handler.LastRequestBody!;
        Assert.Contains("\"text\":", body);
        Assert.Contains("rolled back", body);
        Assert.Contains("sha-abc123", body);
        Assert.Contains("was sha-old", body);
    }

    [Fact]
    public async Task Telegram_SendsToBotApiWithChatId()
    {
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.OK, "");
        using var client = new HttpClient(handler);
        var notifier = new TelegramNotifier("123:abc", "-100200", client);

        var delivered = await notifier.SendAsync(Notification());

        Assert.True(delivered);
        Assert.Equal("https://api.telegram.org/bot123:abc/sendMessage", handler.LastRequest!.RequestUri!.ToString());
        var body = handler.LastRequestBody!;
        Assert.Contains("\"chat_id\":\"-100200\"", body);
        Assert.Contains("deploy succeeded", body);
    }
}

public class NotificationDispatcherTests : IDisposable
{
    private readonly string _directory;
    private readonly string _composePath;

    public NotificationDispatcherTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-notif-tests").FullName;
        _composePath = Path.Combine(_directory, "docker-compose.yml");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private void SaveConfig(Action<NotificationConfig> mutate)
    {
        var config = new NotificationConfig();
        mutate(config);
        new NotificationConfigStore(_composePath).Save(config);
    }

    [Fact]
    public async Task Dispatch_DisabledEvent_SendsNothing()
    {
        SaveConfig(config =>
        {
            config.Webhook.Enabled = true;
            config.Webhook.Url = "https://example.com/hook";
            config.Events.DeploySucceeded = false;
        });
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.OK, "");
        using var client = new HttpClient(handler);
        using var dispatcher = new NotificationDispatcher(_composePath, httpClient: client);

        await dispatcher.OnDeployCompletedAsync(
            new DeployOutcome
            {
                Result = DeployRecordValues.ResultSucceeded,
                Trigger = DeployRecordValues.TriggerCi,
                HealthCheck = DeployRecordValues.HealthPassed,
            },
            CancellationToken.None);

        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task Dispatch_EnabledChannel_SendsMappedEvent()
    {
        SaveConfig(config =>
        {
            config.Webhook.Enabled = true;
            config.Webhook.Url = "https://example.com/hook";
        });
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.OK, "");
        using var client = new HttpClient(handler);
        using var dispatcher = new NotificationDispatcher(_composePath, httpClient: client);

        await dispatcher.OnDeployCompletedAsync(
            new DeployOutcome
            {
                Result = DeployRecordValues.ResultFailed,
                Trigger = DeployRecordValues.TriggerCi,
                HealthCheck = DeployRecordValues.HealthFailed,
                Error = "service 'app' is unhealthy",
            },
            CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        var body = handler.LastRequestBody!;
        Assert.Contains("health_check_failed", body);
    }

    [Fact]
    public async Task Dispatch_NoConfigFile_IsANoop()
    {
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.OK, "");
        using var client = new HttpClient(handler);
        using var dispatcher = new NotificationDispatcher(_composePath, httpClient: client);

        await dispatcher.OnDeployCompletedAsync(
            new DeployOutcome
            {
                Result = DeployRecordValues.ResultSucceeded,
                Trigger = DeployRecordValues.TriggerCi,
                HealthCheck = DeployRecordValues.HealthPassed,
            },
            CancellationToken.None);

        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task SendTest_UnconfiguredChannel_Throws()
    {
        using var dispatcher = new NotificationDispatcher(_composePath);

        await Assert.ThrowsAsync<ArgumentException>(() => dispatcher.SendTestAsync("slack"));
    }

    [Fact]
    public async Task SendTest_ConfiguredButDisabledChannel_StillSends()
    {
        SaveConfig(config => config.Slack.WebhookUrl = "https://hooks.slack.com/services/x");
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.OK, "");
        using var client = new HttpClient(handler);
        using var dispatcher = new NotificationDispatcher(_composePath, httpClient: client);

        Assert.True(await dispatcher.SendTestAsync("slack"));
        Assert.NotNull(handler.LastRequest);
    }
}

public class NotificationConfigStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _composePath;

    public NotificationConfigStoreTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-notifcfg-tests").FullName;
        _composePath = Path.Combine(_directory, "docker-compose.yml");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void RoundTrip_PersistsAllChannels_WithOwnerOnlyPermissions()
    {
        var store = new NotificationConfigStore(_composePath);
        store.Save(new NotificationConfig
        {
            Events = { DeploySucceeded = false },
            Webhook = { Enabled = true, Url = "https://example.com/hook" },
            Telegram = { Enabled = true, BotToken = "123:abc", ChatId = "42" },
        });

        var loaded = store.Load();
        Assert.False(loaded.Events.DeploySucceeded);
        Assert.True(loaded.Events.DeployFailed);
        Assert.True(loaded.Webhook.Enabled);
        Assert.Equal("https://example.com/hook", loaded.Webhook.Url);
        Assert.Equal("123:abc", loaded.Telegram.BotToken);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.Path_));
        }
    }

    [Fact]
    public void Load_MissingOrCorrupt_ReturnsDefaults()
    {
        var store = new NotificationConfigStore(_composePath);
        Assert.True(store.Load().Events.DeployFailed);

        Directory.CreateDirectory(Path.Combine(_directory, ".pinqops"));
        File.WriteAllText(store.Path_, "{broken");
        Assert.True(store.Load().Events.DeployFailed);
    }
}
