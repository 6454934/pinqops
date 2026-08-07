using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PinqOps.Alerts;
using PinqOps.Mail;
using PinqOps.Notifications;
using PinqOps.Secrets;
using PinqOps.Web.Tests.Fakes;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// The alert history's "Sent" column has to mean it was sent.
///
/// <para>On a fresh install "Where alerts are sent" is empty, so the dispatcher
/// builds no channels at all and the send loop's body never runs. It said so in
/// its return value and the scheduler threw that away, writing every transition
/// into the trail as <c>notified: true</c> — the operator's only record of
/// whether they were paged claiming they were. The same false yes appeared for a
/// batch whose every POST failed, and for a rule naming a channel that is
/// switched off.</para>
/// </summary>
public class AlertDeliveryTrailTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("pinqops-alert-trail-").FullName;

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The detached delivery has to finish before the trail can be read. Generous,
    /// because the alternative to waiting is a flaky assertion; nothing here talks
    /// to a real socket, so it is never approached.
    /// </summary>
    private static readonly TimeSpan TrailWait = TimeSpan.FromSeconds(10);

    private const string DownContainer = "api-1";

    private const string WebhookUrl = "https://alerts.example.com/hook";

    private static readonly string PsLine =
        $$"""{"Names":"{{DownContainer}}","State":"exited","Status":"Exited (137) 2 minutes ago"}""";

    private sealed class SilentTransport : IEmailTransport
    {
        public Task<string?> SendAsync(
            SmtpSettings settings,
            string? password,
            EmailEnvelope envelope,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private string In(string name) => Path.Combine(_directory, name);

    private static ProcessResult DockerListings(IReadOnlyList<string> arguments) =>
        arguments.Contains("ps")
            ? new ProcessResult(0, PsLine, string.Empty)
            : new ProcessResult(0, string.Empty, string.Empty);

    private static AlertRule DownRule() => new()
    {
        Id = "down1",
        Name = "Container down",
        Metric = AlertMetrics.ContainerDown,
        Target = "*",
        Threshold = 0,
        ForSeconds = 0,
        Severity = AlertSeverity.Critical,
    };

    private static AlertChannelConfig WebhookOnly() => new()
    {
        Webhook = new WebhookChannel { Enabled = true, Url = WebhookUrl },
    };

    /// <summary>
    /// A whole scheduler, wired to a docker that reports one stopped container and
    /// to whatever channels the caller configured.
    /// </summary>
    private (AlertScheduler Scheduler, AlertHistoryLog History) Scheduler(
        AlertChannelConfig? channels, HttpMessageHandler? handler)
    {
        var channelStore = new AlertChannelStore(In("alert-channels.json"));
        if (channels is not null)
        {
            channelStore.Save(channels);
        }

        var rules = new AlertRuleStore(In("alerts.json"));
        rules.Save(new AlertConfig { Rules = [DownRule()] });

        var mail = new MailService(
            new SmtpSettingsStore(In("smtp.json")),
            new SecretStore(In("secrets.json")),
            new SilentTransport(),
            NullLogger<MailService>.Instance);

        var dispatcher = new AlertDispatcher(
            channelStore,
            mail,
            NullLogger<AlertDispatcher>.Instance,
            handler is null ? null : new HttpClient(handler));

        var history = new AlertHistoryLog(In("alert-history.jsonl"));
        var sampler = new MetricSampler(
            new DockerService(new FakeProcessRunner((_, arguments) => DockerListings(arguments))),
            new SystemInfoService(),
            NullLogger<MetricSampler>.Instance);

        var scheduler = new AlertScheduler(
            sampler,
            new MetricHistoryStore(In("metrics.jsonl")),
            rules,
            new AlertStateStore(In("alert-state.json")),
            history,
            dispatcher,
            NullLogger<AlertScheduler>.Instance);

        return (scheduler, history);
    }

    /// <summary>
    /// Runs one tick and returns the trail entry it produced, waiting for the
    /// delivery that decides the flag.
    /// </summary>
    private async Task<AlertHistoryEntry> TrailEntryAfterOneTickAsync(
        AlertChannelConfig? channels, HttpMessageHandler? handler = null)
    {
        var (scheduler, history) = Scheduler(channels, handler);

        await scheduler.TickAsync(CancellationToken.None);

        var deadline = DateTimeOffset.UtcNow + TrailWait;
        IReadOnlyList<AlertHistoryEntry> entries;
        while ((entries = history.Read()).Count == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        var entry = Assert.Single(entries);
        Assert.Equal(DownContainer, entry.Series);
        Assert.Equal("firing", entry.Kind);
        return entry;
    }

    /// <summary>
    /// The fresh-install case: rules configured, nowhere for them to go. The alert
    /// must not be recorded as delivered.
    /// </summary>
    [Fact]
    public async Task WithNowhereToSendTheTrailDoesNotClaimTheAlertWasSent()
    {
        var entry = await TrailEntryAfterOneTickAsync(channels: null);

        Assert.False(entry.Notified);
    }

    /// <summary>
    /// And the flag still means something: a channel that took the message is
    /// recorded as having taken it.
    /// </summary>
    [Fact]
    public async Task AnAlertAChannelAcceptedIsRecordedAsSent()
    {
        var handler = new SequencedHttpMessageHandler().Enqueue(HttpStatusCode.OK);

        var entry = await TrailEntryAfterOneTickAsync(WebhookOnly(), handler);

        Assert.True(entry.Notified);
        Assert.Single(handler.Requests);
    }

    /// <summary>
    /// A receiver that refuses the POST is the same story as no receiver at all —
    /// nobody was paged, and the trail is where that is looked up afterwards.
    /// </summary>
    [Fact]
    public async Task AnAlertNoChannelAcceptedIsNotRecordedAsSent()
    {
        var handler = new SequencedHttpMessageHandler().Enqueue(HttpStatusCode.InternalServerError);

        var entry = await TrailEntryAfterOneTickAsync(WebhookOnly(), handler);

        Assert.False(entry.Notified);
    }
}
