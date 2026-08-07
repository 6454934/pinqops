using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PinqOps.Invitations;

/// <summary>Where an invitation is in its life.</summary>
public static class InvitationStatus
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Revoked = "revoked";
    public const string Expired = "expired";
}

/// <summary>
/// An invitation to create an account on this server.
///
/// <para><b>The link is not here.</b> Only a hash of its secret half is stored, the
/// same way an API token is: this file is read to render a list, and a list of
/// working invitation links would be a list of ways in.</para>
/// </summary>
public sealed class Invitation
{
    private string _id = string.Empty;
    private string _email = string.Empty;
    private string _role = string.Empty;
    private string _teamId = string.Empty;
    private string _teamRole = string.Empty;
    private string _sha256 = string.Empty;
    private string _createdBy = string.Empty;
    private string _acceptedAs = string.Empty;

    /// <summary>Server-generated, 8 hex characters. The half of the link that is not a secret.</summary>
    public string Id { get => _id; set => _id = value ?? string.Empty; }

    public string Email { get => _email; set => _email = value ?? string.Empty; }

    /// <summary>The global role the account will have.</summary>
    public string Role { get => _role; set => _role = value ?? string.Empty; }

    /// <summary>The team it joins, or empty for none.</summary>
    public string TeamId { get => _teamId; set => _teamId = value ?? string.Empty; }

    public string TeamRole { get => _teamRole; set => _teamRole = value ?? string.Empty; }

    /// <summary>SHA-256 of the link's secret half, hex.</summary>
    public string Sha256 { get => _sha256; set => _sha256 = value ?? string.Empty; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public string CreatedBy { get => _createdBy; set => _createdBy = value ?? string.Empty; }

    public DateTimeOffset? AcceptedAt { get; set; }

    /// <summary>The username the invitee chose, once they have.</summary>
    public string AcceptedAs { get => _acceptedAs; set => _acceptedAs = value ?? string.Empty; }

    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// Where it stands. Accepted and revoked come first: an invitation that was
    /// used, or withdrawn, does not become "expired" later — what happened to it is
    /// what happened to it.
    /// </summary>
    public string StatusAt(DateTimeOffset now)
    {
        if (AcceptedAt is not null)
        {
            return InvitationStatus.Accepted;
        }

        if (RevokedAt is not null)
        {
            return InvitationStatus.Revoked;
        }

        return ExpiresAt <= now ? InvitationStatus.Expired : InvitationStatus.Pending;
    }

    public bool IsUsable(DateTimeOffset now) => StatusAt(now) == InvitationStatus.Pending;
}

/// <summary>
/// The link: an id and a secret, separated by a dot.
///
/// <para><b>The secret is random, not signed.</b> A signature over the stored
/// fields would prove the token was issued here and then still need the stored
/// record to know whether it had been used or withdrawn — so the lookup happens
/// either way, and what actually has to be unguessable is the secret. Thirty-two
/// random bytes are, and only their hash is kept, so a copied-away
/// <c>invitations.json</c> contains no working link. This is the same shape the
/// API tokens use.</para>
///
/// <para>The id travels in front so a link can be found in one lookup rather than
/// by hashing the secret against every row — which is also what keeps a revoked
/// invitation cheap to refuse.</para>
/// </summary>
public static class InvitationToken
{
    public const int SecretBytes = 32;

    /// <summary>A fresh link for an id, and the hash to store beside it.</summary>
    public static (string Token, string Sha256) New(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(SecretBytes));
        return ($"{id}.{secret}", Hash(secret));
    }

    /// <summary>Splits a link into its two halves. False when it is not one.</summary>
    public static bool TrySplit(string? token, out string id, out string secret)
    {
        id = string.Empty;
        secret = string.Empty;

        var value = (token ?? string.Empty).Trim();
        var dot = value.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot == value.Length - 1)
        {
            return false;
        }

        id = value[..dot];
        secret = value[(dot + 1)..];
        return id.All(char.IsAsciiHexDigit) && secret.All(char.IsAsciiHexDigit);
    }

    public static string Hash(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>
    /// Whether a presented secret is the one an invitation was issued with,
    /// compared in constant time so the answer cannot be found a character at a
    /// time.
    /// </summary>
    public static bool Matches(string presentedSecret, string storedSha256) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Hash(presentedSecret)),
            Encoding.ASCII.GetBytes(storedSha256 ?? string.Empty));
}

/// <summary>
/// How many invitations one person may send, and how long they last.
///
/// <para>Pure, so the rule is testable without a clock or a store: an invitation
/// endpoint is a way to make this server send mail to an address of the caller's
/// choosing, and without a cap that is a way to make it send a lot of it.</para>
/// </summary>
public static class InvitationPolicy
{
    /// <summary>How many one account may create in <see cref="RateWindow"/>.</summary>
    public const int MaximumPerWindow = 20;

    public static readonly TimeSpan RateWindow = TimeSpan.FromHours(1);

    /// <summary>Default validity. Long enough to reach somebody on holiday, short enough to matter.</summary>
    public const int DefaultValidHours = 72;

    public const int MaximumValidHours = 24 * 30;

    /// <summary>How long accepted, revoked and expired invitations are kept before being swept.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// <summary>Null when the actor may send another, otherwise why not.</summary>
    public static string? CheckRate(IEnumerable<Invitation> existing, string actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(existing);

        var recent = existing.Count(invitation =>
            string.Equals(invitation.CreatedBy, actor, StringComparison.OrdinalIgnoreCase)
            && invitation.CreatedAt > now - RateWindow);

        return recent >= MaximumPerWindow
            ? $"That is {MaximumPerWindow} invitations in an hour — wait a while before sending more."
            : null;
    }

    /// <summary>The validity to use, clamped to something sane.</summary>
    public static int ValidHours(int? requested) =>
        Math.Clamp(requested ?? DefaultValidHours, 1, MaximumValidHours);
}

/// <summary>
/// The invitations, in one file. Server-global: an invitation is to this server,
/// not to any one app on it.
/// </summary>
public sealed class InvitationStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly Lock _gate = new();

    public InvitationStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public string Path_ => _path;

    public List<Invitation> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<List<Invitation>>(SecureFile.ReadAllText(_path), SerializerOptions) ?? [];
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt file means "no invitations", never a crash — and never an
            // invitation that accepts anything.
        }

        return [];
    }

    /// <summary>
    /// Load, mutate and save under one lock. Accepting an invitation is a
    /// read-modify-write, and two acceptances of the same link arriving together
    /// would otherwise both see it unused.
    /// </summary>
    public T Update<T>(Func<List<Invitation>, T> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_gate)
        {
            var invitations = Load();
            var result = mutate(invitations);
            SecureFile.WriteAllText(_path, JsonSerializer.Serialize(invitations, SerializerOptions));
            return result;
        }
    }

    /// <summary>Drops finished invitations older than the retention window.</summary>
    public void Sweep(DateTimeOffset now) =>
        Update(invitations => invitations.RemoveAll(invitation =>
            invitation.StatusAt(now) != InvitationStatus.Pending
            && (invitation.AcceptedAt ?? invitation.RevokedAt ?? invitation.ExpiresAt) < now - InvitationPolicy.Retention));

    public static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
}
