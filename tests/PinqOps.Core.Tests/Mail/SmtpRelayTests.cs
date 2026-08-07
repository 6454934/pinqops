using System.Net;
using System.Net.Sockets;
using System.Text;
using PinqOps.Mail;
using Xunit;

namespace PinqOps.Tests.Mail;

/// <summary>
/// The relay client, talked to by a server that answers on loopback.
///
/// <para>A fake at the socket rather than behind an interface: the whole of this
/// class is the conversation, so a test that stubbed the conversation out would
/// only be testing itself. What it cannot cover is TLS — a handshake needs a
/// certificate this has no business generating — so the two TLS decisions that
/// matter are tested by what the client refuses to do, which is the half that has
/// consequences.</para>
/// </summary>
public class SmtpRelayTests
{
    private static SmtpSettings Settings(FakeSmtpServer server, string security = SmtpSecurity.None) => new()
    {
        Enabled = true,
        Host = "127.0.0.1",
        Port = server.Port,
        Security = security,
        FromAddress = "alerts@example.com",
        FromName = "pinqops",
    };

    private static EmailEnvelope Envelope(string subject = "Disk is full", string body = "host cpu 95%") =>
        new("alerts@example.com", "pinqops", ["ops@example.com"], subject, body);

    [Fact]
    public async Task AMessageGoesThroughAndTheConversationIsTheOneSmtpExpects()
    {
        using var server = FakeSmtpServer.Start();

        var failure = await new SmtpRelay().SendAsync(Settings(server), password: null, Envelope());

        Assert.Null(failure);
        var conversation = await server.Conversation;
        Assert.Contains(conversation, line => line.StartsWith("EHLO ", StringComparison.Ordinal));
        Assert.Contains("MAIL FROM:<alerts@example.com>", conversation);
        Assert.Contains("RCPT TO:<ops@example.com>", conversation);
        Assert.Contains("DATA", conversation);
        Assert.Contains("QUIT", conversation);
    }

    [Fact]
    public async Task TheMessageTheRelayReceivesCarriesTheSubjectAndTheSender()
    {
        using var server = FakeSmtpServer.Start();

        await new SmtpRelay().SendAsync(Settings(server), password: null, Envelope());

        var message = string.Join("\r\n", await server.Conversation);
        Assert.Contains("Subject: Disk is full", message, StringComparison.Ordinal);
        Assert.Contains("From: pinqops <alerts@example.com>", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The relay is told which name to use rather than being left to guess from the
    /// machine's hostname, which on a container is a random hex string.
    /// </summary>
    [Fact]
    public async Task TheGreetingUsesTheSendersDomainByDefault()
    {
        using var server = FakeSmtpServer.Start();

        await new SmtpRelay().SendAsync(Settings(server), password: null, Envelope());

        Assert.Contains("EHLO example.com", await server.Conversation);
    }

    [Fact]
    public async Task ARefusedRecipientIsReportedInTheRelaysOwnWords()
    {
        using var server = FakeSmtpServer.Start(refuseRecipient: true);

        var failure = await new SmtpRelay().SendAsync(Settings(server), password: null, Envelope());

        Assert.NotNull(failure);
        Assert.Contains("relaying denied", failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARefusedMessageIsReported()
    {
        using var server = FakeSmtpServer.Start(refuseData: true);

        var failure = await new SmtpRelay().SendAsync(Settings(server), password: null, Envelope());

        Assert.NotNull(failure);
        Assert.Contains("message", failure, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the two refusals that matter -----------------------------------------

    /// <summary>
    /// The whole point of choosing STARTTLS. Carrying on unencrypted because the
    /// server did not offer it is a downgrade nobody would ever be told about.
    /// </summary>
    [Fact]
    public async Task StartTlsAgainstAServerThatDoesNotOfferItStopsRatherThanContinuingInTheClear()
    {
        using var server = FakeSmtpServer.Start();

        var failure = await new SmtpRelay()
            .SendAsync(Settings(server, SmtpSecurity.StartTls), password: null, Envelope());

        Assert.NotNull(failure);
        Assert.Contains("STARTTLS", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("MAIL FROM:<alerts@example.com>", await server.Conversation);
    }

    /// <summary>
    /// And the other half: a password is never put on an unencrypted wire by
    /// accident. The check is before the command, so the credential is not sent and
    /// then regretted.
    /// </summary>
    [Fact]
    public async Task APasswordIsNotSentOverAnUnencryptedConnection()
    {
        using var server = FakeSmtpServer.Start();
        var settings = Settings(server);
        settings.Username = "apikey";
        settings.SecretName = "SMTP_PASSWORD";

        var failure = await new SmtpRelay().SendAsync(settings, "hunter2", Envelope());

        Assert.NotNull(failure);
        Assert.Contains("unencrypted", failure, StringComparison.OrdinalIgnoreCase);
        var conversation = string.Join("\n", await server.Conversation);
        Assert.DoesNotContain("hunter2", conversation, StringComparison.Ordinal);
        Assert.DoesNotContain("AUTH", conversation, StringComparison.Ordinal);
    }

    /// <summary>
    /// The deliberate opt-out, for a relay on this machine. It authenticates,
    /// which is the point — the setting exists so that choice is made once and in
    /// the open.
    /// </summary>
    [Fact]
    public async Task ItAuthenticatesInTheClearOnlyWhenThatWasChosen()
    {
        using var server = FakeSmtpServer.Start(offerAuth: true);
        var settings = Settings(server);
        settings.Username = "apikey";
        settings.SecretName = "SMTP_PASSWORD";
        settings.AllowInsecureAuth = true;

        var failure = await new SmtpRelay().SendAsync(settings, "hunter2", Envelope());

        Assert.Null(failure);
        var authLine = Assert.Single(
            await server.Conversation,
            line => line.StartsWith("AUTH PLAIN ", StringComparison.Ordinal));
        Assert.Equal(
            "\0apikey\0hunter2",
            Encoding.UTF8.GetString(Convert.FromBase64String(authLine["AUTH PLAIN ".Length..])));
    }

    [Fact]
    public async Task ARejectedSignInIsReported()
    {
        using var server = FakeSmtpServer.Start(offerAuth: true, refuseAuth: true);
        var settings = Settings(server);
        settings.Username = "apikey";
        settings.SecretName = "SMTP_PASSWORD";
        settings.AllowInsecureAuth = true;

        var failure = await new SmtpRelay().SendAsync(settings, "wrong", Envelope());

        Assert.NotNull(failure);
        Assert.Contains("sign-in", failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SettingsThatCouldNotSendAreRefusedBeforeAnythingIsDialled()
    {
        var settings = new SmtpSettings { Enabled = true, Host = string.Empty };

        Assert.NotNull(await new SmtpRelay().SendAsync(settings, password: null, Envelope()));
    }

    // ---- dot-stuffing ---------------------------------------------------------

    /// <summary>
    /// A line reading <c>.</c> ends the message. Nothing this composes can produce
    /// one — the body is base64 — but that is a property of the composer, and the
    /// transport keeps its own.
    /// </summary>
    [Theory]
    [InlineData("a\r\n.\r\nb", "a\r\n..\r\nb")]
    [InlineData(".leading", "..leading")]
    [InlineData("nothing to do", "nothing to do")]
    public void ALeadingDotIsDoubled(string message, string expected) =>
        Assert.Equal(expected, SmtpRelay.DotStuff(message));
}

/// <summary>
/// An SMTP server that says yes. It records every line it was sent, which is what
/// the tests assert against — including the ones asserting that a line was never
/// sent at all.
/// </summary>
internal sealed class FakeSmtpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly TaskCompletionSource<IReadOnlyList<string>> _conversation = new();

    private FakeSmtpServer(TcpListener listener, bool offerAuth, bool refuseAuth, bool refuseRecipient, bool refuseData)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(() => ServeAsync(offerAuth, refuseAuth, refuseRecipient, refuseData));
    }

    internal int Port { get; }

    /// <summary>
    /// Every line the client sent, once the connection is over. Bounded, so a
    /// client that never hangs up fails the test rather than hanging the suite.
    /// </summary>
    internal Task<IReadOnlyList<string>> Conversation => _conversation.Task.WaitAsync(TimeSpan.FromSeconds(15));

    internal static FakeSmtpServer Start(
        bool offerAuth = false, bool refuseAuth = false, bool refuseRecipient = false, bool refuseData = false)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new FakeSmtpServer(listener, offerAuth, refuseAuth, refuseRecipient, refuseData);
    }

    private async Task ServeAsync(bool offerAuth, bool refuseAuth, bool refuseRecipient, bool refuseData)
    {
        var lines = new List<string>();
        try
        {
            using var client = await _listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            // No byte-order mark: SMTP starts at the greeting's first digit, and a
            // BOM in front of it is not a reply.
            var wire = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            using var reader = new StreamReader(stream, wire);
            await using var writer = new StreamWriter(stream, wire) { AutoFlush = true, NewLine = "\r\n" };

            await writer.WriteLineAsync("220 fake.example.com ESMTP");

            var inData = false;
            while (await reader.ReadLineAsync() is { } line)
            {
                lines.Add(line);

                if (inData)
                {
                    if (line == ".")
                    {
                        inData = false;
                        await writer.WriteLineAsync(refuseData ? "554 5.6.0 message rejected" : "250 2.0.0 Ok");
                    }

                    continue;
                }

                if (line.StartsWith("EHLO", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync("250-fake.example.com Hello");
                    if (offerAuth)
                    {
                        await writer.WriteLineAsync("250-AUTH PLAIN LOGIN");
                    }

                    await writer.WriteLineAsync("250 8BITMIME");
                }
                else if (line.StartsWith("AUTH", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync(refuseAuth ? "535 5.7.8 authentication failed" : "235 2.7.0 Accepted");
                }
                else if (line.StartsWith("MAIL FROM", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync("250 2.1.0 Ok");
                }
                else if (line.StartsWith("RCPT TO", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync(refuseRecipient ? "550 5.7.1 relaying denied" : "250 2.1.5 Ok");
                }
                else if (line == "DATA")
                {
                    inData = true;
                    await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                }
                else if (line == "QUIT")
                {
                    await writer.WriteLineAsync("221 2.0.0 Bye");
                    break;
                }
                else
                {
                    await writer.WriteLineAsync("502 5.5.2 not implemented");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            // The client hung up, which several of these tests are about.
        }
        catch (Exception exception)
        {
            // Anything else is a bug in this fake, and hiding it would show up as a
            // baffling assertion failure somewhere else.
            _conversation.TrySetException(exception);
        }
        finally
        {
            _conversation.TrySetResult(lines);
        }
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Dispose();
        _conversation.TrySetResult([]);
    }
}
