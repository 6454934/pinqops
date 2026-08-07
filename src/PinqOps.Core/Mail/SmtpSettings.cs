using System.Text.Json;

namespace PinqOps.Mail;

/// <summary>How the connection to the relay is protected.</summary>
public static class SmtpSecurity
{
    /// <summary>Plain connection, upgraded with <c>STARTTLS</c> before anything else. Port 587.</summary>
    public const string StartTls = "starttls";

    /// <summary>TLS from the first byte. Port 465.</summary>
    public const string Tls = "tls";

    /// <summary>
    /// No encryption at all. Only sensible for a relay on this machine or on the
    /// docker network beside it, which is why credentials over it need a separate,
    /// explicit opt-in.
    /// </summary>
    public const string None = "none";

    public static readonly string[] All = [StartTls, Tls, None];

    public static bool IsKnown(string? security) => Array.IndexOf(All, security) >= 0;

    /// <summary>The port this mode is normally reached on.</summary>
    public static int DefaultPort(string? security) => security switch
    {
        Tls => 465,
        None => 25,
        _ => 587,
    };
}

/// <summary>
/// How pinqops sends its own mail: alerts, and later the invitations that
/// depend on this.
///
/// <para><b>A relay, not a mail server.</b> Nothing here receives mail, queues it,
/// retries it or speaks for a domain. It hands a message to something that does —
/// a provider, or a mail server the operator runs — and reports what that
/// something said. The dashboard says so on the page, because "email settings"
/// reads like a mail server to everyone who has not read this.</para>
///
/// <para><b>The password is not here.</b> It lives in the secret vault under
/// <see cref="SecretName"/>, the same split the registry credentials use, and for
/// the same reason: this file is read to render a form.</para>
/// </summary>
public sealed class SmtpSettings
{
    private string _host = string.Empty;
    private string _security = SmtpSecurity.StartTls;
    private string _username = string.Empty;
    private string _secretName = string.Empty;
    private string _fromAddress = string.Empty;
    private string _fromName = "pinqops";

    public bool Enabled { get; set; }

    public string Host { get => _host; set => _host = value ?? string.Empty; }

    public int Port { get; set; } = SmtpSecurity.DefaultPort(SmtpSecurity.StartTls);

    /// <summary>One of <see cref="SmtpSecurity"/>.</summary>
    public string Security { get => _security; set => _security = value ?? string.Empty; }

    /// <summary>Empty sends without authenticating, which some internal relays want.</summary>
    public string Username { get => _username; set => _username = value ?? string.Empty; }

    /// <summary>The vault entry holding the password. Empty when there is no username.</summary>
    public string SecretName { get => _secretName; set => _secretName = value ?? string.Empty; }

    public string FromAddress { get => _fromAddress; set => _fromAddress = value ?? string.Empty; }

    public string FromName { get => _fromName; set => _fromName = value ?? string.Empty; }

    /// <summary>
    /// Allows the password to be sent over an unencrypted connection.
    ///
    /// <para>Off, and it stays off unless somebody deliberately turns it on. A
    /// relay that offers no TLS is either on this machine — where there is no
    /// network to listen on — or is one that should not be given a password at
    /// all. Sending it anyway to "make it work" is how a credential ends up on the
    /// wire, and the failure it prevents is silent.</para>
    /// </summary>
    public bool AllowInsecureAuth { get; set; }

    /// <summary>The name this client gives in <c>EHLO</c>. Empty uses the sender's domain.</summary>
    public string EhloName { get; set; } = string.Empty;
}

/// <summary>Whether the relay settings are ones a message could actually be sent through.</summary>
public static class SmtpSettingsValidator
{
    public const int MaximumHostLength = 253;

    /// <summary>Null when the settings are usable, otherwise why they are not.</summary>
    public static string? Validate(SmtpSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var host = settings.Host.Trim();
        if (host.Length == 0)
        {
            return "A relay host is required.";
        }

        if (host.Length > MaximumHostLength || !IsHostname(host))
        {
            return $"'{settings.Host}' is not a relay host.";
        }

        if (!HostPort.IsValid(settings.Port))
        {
            return "The relay port must be between 1 and 65535.";
        }

        if (!SmtpSecurity.IsKnown(settings.Security))
        {
            return $"Unknown connection security '{settings.Security}'.";
        }

        if (!EmailAddress.IsValid(settings.FromAddress))
        {
            return "A sender address is required, and it has to be an address.";
        }

        if (settings.FromName.Length > 100 || settings.FromName.Any(char.IsControl))
        {
            return "The sender name may be at most 100 characters, on one line.";
        }

        var username = settings.Username.Trim();
        if (username.Length > 0 && !Secrets.SecretName.IsValid(settings.SecretName))
        {
            return "A vault entry holding the relay password is required.";
        }

        if (username.Length == 0 && settings.SecretName.Trim().Length > 0)
        {
            // A password with nothing to authenticate as is a setting that reads as
            // configured and sends nothing — the relay is never told either.
            return "A relay password needs a username to go with it.";
        }

        if (username.Any(char.IsControl))
        {
            return "The relay username must be on one line.";
        }

        // The greeting name is interpolated into `EHLO <name>`, so a carriage
        // return in it ends that line and makes what follows a second command in
        // the same session. Refused rather than stripped: a relay greeting itself
        // as something other than what was typed is a setting that quietly does not
        // mean what it says.
        if (settings.EhloName.Any(char.IsControl))
        {
            return "The name given in the SMTP greeting must be on one line.";
        }

        return null;
    }

    /// <summary>
    /// A hostname or a bare IP address. Not a URL: a scheme here is the mistake
    /// that produces a connection attempt to a host called "smtps" and a DNS error
    /// that says nothing about what was typed.
    /// </summary>
    private static bool IsHostname(string host) =>
        !host.StartsWith('-')
        && !host.StartsWith('.')
        && !host.EndsWith('-')
        && !host.EndsWith('.')
        && !host.Contains("..", StringComparison.Ordinal)
        && host.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-');
}

/// <summary>
/// The relay settings, in one file. Server-global: how this server sends mail is
/// a property of the server, not of any app on it.
/// </summary>
public sealed class SmtpSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly Lock _gate = new();

    public SmtpSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public string Path_ => _path;

    public SmtpSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<SmtpSettings>(SecureFile.ReadAllText(_path), SerializerOptions)
                    ?? new SmtpSettings();
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt file means "no relay configured", never a crash — the same
            // stance every other store here takes.
        }

        return new SmtpSettings();
    }

    /// <summary>
    /// Load, mutate and save under one lock. The partial update keeps the stored
    /// username when the request leaves it blank, so a racing save could otherwise
    /// write back a value it never saw.
    /// </summary>
    public T Update<T>(Func<SmtpSettings, T> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_gate)
        {
            var settings = Load();
            var result = mutate(settings);
            SecureFile.WriteAllText(_path, JsonSerializer.Serialize(settings, SerializerOptions));
            return result;
        }
    }
}
