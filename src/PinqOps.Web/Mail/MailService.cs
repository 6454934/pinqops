using PinqOps.Mail;
using PinqOps.Notifications;
using PinqOps.Secrets;

namespace PinqOps.Web;

/// <summary>
/// How this server sends its own mail.
///
/// <para><b>A relay, not a mail server.</b> pinqops hands a message to something
/// that already speaks for a domain — a provider, or a mail server the operator
/// runs — and reports what it said. It does not receive mail, hold a queue, retry
/// on its own schedule or sign anything. The page says this in as many words,
/// because "email settings" reads like a mail server to everyone who has not been
/// told otherwise.</para>
///
/// <para><b>The password is read per send.</b> Holding it in memory would mean a
/// rotation in the vault did not take effect until a restart, and the read is a
/// file the process already owns.</para>
/// </summary>
public sealed class MailService
{
    private readonly SmtpSettingsStore _settings;
    private readonly SecretStore _secrets;
    private readonly IEmailTransport _transport;
    private readonly ILogger<MailService> _logger;

    public MailService(
        SmtpSettingsStore settings,
        SecretStore secrets,
        IEmailTransport transport,
        ILogger<MailService> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _secrets = secrets;
        _transport = transport;
        _logger = logger;
    }

    public SmtpSettingsStore Store => _settings;

    /// <summary>Whether a message sent right now would have somewhere to go.</summary>
    public bool Ready
    {
        get
        {
            var settings = _settings.Load();
            return settings.Enabled && SmtpSettingsValidator.Validate(settings) is null;
        }
    }

    /// <summary>
    /// Sends one message. Null when the relay took it, otherwise why it did not.
    /// </summary>
    public async Task<string?> SendAsync(
        IReadOnlyList<string> recipients,
        string subject,
        string body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipients);

        var settings = _settings.Load();
        if (!settings.Enabled)
        {
            return "Sending mail is switched off.";
        }

        if (SmtpSettingsValidator.Validate(settings) is { } invalid)
        {
            return invalid;
        }

        if (recipients.Count == 0)
        {
            return "A recipient is required.";
        }

        var envelope = new EmailEnvelope(
            settings.FromAddress, settings.FromName, recipients, subject, body);

        var failure = await _transport
            .SendAsync(settings, PasswordOrNull(settings), envelope, cancellationToken)
            .ConfigureAwait(false);

        if (failure is null)
        {
            // The recipients are worth recording — this is the audit trail for who
            // this server mailed — and the body never is.
            _logger.LogInformation("Mail sent to {Count} recipient(s) via {Host}", recipients.Count, settings.Host);
        }
        else
        {
            _logger.LogWarning("Mail via {Host} was not delivered: {Detail}", settings.Host, failure);
        }

        return failure;
    }

    /// <summary>
    /// The alert channel, or null when mail is not a place alerts can go right now.
    /// Built per dispatch rather than held, so turning the relay off takes effect on
    /// the next alert rather than on the next restart.
    /// </summary>
    public INotificationChannel? BuildChannel(EmailChannel channel, bool includeDisabled, Action<string>? log)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (!channel.Enabled && !includeDisabled)
        {
            return null;
        }

        var settings = _settings.Load();
        if (!settings.Enabled && !includeDisabled)
        {
            return null;
        }

        if (SmtpSettingsValidator.Validate(settings) is { } invalid)
        {
            log?.Invoke($"skipping the email notification channel: {invalid}");
            return null;
        }

        IReadOnlyList<string> recipients;
        try
        {
            recipients = EmailAddress.ParseList(channel.To);
        }
        catch (ArgumentException exception)
        {
            log?.Invoke($"skipping the email notification channel: {exception.Message}");
            return null;
        }

        if (recipients.Count == 0)
        {
            log?.Invoke("skipping the email notification channel: it has no recipients");
            return null;
        }

        return new EmailNotifier(settings, PasswordOrNull(settings), recipients, _transport, log);
    }

    /// <summary>
    /// The relay password from the vault, or null when there is no username to use
    /// it with. A missing vault entry is null too: the transport then sends nothing
    /// and the relay says what it thinks of that, which is a better message than one
    /// invented here.
    /// </summary>
    private string? PasswordOrNull(SmtpSettings settings)
    {
        if (settings.Username.Trim().Length == 0)
        {
            return null;
        }

        try
        {
            return _secrets.Reveal(SecretScopes.Global, settings.SecretName, version: null).Value;
        }
        catch (Exception exception) when (exception is KeyNotFoundException or ArgumentException)
        {
            _logger.LogWarning("The vault has no entry called {Name} for the mail relay", settings.SecretName);
            return null;
        }
    }
}
