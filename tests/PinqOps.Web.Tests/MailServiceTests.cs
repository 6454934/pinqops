using Microsoft.Extensions.Logging.Abstractions;
using PinqOps.Alerts;
using PinqOps.Mail;
using PinqOps.Notifications;
using PinqOps.Secrets;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// The relay pinqops sends its own mail through, and the alert channel that uses
/// it. The transport is faked here — what it does with a socket is tested in the
/// core suite — so these are about the two things above it: whether the password
/// is fetched from the vault rather than stored, and whether an alert actually
/// reaches the channel.
/// </summary>
public class MailServiceTests : IDisposable
{
    private const string Password = "relay-password";

    private readonly string _directory;
    private readonly SmtpSettingsStore _settings;
    private readonly SecretStore _secrets;
    private readonly RecordingTransport _transport = new();

    public MailServiceTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-mail-tests").FullName;
        _settings = new SmtpSettingsStore(Path.Combine(_directory, "mail.json"));
        _secrets = new SecretStore(Path.Combine(_directory, "secrets.json"));
        _secrets.Set(SecretScopes.Global, "SMTP_PASSWORD", Password, null, "tester", DateTimeOffset.UtcNow);
        _settings.Update<object?>(settings =>
        {
            settings.Enabled = true;
            settings.Host = "smtp.example.com";
            settings.Port = 587;
            settings.Security = SmtpSecurity.StartTls;
            settings.Username = "apikey";
            settings.SecretName = "SMTP_PASSWORD";
            settings.FromAddress = "pinqops@example.com";
            settings.FromName = "pinqops";
            return null;
        });
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private MailService Service() =>
        new(_settings, _secrets, _transport, NullLogger<MailService>.Instance);

    [Fact]
    public async Task AMessageReachesTheRelay()
    {
        Assert.Null(await Service().SendAsync(["ops@example.com"], "Subject", "Body"));

        var sent = Assert.Single(_transport.Sent);
        Assert.Equal(["ops@example.com"], sent.Envelope.To);
        Assert.Equal("Subject", sent.Envelope.Subject);
    }

    /// <summary>
    /// The settings file holds the name of a vault entry, never the password. It is
    /// read at send time rather than held, so a rotation takes effect on the next
    /// message rather than on the next restart.
    /// </summary>
    [Fact]
    public async Task ThePasswordComesFromTheVaultAndIsNeverInTheSettingsFile()
    {
        await Service().SendAsync(["ops@example.com"], "Subject", "Body");

        Assert.Equal(Password, Assert.Single(_transport.Sent).Password);
        Assert.DoesNotContain(Password, File.ReadAllText(_settings.Path_), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARotatedPasswordIsPickedUpWithoutARestart()
    {
        var service = Service();
        await service.SendAsync(["ops@example.com"], "Subject", "Body");

        _secrets.Set(SecretScopes.Global, "SMTP_PASSWORD", "rotated", null, "tester", DateTimeOffset.UtcNow);
        await service.SendAsync(["ops@example.com"], "Subject", "Body");

        Assert.Equal(["relay-password", "rotated"], _transport.Sent.Select(sent => sent.Password));
    }

    [Fact]
    public async Task NothingIsSentWhileTheRelayIsSwitchedOff()
    {
        _settings.Update<object?>(settings => { settings.Enabled = false; return null; });

        Assert.NotNull(await Service().SendAsync(["ops@example.com"], "Subject", "Body"));
        Assert.Empty(_transport.Sent);
    }

    // ---- the alert channel ----------------------------------------------------

    [Fact]
    public void TheChannelIsBuiltWhenTheRelayAndTheRecipientsAreThere()
    {
        var channel = Service().BuildChannel(
            new EmailChannel { Enabled = true, To = "ops@example.com" }, includeDisabled: false, log: null);

        Assert.Equal(AlertChannelNames.Email, channel?.Channel);
    }

    [Fact]
    public async Task AnAlertReachesTheRelayThroughTheChannel()
    {
        var channel = Service().BuildChannel(
            new EmailChannel { Enabled = true, To = "ops@example.com, oncall@example.com" },
            includeDisabled: false,
            log: null)!;

        Assert.True(await channel.SendAsync(new NotificationMessage
        {
            Event = AlertMessage.FiringEvent,
            Text = "pinqops @ web: host CPU is 95% (> 90%)",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = new { rule = "cpu" },
            Severity = AlertSeverity.Critical,
        }));

        var sent = Assert.Single(_transport.Sent);
        Assert.Equal(["ops@example.com", "oncall@example.com"], sent.Envelope.To);

        // The subject is the alert's own one-line text, so a phone's lock screen
        // shows what happened rather than the word "alert".
        Assert.Equal("[CRITICAL] pinqops @ web: host CPU is 95% (> 90%)", sent.Envelope.Subject);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    public void AChannelWithNoUsableRecipientsIsSkippedRatherThanBuilt(string to) =>
        Assert.Null(Service().BuildChannel(
            new EmailChannel { Enabled = true, To = to }, includeDisabled: false, log: null));

    /// <summary>
    /// One channel configured wrongly skips that channel; the rest keep working.
    /// Here the whole relay is unusable, which is the same contract.
    /// </summary>
    [Fact]
    public void AnUnusableRelayIsSkippedAndSaysWhy()
    {
        _settings.Update<object?>(settings => { settings.Host = string.Empty; return null; });
        var reasons = new List<string>();

        var channel = Service().BuildChannel(
            new EmailChannel { Enabled = true, To = "ops@example.com" }, includeDisabled: false, reasons.Add);

        Assert.Null(channel);
        Assert.Contains(reasons, reason => reason.Contains("email notification channel", StringComparison.Ordinal));
    }

    [Fact]
    public void ASwitchedOffChannelIsOnlyBuiltForATest()
    {
        var off = new EmailChannel { Enabled = false, To = "ops@example.com" };

        Assert.Null(Service().BuildChannel(off, includeDisabled: false, log: null));
        Assert.NotNull(Service().BuildChannel(off, includeDisabled: true, log: null));
    }

    private sealed record SentMessage(SmtpSettings Settings, string? Password, EmailEnvelope Envelope);

    private sealed class RecordingTransport : IEmailTransport
    {
        internal List<SentMessage> Sent { get; } = [];

        public Task<string?> SendAsync(
            SmtpSettings settings,
            string? password,
            EmailEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(new SentMessage(settings, password, envelope));
            return Task.FromResult<string?>(null);
        }
    }
}
