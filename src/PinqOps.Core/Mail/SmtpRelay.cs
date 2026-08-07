using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace PinqOps.Mail;

/// <summary>Hands one message to a relay.</summary>
public interface IEmailTransport
{
    /// <summary>
    /// Null when the relay took the message, otherwise why it did not — the
    /// server's own reply where there is one, because "550 5.7.1 relaying denied"
    /// says what no message of ours could.
    /// </summary>
    Task<string?> SendAsync(
        SmtpSettings settings,
        string? password,
        EmailEnvelope envelope,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A small SMTP client: connect, greet, secure, authenticate, send, quit.
///
/// <para><b>Why not <c>System.Net.Mail.SmtpClient</c>.</b> It cannot open a
/// connection that is TLS from the first byte, which is what port 465 is, and 465
/// is the port several providers document first. The rest of what it does is the
/// handful of commands below.</para>
///
/// <para><b>It fails closed, twice.</b> If <c>STARTTLS</c> was asked for and the
/// server does not offer it, this stops — continuing in the clear would be a
/// downgrade that nothing reports. And a password is never sent over an
/// unencrypted connection unless the operator has explicitly allowed it, because
/// the alternative is a credential on the wire and a send that looks like it
/// worked.</para>
/// </summary>
public sealed class SmtpRelay : IEmailTransport
{
    /// <summary>
    /// The whole conversation's budget. A relay that has stopped answering must
    /// not hold an alert dispatch — or the operator's test click — open forever.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Caps on one reply, so a hostile or broken server cannot make this read forever.</summary>
    public const int MaximumReplyLines = 100;

    public const int MaximumLineLength = 4096;

    public async Task<string?> SendAsync(
        SmtpSettings settings,
        string? password,
        EmailEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(envelope);

        if (SmtpSettingsValidator.Validate(settings) is { } invalid)
        {
            return invalid;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);

        try
        {
            return await ConverseAsync(settings, password, envelope, deadline.Token).ConfigureAwait(false);
        }
        catch (SmtpRefusedException refusal)
        {
            return refusal.Message;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"The relay at {settings.Host} did not answer within {Timeout.TotalSeconds:0} seconds.";
        }
        catch (Exception exception) when (exception is SocketException or IOException or AuthenticationException)
        {
            // Never the stack, never the credentials: what reaches the operator is
            // what they can act on, and the detail is logged by the caller.
            return $"Could not reach the relay at {settings.Host}: {exception.Message}";
        }
    }

    private static async Task<string?> ConverseAsync(
        SmtpSettings settings, string? password, EmailEnvelope envelope, CancellationToken cancellationToken)
    {
        var host = settings.Host.Trim();
        using var client = new TcpClient();
        await client.ConnectAsync(host, settings.Port, cancellationToken).ConfigureAwait(false);

        Stream stream = client.GetStream();
        var secured = false;
        try
        {
            if (string.Equals(settings.Security, SmtpSecurity.Tls, StringComparison.Ordinal))
            {
                stream = await SecureAsync(stream, host, cancellationToken).ConfigureAwait(false);
                secured = true;
            }

            var connection = new SmtpConnection(stream);
            await Expect(connection, 220, cancellationToken).ConfigureAwait(false);

            var ehloName = settings.EhloName.Trim() is { Length: > 0 } configured
                ? configured
                : EmailAddress.DomainOf(settings.FromAddress);
            var capabilities = await GreetAsync(connection, ehloName, cancellationToken).ConfigureAwait(false);

            if (string.Equals(settings.Security, SmtpSecurity.StartTls, StringComparison.Ordinal))
            {
                if (!capabilities.Contains("STARTTLS"))
                {
                    // Carrying on unencrypted here is a downgrade the operator asked
                    // not to have and would never be told about.
                    return $"The relay at {host} does not offer STARTTLS. Use port 465 with implicit TLS, or say so explicitly by choosing no encryption.";
                }

                await Command(connection, "STARTTLS", 220, cancellationToken).ConfigureAwait(false);
                stream = await SecureAsync(stream, host, cancellationToken).ConfigureAwait(false);
                secured = true;
                // A fresh reader, so anything the server sent between its 220 and
                // the handshake is dropped rather than read as if it had arrived
                // inside the encrypted session.
                connection = new SmtpConnection(stream);
                // The capability list before and after TLS are different documents;
                // AUTH in particular is normally only advertised once secured.
                capabilities = await GreetAsync(connection, ehloName, cancellationToken).ConfigureAwait(false);
            }

            var username = settings.Username.Trim();
            if (username.Length > 0)
            {
                if (!secured && !settings.AllowInsecureAuth)
                {
                    return "The relay password would be sent unencrypted. Use STARTTLS or TLS, or allow it deliberately.";
                }

                await AuthenticateAsync(connection, capabilities, username, password ?? string.Empty, cancellationToken)
                    .ConfigureAwait(false);
            }

            await SendMessageAsync(connection, settings, envelope, cancellationToken).ConfigureAwait(false);

            // Best effort: the message is accepted at the 250 after the dot, and a
            // relay that drops the connection instead of answering QUIT has still
            // taken it.
            try
            {
                await Command(connection, "QUIT", 221, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SmtpRefusedException or IOException or SocketException)
            {
            }

            return null;
        }
        finally
        {
            if (stream is SslStream secureStream)
            {
                await secureStream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<SslStream> SecureAsync(Stream stream, string host, CancellationToken cancellationToken)
    {
        // Default validation, deliberately: a relay whose certificate does not
        // check out is either misconfigured or is not the relay. An operator who
        // knows their local mail server has a self-signed certificate can choose
        // no encryption for it, which is at least honest about what it is.
        var secure = new SslStream(stream, leaveInnerStreamOpen: false);
        await secure.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cancellationToken)
            .ConfigureAwait(false);
        return secure;
    }

    /// <summary>Sends <c>EHLO</c> and returns the capability keywords the relay listed.</summary>
    private static async Task<HashSet<string>> GreetAsync(
        SmtpConnection connection, string ehloName, CancellationToken cancellationToken)
    {
        var reply = await connection.ExchangeAsync($"EHLO {ehloName}", cancellationToken).ConfigureAwait(false);
        if (reply.Code != 250)
        {
            // A server too old for EHLO cannot do TLS or AUTH either, so there is
            // nothing to fall back to that would be worth having.
            throw new SmtpRefusedException($"The relay refused EHLO: {reply.Text}");
        }

        var capabilities = new HashSet<string>(StringComparer.Ordinal);

        // The first line is the server's greeting — its hostname and whatever it
        // feels like saying. The capabilities are the lines after it.
        foreach (var line in reply.Lines.Skip(1))
        {
            var keyword = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (keyword is { Length: > 0 })
            {
                capabilities.Add(keyword.ToUpperInvariant());
            }

            if (line.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var mechanism in line[4..].Split([' ', '='], StringSplitOptions.RemoveEmptyEntries))
                {
                    capabilities.Add("AUTH " + mechanism.ToUpperInvariant());
                }
            }
        }

        return capabilities;
    }

    private static async Task AuthenticateAsync(
        SmtpConnection connection,
        HashSet<string> capabilities,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        // PLAIN first: one round trip, and every relay that offers LOGIN offers it.
        // LOGIN is the fallback for the few that only do that.
        if (capabilities.Contains("AUTH PLAIN") || !capabilities.Contains("AUTH LOGIN"))
        {
            var credential = Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{username}\0{password}"));
            var reply = await connection.ExchangeAsync($"AUTH PLAIN {credential}", cancellationToken).ConfigureAwait(false);
            if (reply.Code != 235)
            {
                throw new SmtpRefusedException($"The relay rejected the sign-in: {reply.Text}");
            }

            return;
        }

        await Command(connection, "AUTH LOGIN", 334, cancellationToken).ConfigureAwait(false);
        await Command(connection, Convert.ToBase64String(Encoding.UTF8.GetBytes(username)), 334, cancellationToken)
            .ConfigureAwait(false);

        var final = await connection
            .ExchangeAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(password)), cancellationToken)
            .ConfigureAwait(false);
        if (final.Code != 235)
        {
            throw new SmtpRefusedException($"The relay rejected the sign-in: {final.Text}");
        }
    }

    private static async Task SendMessageAsync(
        SmtpConnection connection, SmtpSettings settings, EmailEnvelope envelope, CancellationToken cancellationToken)
    {
        var from = EmailAddress.Normalize(settings.FromAddress);
        await Command(connection, $"MAIL FROM:<{from}>", 250, cancellationToken).ConfigureAwait(false);

        foreach (var recipient in envelope.To)
        {
            var reply = await connection
                .ExchangeAsync($"RCPT TO:<{EmailAddress.Normalize(recipient)}>", cancellationToken)
                .ConfigureAwait(false);

            // 251 is "not local, will forward", which is a yes.
            if (reply.Code is not (250 or 251))
            {
                throw new SmtpRefusedException($"The relay refused the recipient {recipient}: {reply.Text}");
            }
        }

        await Command(connection, "DATA", 354, cancellationToken).ConfigureAwait(false);

        var message = MailComposer.Render(
            envelope with { From = from, FromName = settings.FromName },
            MailComposer.NewMessageId(EmailAddress.DomainOf(from)),
            DateTimeOffset.Now);

        var accepted = await connection.ExchangeAsync(DotStuff(message) + ".", cancellationToken).ConfigureAwait(false);
        if (accepted.Code != 250)
        {
            throw new SmtpRefusedException($"The relay did not accept the message: {accepted.Text}");
        }
    }

    /// <summary>
    /// Doubles a leading dot on every line, which is what stops a line reading
    /// <c>.</c> from ending the message early.
    ///
    /// <para>A base64 body can never produce one and the headers are validated, so
    /// nothing in a message built here needs it. It is here so that stays a
    /// property of this transport rather than of the composer that happens to feed
    /// it.</para>
    /// </summary>
    public static string DotStuff(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var stuffed = message.Replace(
            MailComposer.LineEnding + ".", MailComposer.LineEnding + "..", StringComparison.Ordinal);
        return stuffed.StartsWith('.') ? "." + stuffed : stuffed;
    }

    private static async Task Expect(SmtpConnection connection, int code, CancellationToken cancellationToken)
    {
        var reply = await connection.ReadReplyAsync(cancellationToken).ConfigureAwait(false);
        if (reply.Code != code)
        {
            throw new SmtpRefusedException($"The relay answered unexpectedly: {reply.Text}");
        }
    }

    private static async Task Command(
        SmtpConnection connection, string command, int code, CancellationToken cancellationToken)
    {
        var reply = await connection.ExchangeAsync(command, cancellationToken).ConfigureAwait(false);
        if (reply.Code != code)
        {
            throw new SmtpRefusedException($"The relay answered unexpectedly: {reply.Text}");
        }
    }
}

/// <summary>One reply: its code, every line of it, and the text to show a human.</summary>
public sealed record SmtpReply(int Code, IReadOnlyList<string> Lines)
{
    public string Text => $"{Code} {string.Join(" ", Lines)}".Trim();
}

/// <summary>A relay said no. Carries the relay's own wording, which is the useful part.</summary>
public sealed class SmtpRefusedException : Exception
{
    public SmtpRefusedException(string message)
        : base(message)
    {
    }

    public SmtpRefusedException()
    {
    }

    public SmtpRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Line-oriented reading and writing over the socket. SMTP replies are one or
/// more lines with the same code, continuation marked by a <c>-</c> after it —
/// so a reader that took one line would read half of every capability list.
/// </summary>
internal sealed class SmtpConnection
{
    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[8192];
    private int _start;
    private int _end;

    internal SmtpConnection(Stream stream) => _stream = stream;

    internal async Task<SmtpReply> ExchangeAsync(string command, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(command + MailComposer.LineEnding);
        await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return await ReadReplyAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<SmtpReply> ReadReplyAsync(CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var code = 0;

        for (var read = 0; read < SmtpRelay.MaximumReplyLines; read++)
        {
            var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line.Length < 3 || !int.TryParse(line[..3], out code))
            {
                throw new SmtpRefusedException($"The relay sent something that is not an SMTP reply: {Trim(line)}");
            }

            lines.Add(line.Length > 4 ? line[4..] : string.Empty);

            // "250-" continues, "250 " ends. A reply shorter than four characters
            // is the last line of a bare code.
            if (line.Length < 4 || line[3] != '-')
            {
                return new SmtpReply(code, lines);
            }
        }

        throw new SmtpRefusedException($"The relay sent more than {SmtpRelay.MaximumReplyLines} reply lines.");
    }

    private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder();

        while (true)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await _stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                if (_end == 0)
                {
                    throw new SmtpRefusedException("The relay closed the connection.");
                }
            }

            while (_start < _end)
            {
                var character = (char)_buffer[_start++];
                if (character == '\n')
                {
                    return line.ToString().TrimEnd('\r');
                }

                line.Append(character);
                if (line.Length > SmtpRelay.MaximumLineLength)
                {
                    throw new SmtpRefusedException("The relay sent a line longer than SMTP allows.");
                }
            }
        }
    }

    private static string Trim(string line) => line.Length <= 120 ? line : line[..120] + "…";
}
