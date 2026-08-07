using System.Text;
using PinqOps.Mail;
using Xunit;

namespace PinqOps.Tests.Mail;

public class EmailAddressTests
{
    [Theory]
    [InlineData("alerts@example.com")]
    [InlineData("first.last+tag@mail.example.co.uk")]
    [InlineData("root@localhost")]
    [InlineData("a@b")]
    public void AnOrdinaryAddressIsAccepted(string address) => Assert.True(EmailAddress.IsValid(address));

    [Theory]
    [InlineData("")]
    [InlineData("no-at-sign")]
    [InlineData("two@at@signs.com")]
    [InlineData("@example.com")]
    [InlineData("user@")]
    [InlineData(".leading@example.com")]
    [InlineData("trailing.@example.com")]
    [InlineData("double..dot@example.com")]
    [InlineData("user@-example.com")]
    [InlineData("user@example..com")]
    public void SomethingThatIsNotAnAddressIsRefused(string address) => Assert.False(EmailAddress.IsValid(address));

    /// <summary>
    /// The address is written into a <c>RCPT TO:</c> command and a <c>To:</c>
    /// header. A newline in it is a second command or a second header, so the
    /// answer is no rather than an escape.
    /// </summary>
    [Theory]
    [InlineData("user@example.com\r\nRCPT TO:<victim@example.com>")]
    [InlineData("user@example.com\nBcc: victim@example.com")]
    [InlineData("user@example.com\r\n")]
    [InlineData("\"quoted\"@example.com")]
    [InlineData("display <user@example.com>")]
    [InlineData("user@example.com, other@example.com")]
    public void AnythingCarryingAControlCharacterOrExtraSyntaxIsRefused(string address) =>
        Assert.False(EmailAddress.IsValid(address));

    [Fact]
    public void AListIsSplitOnCommasAndSemicolons() =>
        Assert.Equal(
            ["a@example.com", "b@example.com", "c@example.com"],
            EmailAddress.ParseList("a@example.com, b@example.com; c@example.com"));

    [Fact]
    public void BlankEntriesInAListAreDropped() =>
        Assert.Equal(["a@example.com"], EmailAddress.ParseList(" a@example.com , , "));

    /// <summary>
    /// A recipient list that silently loses an address is a message somebody never
    /// got and nobody was told about.
    /// </summary>
    [Fact]
    public void OneBadEntryRejectsTheWholeList() =>
        Assert.Throws<ArgumentException>(() => EmailAddress.ParseList("a@example.com, not-an-address"));

    [Fact]
    public void ThereIsACapOnRecipients()
    {
        var many = string.Join(",", Enumerable.Range(0, EmailAddress.MaximumRecipients + 1).Select(i => $"a{i}@example.com"));

        Assert.Throws<ArgumentException>(() => EmailAddress.ParseList(many));
    }

    [Fact]
    public void TheDomainIsWhatFollowsTheAt() =>
        Assert.Equal("example.com", EmailAddress.DomainOf(" alerts@example.com "));
}

public class MailComposerTests
{
    private static readonly DateTimeOffset At = new(2026, 8, 2, 9, 5, 0, TimeSpan.FromHours(3));

    private static EmailEnvelope Envelope(
        string subject = "Subject", string body = "Body", string fromName = "pinqops") =>
        new("alerts@example.com", fromName, ["ops@example.com"], subject, body);

    private static string Render(EmailEnvelope envelope) =>
        MailComposer.Render(envelope, "abc@example.com", At);

    [Fact]
    public void TheHeadersAreThere()
    {
        var message = Render(Envelope());

        Assert.Contains("From: pinqops <alerts@example.com>", message, StringComparison.Ordinal);
        Assert.Contains("To: ops@example.com", message, StringComparison.Ordinal);
        Assert.Contains("Subject: Subject", message, StringComparison.Ordinal);
        Assert.Contains("Message-ID: <abc@example.com>", message, StringComparison.Ordinal);
        Assert.Contains("Content-Transfer-Encoding: base64", message, StringComparison.Ordinal);
    }

    /// <summary>A wire format uses CRLF, whatever the machine composing it uses.</summary>
    [Fact]
    public void EveryLineEndsWithCarriageReturnLineFeed()
    {
        var message = Render(Envelope());

        Assert.DoesNotContain("\n", message.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    /// <summary>
    /// RFC 5322's own format. "R" would print GMT and throw the real offset away,
    /// which puts a message three hours in the past for anyone reading the header.
    /// </summary>
    [Fact]
    public void TheDateCarriesTheRealOffset() =>
        Assert.Contains("Date: Sun, 2 Aug 2026 09:05:00 +0300", Render(Envelope()), StringComparison.Ordinal);

    [Fact]
    public void TheBodyIsBase64()
    {
        var message = Render(Envelope(body: "hello"));
        var body = message[(message.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..].Trim();

        Assert.Equal("hello", Encoding.UTF8.GetString(Convert.FromBase64String(body)));
    }

    /// <summary>
    /// Base64 is what makes a long line, a leading dot and a byte above 127 all
    /// impossible at once — each of which is a corruption that only shows up in
    /// somebody else's inbox.
    /// </summary>
    [Fact]
    public void ALongBodyIsWrapped()
    {
        var message = Render(Envelope(body: new string('x', 5000)));

        Assert.All(
            message.Split("\r\n", StringSplitOptions.RemoveEmptyEntries),
            line => Assert.True(line.Length <= MailComposer.Base64LineLength, line));
    }

    [Fact]
    public void AnAsciiSubjectIsLeftReadable() =>
        Assert.Equal("Disk is full", MailComposer.EncodeHeader("Disk is full"));

    /// <summary>Without this a Turkish subject line arrives as question marks.</summary>
    [Fact]
    public void ANonAsciiSubjectIsEncoded()
    {
        var encoded = MailComposer.EncodeHeader("Disk doldu — sunucu çöküyor");

        Assert.StartsWith("=?UTF-8?B?", encoded, StringComparison.Ordinal);
        Assert.EndsWith("?=", encoded, StringComparison.Ordinal);
        Assert.Equal(
            "Disk doldu — sunucu çöküyor",
            Encoding.UTF8.GetString(Convert.FromBase64String(encoded[10..^2])));
    }

    /// <summary>
    /// A newline in a subject is how a second header — another recipient, a
    /// different From — is smuggled in. There is no reading of one that was meant.
    /// </summary>
    [Theory]
    [InlineData("Subject\r\nBcc: victim@example.com")]
    [InlineData("Subject\nBcc: victim@example.com")]
    [InlineData("Subject\tinjected")]
    public void ASubjectCarryingALineBreakIsRefused(string subject) =>
        Assert.Throws<ArgumentException>(() => Render(Envelope(subject: subject)));

    [Fact]
    public void ASenderNameCarryingALineBreakIsRefused() =>
        Assert.Throws<ArgumentException>(() => Render(Envelope(fromName: "pinqops\r\nBcc: victim@example.com")));

    [Fact]
    public void AMessageWithNoRecipientsIsRefused() =>
        Assert.Throws<ArgumentException>(() =>
            MailComposer.Render(new EmailEnvelope("a@example.com", string.Empty, [], "s", "b"), "id@x", At));

    [Fact]
    public void ABodyPastTheCapIsRefused() =>
        Assert.Throws<ArgumentException>(() =>
            Render(Envelope(body: new string('x', MailComposer.MaximumBodyLength + 1))));

    [Fact]
    public void ABareAddressIsSentWhenThereIsNoDisplayName() =>
        Assert.Contains("From: alerts@example.com\r\n", Render(Envelope(fromName: "")), StringComparison.Ordinal);

    [Fact]
    public void EveryMessageGetsItsOwnIdentifier() =>
        Assert.NotEqual(MailComposer.NewMessageId("example.com"), MailComposer.NewMessageId("example.com"));
}

public class SmtpSettingsValidatorTests
{
    private static SmtpSettings Settings() => new()
    {
        Enabled = true,
        Host = "smtp.example.com",
        Port = 587,
        Security = SmtpSecurity.StartTls,
        FromAddress = "alerts@example.com",
    };

    [Fact]
    public void CompleteSettingsAreAccepted() => Assert.Null(SmtpSettingsValidator.Validate(Settings()));

    [Theory]
    [InlineData(SmtpSecurity.StartTls, 587)]
    [InlineData(SmtpSecurity.Tls, 465)]
    [InlineData(SmtpSecurity.None, 25)]
    public void EachModeHasThePortItIsNormallyReachedOn(string security, int port) =>
        Assert.Equal(port, SmtpSecurity.DefaultPort(security));

    [Fact]
    public void AHostIsRequired()
    {
        var settings = Settings();
        settings.Host = string.Empty;

        Assert.NotNull(SmtpSettingsValidator.Validate(settings));
    }

    /// <summary>A scheme here produces a DNS error that says nothing about what was typed.</summary>
    [Theory]
    [InlineData("smtps://smtp.example.com")]
    [InlineData("smtp.example.com/submit")]
    [InlineData("smtp example com")]
    public void AHostThatIsNotAHostnameIsRefused(string host)
    {
        var settings = Settings();
        settings.Host = host;

        Assert.NotNull(SmtpSettingsValidator.Validate(settings));
    }

    [Fact]
    public void TheSenderHasToBeAnAddress()
    {
        var settings = Settings();
        settings.FromAddress = "pinqops";

        Assert.NotNull(SmtpSettingsValidator.Validate(settings));
    }

    [Fact]
    public void AUsernameNeedsAVaultEntryToGoWithIt()
    {
        var settings = Settings();
        settings.Username = "apikey";

        Assert.NotNull(SmtpSettingsValidator.Validate(settings));

        settings.SecretName = "SMTP_PASSWORD";
        Assert.Null(SmtpSettingsValidator.Validate(settings));
    }

    /// <summary>
    /// A password with nothing to authenticate as is a setting that reads as
    /// configured and sends nothing; the relay is never told about it either.
    /// </summary>
    [Fact]
    public void AVaultEntryWithNoUsernameIsRefused()
    {
        var settings = Settings();
        settings.SecretName = "SMTP_PASSWORD";

        Assert.NotNull(SmtpSettingsValidator.Validate(settings));
    }

    /// <summary>
    /// The greeting name is interpolated straight into <c>EHLO &lt;name&gt;</c>, so a
    /// carriage return inside it ends that line and makes whatever follows a second
    /// SMTP command in the same session — a relay setting that turns into arbitrary
    /// SMTP verbs. Its two neighbours on the same form, the sender name and the
    /// username, are already refused for exactly this.
    ///
    /// <para>Refused rather than stripped: a relay that greets itself as something
    /// other than what was typed is a setting that quietly does not mean what it
    /// says, and the operator would find out from the relay's logs.</para>
    /// </summary>
    [Theory]
    [InlineData("pinqops\rMAIL FROM:<victim@example.com>")]
    [InlineData("pinqops\nMAIL FROM:<victim@example.com>")]
    [InlineData("pinqops\r\nMAIL FROM:<victim@example.com>")]
    [InlineData("pinqops\0host")]
    public void AGreetingNameThatCouldStartASecondCommandIsRefused(string ehloName)
    {
        var settings = Settings();
        settings.EhloName = ehloName;

        Assert.NotNull(SmtpSettingsValidator.Validate(settings));
    }

    [Fact]
    public void AnOrdinaryGreetingNameIsAccepted()
    {
        var settings = Settings();
        settings.EhloName = "mail.example.com";

        Assert.Null(SmtpSettingsValidator.Validate(settings));
    }

    [Fact]
    public void AuthenticatingInTheClearIsOffUntilItIsChosen() => Assert.False(new SmtpSettings().AllowInsecureAuth);
}

public class MailDnsRecordTests
{
    private static IReadOnlyList<MailDnsRecord> Records(
        string? mailHost = "mail.example.com", string? include = null, string? reportTo = null) =>
        MailDnsRecords.For("example.com", mailHost, include, dkimSelector: null, reportTo);

    [Fact]
    public void ThereIsAnMxWhenAHostWasGiven() =>
        Assert.Equal("10 mail.example.com.", Records().Single(r => r.Type == "MX").Value);

    /// <summary>Mail that only ever goes out through a provider needs no MX of its own.</summary>
    [Fact]
    public void ThereIsNoMxWhenThereIsNoMailHost() =>
        Assert.DoesNotContain(Records(mailHost: null), record => record.Type == "MX");

    [Fact]
    public void SpfAuthorisesTheProviderWhenThereIsOne() =>
        Assert.Equal(
            "v=spf1 include:spf.mailgun.org ~all",
            Records(include: "spf.mailgun.org").First(r => r.Name == "example.com" && r.Type == "TXT").Value);

    [Fact]
    public void SpfAuthorisesTheDomainsOwnMailHostOtherwise() =>
        Assert.Equal("v=spf1 mx ~all", Records().First(r => r.Name == "example.com" && r.Type == "TXT").Value);

    /// <summary>
    /// A hard fail on a record that is not yet complete bounces real mail, which is
    /// a worse outcome than the spam it prevents.
    /// </summary>
    [Fact]
    public void SpfIsASoftFail() =>
        Assert.All(
            Records().Where(record => record.Value.StartsWith("v=spf1", StringComparison.Ordinal)),
            record => Assert.EndsWith("~all", record.Value, StringComparison.Ordinal));

    /// <summary>
    /// The key is generated by whatever signs the mail, so the record is offered as
    /// a shape with the command that produces it — and marked, so it is never
    /// pasted into a zone as-is.
    /// </summary>
    [Fact]
    public void TheDkimRecordIsAPlaceholderAndSaysSo()
    {
        var dkim = Records().Single(record => record.Name.Contains("_domainkey", StringComparison.Ordinal));

        Assert.Equal($"{MailDnsRecords.DefaultDkimSelector}._domainkey.example.com", dkim.Name);
        Assert.True(dkim.Placeholder);
        Assert.Contains("setup config dkim", dkim.Purpose, StringComparison.Ordinal);
    }

    /// <summary>
    /// p=reject published before the reports show SPF and DKIM passing does not
    /// fail loudly — it silently destroys delivery of mail that was working.
    /// </summary>
    [Fact]
    public void DmarcStartsAtNone()
    {
        var dmarc = Records().Single(record => record.Name.StartsWith("_dmarc", StringComparison.Ordinal));

        Assert.Contains("p=none", dmarc.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("p=reject", dmarc.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void DmarcAsksForReportsWhenThereIsSomewhereToSendThem() =>
        Assert.Contains(
            "rua=mailto:postmaster@example.com",
            Records(reportTo: "postmaster@example.com").Single(r => r.Name.StartsWith("_dmarc", StringComparison.Ordinal)).Value,
            StringComparison.Ordinal);

    [Fact]
    public void SomethingThatIsNotAnAddressIsNotWrittenIntoTheReportField() =>
        Assert.DoesNotContain(
            "rua=",
            Records(reportTo: "not-an-address").Single(r => r.Name.StartsWith("_dmarc", StringComparison.Ordinal)).Value,
            StringComparison.Ordinal);

    [Theory]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("not a domain")]
    [InlineData("-bad.example.com")]
    public void SomethingThatIsNotADomainIsRefused(string domain) =>
        Assert.Throws<ArgumentException>(() => MailDnsRecords.For(domain));

    [Fact]
    public void ADomainIsLowercasedAndTheTrailingDotDropped() =>
        Assert.Equal("example.com", MailDnsRecords.For("Example.COM.")[0].Name);
}
