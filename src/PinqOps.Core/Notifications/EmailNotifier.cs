using System.Globalization;
using PinqOps.Mail;

namespace PinqOps.Notifications;

/// <summary>
/// Sends a notification as mail, through the configured relay.
///
/// <para>Unlike the other three channels this one has no URL of its own: where it
/// sends is the server's relay settings, and who it sends to is the channel's
/// recipient list. Both are resolved before the notifier is built, so it stays a
/// thing that sends one message and reports whether it went.</para>
/// </summary>
public sealed class EmailNotifier : INotificationChannel
{
    private readonly SmtpSettings _settings;
    private readonly string? _password;
    private readonly IReadOnlyList<string> _recipients;
    private readonly IEmailTransport _transport;
    private readonly Action<string>? _log;

    public EmailNotifier(
        SmtpSettings settings,
        string? password,
        IReadOnlyList<string> recipients,
        IEmailTransport transport,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(recipients);
        ArgumentNullException.ThrowIfNull(transport);

        if (recipients.Count == 0)
        {
            throw new ArgumentException("An email channel needs at least one recipient.", nameof(recipients));
        }

        _settings = settings;
        _password = password;
        _recipients = recipients;
        _transport = transport;
        _log = log;
    }

    public string Channel => "email";

    public async Task<bool> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var failure = await _transport
            .SendAsync(_settings, _password, EmailNotification.Compose(message, _settings, _recipients), cancellationToken)
            .ConfigureAwait(false);

        if (failure is not null)
        {
            _log?.Invoke($"the email channel did not deliver: {failure}");
        }

        return failure is null;
    }
}

/// <summary>
/// Turns a notification into a subject and a body.
///
/// <para>The subject is the notification's own one-line text rather than a
/// template. That line is written to be read on its own — it is what a Slack
/// message and a Telegram message contain in full — and a subject that reads
/// "pinqops alert" tells a phone's lock screen nothing.</para>
/// </summary>
public static class EmailNotification
{
    /// <summary>
    /// Where the subject is cut. Long subjects are truncated by mail clients
    /// anyway; cutting here means what survives is chosen rather than whatever
    /// happened to fit.
    /// </summary>
    public const int SubjectLength = 160;

    public static EmailEnvelope Compose(
        NotificationMessage message, SmtpSettings settings, IReadOnlyList<string> recipients)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(recipients);

        var text = message.Text ?? string.Empty;
        return new EmailEnvelope(
            settings.FromAddress,
            settings.FromName,
            recipients,
            Subject(text, message.Severity),
            Body(text, message));
    }

    /// <summary>The first line of the text, prefixed with the severity when there is one.</summary>
    private static string Subject(string text, string? severity)
    {
        var firstLine = FirstLine(text);
        var subject = string.IsNullOrWhiteSpace(severity)
            ? firstLine
            : $"[{severity.ToUpperInvariant()}] {firstLine}";

        return subject.Length <= SubjectLength ? subject : subject[..(SubjectLength - 1)] + "…";
    }

    private static string Body(string text, NotificationMessage message)
    {
        var body = text.Length > 0 ? text : message.Event ?? string.Empty;
        return body
            + Environment.NewLine + Environment.NewLine
            + message.Timestamp.ToString("u", CultureInfo.InvariantCulture)
            + Environment.NewLine
            + "Sent by pinqops. This message was not requested by anyone; it is an alert from a server you administer.";
    }

    /// <summary>
    /// The first line, with control characters gone. A subject header cannot carry
    /// a newline, and the composer refuses one rather than escaping it — so the
    /// cut happens here, where there is still a sensible answer.
    /// </summary>
    private static string FirstLine(string text)
    {
        var line = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return new string([.. line.Where(character => !char.IsControl(character))]).Trim();
    }
}
