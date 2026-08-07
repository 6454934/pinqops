using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PinqOps.Mail;

/// <summary>One message, before it is turned into bytes.</summary>
/// <param name="FromName">The display name; empty sends the bare address.</param>
public sealed record EmailEnvelope(
    string From,
    string FromName,
    IReadOnlyList<string> To,
    string Subject,
    string Body);

/// <summary>
/// Turns an envelope into the RFC 5322 message a relay is handed.
///
/// <para><b>The body is always base64.</b> It could be sent as-is for plain ASCII,
/// but then every message would have to be checked for the three things SMTP
/// cares about — a line over 998 characters, a line starting with a dot, and any
/// byte above 127 — and each of those is a corruption that only shows up in
/// somebody else's inbox. Base64 makes all three impossible by construction, at
/// the cost of a third more bytes on a message that is a few hundred long.</para>
///
/// <para><b>Headers are validated, not escaped.</b> A carriage return in a subject
/// is how a header is injected, and there is no safe way to keep going once one is
/// there: whatever produced it did not mean what it said.</para>
/// </summary>
public static class MailComposer
{
    /// <summary>SMTP's line ending. Not the platform's — this is a wire format.</summary>
    public const string LineEnding = "\r\n";

    /// <summary>Where a base64 body is wrapped. The specification's limit is 76.</summary>
    public const int Base64LineLength = 76;

    public const int MaximumSubjectLength = 400;

    /// <summary>
    /// An alert is a few hundred characters. The cap is here so a message built
    /// from something unbounded — a container's output, a stack trace — is refused
    /// rather than handed to a relay that will reject it after the upload.
    /// </summary>
    public const int MaximumBodyLength = 64 * 1024;

    /// <summary>
    /// The whole message, headers and body, ready for <c>DATA</c>.
    /// </summary>
    public static string Render(EmailEnvelope envelope, string messageId, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        var from = EmailAddress.Normalize(envelope.From);
        if (envelope.To.Count == 0)
        {
            throw new ArgumentException("A message needs at least one recipient.");
        }

        var recipients = envelope.To.Select(EmailAddress.Normalize).ToList();
        var subject = ValidateHeaderValue(envelope.Subject ?? string.Empty, "subject", MaximumSubjectLength);
        var displayName = ValidateHeaderValue(envelope.FromName ?? string.Empty, "sender name", 100);
        var body = envelope.Body ?? string.Empty;
        if (body.Length > MaximumBodyLength)
        {
            throw new ArgumentException($"A message body may be at most {MaximumBodyLength} characters.");
        }

        var builder = new StringBuilder();
        Header(builder, "From", displayName.Length > 0 ? $"{EncodeHeader(displayName)} <{from}>" : from);
        Header(builder, "To", string.Join(", ", recipients));
        Header(builder, "Subject", EncodeHeader(subject));
        // RFC 5322's own format, which is not one of DateTimeOffset's named ones:
        // "R" would print GMT and throw the real offset away.
        Header(builder, "Date", FormatDate(at));
        Header(builder, "Message-ID", $"<{messageId}>");
        Header(builder, "MIME-Version", "1.0");
        Header(builder, "Content-Type", "text/plain; charset=utf-8");
        Header(builder, "Content-Transfer-Encoding", "base64");
        builder.Append(LineEnding);
        builder.Append(Base64Body(body));
        return builder.ToString();
    }

    /// <summary>
    /// A fresh Message-ID. Relays and spam filters use it to tell a retry from a
    /// duplicate, so it has to be unique per message rather than per batch.
    /// </summary>
    public static string NewMessageId(string domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        return $"{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12))}@{domain}";
    }

    /// <summary>
    /// A header value, encoded only when it needs to be. ASCII passes through, so
    /// the common case stays readable in a raw message; anything else becomes an
    /// RFC 2047 encoded word, which is what makes a Turkish subject line arrive
    /// intact instead of as question marks.
    /// </summary>
    public static string EncodeHeader(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.All(char.IsAscii))
        {
            return value;
        }

        return "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "?=";
    }

    /// <summary>The body, base64, wrapped at <see cref="Base64LineLength"/>.</summary>
    public static string Base64Body(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
        var builder = new StringBuilder(encoded.Length + (encoded.Length / Base64LineLength * 2) + 2);
        for (var offset = 0; offset < encoded.Length; offset += Base64LineLength)
        {
            builder.Append(encoded.AsSpan(offset, Math.Min(Base64LineLength, encoded.Length - offset)));
            builder.Append(LineEnding);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The value, or a refusal. A newline in a header is how a second header — a
    /// second recipient, a different From — is smuggled into a message, and there
    /// is no reading of a subject line containing one that was intended.
    /// </summary>
    public static string ValidateHeaderValue(string value, string what, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length > maximumLength)
        {
            throw new ArgumentException($"The {what} may be at most {maximumLength} characters.");
        }

        if (value.Any(character => char.IsControl(character)))
        {
            throw new ArgumentException($"The {what} may not contain line breaks or control characters.");
        }

        return value.Trim();
    }

    private static string FormatDate(DateTimeOffset at) =>
        at.ToString("ddd, d MMM yyyy HH:mm:ss ", CultureInfo.InvariantCulture)
        + (at.Offset < TimeSpan.Zero ? "-" : "+")
        + at.Offset.ToString("hhmm", CultureInfo.InvariantCulture);

    private static void Header(StringBuilder builder, string name, string value) =>
        builder.Append(name).Append(": ").Append(value).Append(LineEnding);
}
