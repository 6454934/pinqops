using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace PinqOps.Web;

/// <summary>
/// One Docker host the dashboard manages: the machine it runs on, or a remote
/// one reached over SSH.
///
/// The dashboard used to drive only its own daemon, so "which host" was never a
/// question. Now that it can hold credentials for several, an environment is the
/// unit that access and capability decisions attach to — a compromised dashboard
/// reaches every environment it has keys for, so those keys are encrypted at rest
/// and each environment can be pinned read-only independently of who is asking.
/// </summary>
public sealed class ManagedEnvironment
{
    /// <summary>The always-present environment for the daemon on this machine.</summary>
    public const string LocalId = "local";

    public const string TransportLocal = "local";
    public const string TransportSsh = "ssh";

    /// <summary>
    /// Ids become SSH config host aliases and appear in file paths, so the
    /// character set is deliberately narrow.
    /// </summary>
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.Compiled);

    public required string Id { get; set; }

    public required string Name { get; set; }

    /// <summary><see cref="TransportLocal"/> or <see cref="TransportSsh"/>.</summary>
    public string Transport { get; set; } = TransportLocal;

    // ---- SSH transport ----------------------------------------------------

    public string? Host { get; set; }

    public string? User { get; set; }

    public int Port { get; set; } = 22;

    /// <summary>
    /// The private key, encrypted by <see cref="PinqOps.SecretBox"/>. Written to
    /// a 0600 file for SSH to read; never returned by the API.
    /// </summary>
    public string? PrivateKey { get; set; }

    /// <summary>
    /// The host's public key in <c>known_hosts</c> form, pinned on first connect.
    /// Without it SSH would have to accept any key, which makes the first
    /// connection — and every one after a network compromise — trivially
    /// interceptable.
    /// </summary>
    public string? HostKey { get; set; }

    // ---- Capabilities -----------------------------------------------------

    /// <summary>
    /// Refuse every mutation against this environment, whoever is asking. A role
    /// says what a person may do; this says what an environment permits, which is
    /// the control you want on production when the people who administer the
    /// dashboard are not the people who should be restarting containers there.
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>True when this is the daemon on the machine the dashboard runs on.</summary>
    public bool IsLocal => string.Equals(Transport, TransportLocal, StringComparison.OrdinalIgnoreCase);

    public static bool IsValidId(string? id) => id is not null && IdPattern.IsMatch(id);

    /// <summary>
    /// The environment that always exists, for this machine's own daemon. The
    /// dashboard shows it as a translated "Default" rather than this name — it
    /// is the one nobody chose, so it should not read like a name anyone typed.
    /// </summary>
    public static ManagedEnvironment Local() => new()
    {
        Id = LocalId,
        Name = "Default",
        Transport = TransportLocal,
    };

    /// <summary>
    /// Throws when the environment could not be connected to as described. Called
    /// before anything is stored, so a half-specified SSH host cannot be saved and
    /// then fail confusingly at every request.
    /// </summary>
    public void Validate()
    {
        if (!IsValidId(Id))
        {
            throw new ArgumentException("Environment id must be 1-32 characters of a-z, 0-9 or '-', starting alphanumeric.");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("Environment name is required.");
        }

        if (IsLocal)
        {
            return;
        }

        if (!string.Equals(Transport, TransportSsh, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Unsupported transport '{Transport}'.");
        }

        if (string.IsNullOrWhiteSpace(Host) || !SshTarget.IsValidHost(Host))
        {
            throw new ArgumentException("A valid SSH host (name or IP address) is required.");
        }

        if (string.IsNullOrWhiteSpace(User) || !SshTarget.IsValidUser(User))
        {
            throw new ArgumentException("A valid SSH user is required.");
        }

        if (Port is < 1 or > 65535)
        {
            throw new ArgumentException("SSH port must be between 1 and 65535.");
        }
    }
}

/// <summary>
/// Validation for the parts of an SSH target that get written into a config file
/// and a docker argument. Kept separate so the rules can be tested on their own
/// and reused by the config generator, which re-checks at the last boundary.
/// </summary>
public static class SshTarget
{
    private static readonly Regex HostPattern = new(@"^[A-Za-z0-9]([A-Za-z0-9._-]{0,252}[A-Za-z0-9])?$", RegexOptions.Compiled);
    private static readonly Regex UserPattern = new("^[a-z_][a-z0-9_-]{0,31}$", RegexOptions.Compiled);

    /// <summary>
    /// A hostname or IP literal — no whitespace, no newlines, no config syntax.
    ///
    /// IPv6 goes through a second predicate because the hostname pattern cannot
    /// admit ':' without admitting it in a hostname too. Without it an IPv6-only
    /// server could not be registered at all, and the message it was refused with
    /// — "a valid SSH host (name or IP address) is required" — named the very
    /// thing the operator had just typed.
    /// </summary>
    public static bool IsValidHost(string? host) =>
        host is not null && (HostPattern.IsMatch(host) || IsIpv6Literal(host));

    /// <summary>
    /// An IPv6 address written in plain hex-and-colon form.
    ///
    /// The character set is checked as well as the parse, and deliberately admits
    /// nothing else: the value is written into <c>ssh_config</c> as a
    /// <c>HostName</c>, where a scope suffix (<c>fe80::1%eth0</c>) would be read
    /// as an unknown <c>%</c> token and break the whole generated block — not just
    /// this one host. That also rules out whitespace, newlines and config syntax
    /// without a second pass.
    /// </summary>
    private static bool IsIpv6Literal(string host) =>
        host.Length > 0
        && host.All(character => char.IsAsciiHexDigit(character) || character == ':')
        && IPAddress.TryParse(host, out var address)
        && address.AddressFamily == AddressFamily.InterNetworkV6;

    /// <summary>A POSIX-ish account name.</summary>
    public static bool IsValidUser(string? user) => user is not null && UserPattern.IsMatch(user);
}
