namespace PinqOps.Web;

/// <summary>
/// The rule for a new account name, in one place so the routes that create one
/// cannot drift apart — the same reason <see cref="PasswordPolicy"/> exists. Two
/// routes take a caller-supplied name: an admin creating a user, and an invitee
/// accepting an invitation. They had a copy of this each.
/// </summary>
public static class UsernamePolicy
{
    public const int MinimumLength = 2;

    /// <summary>
    /// Names nobody may sign in as.
    ///
    /// <para><see cref="ApiTokenStore.RetiredPrincipal"/> is the principal every
    /// API token shared before each got its own. Container ownership records
    /// written then still name it, and they are safe for exactly one reason: no
    /// one can authenticate as it, so they resolve to unowned, which is admin
    /// only. The team migration relies on the same property — it deliberately
    /// leaves those rows alone rather than turning them into grants, because
    /// reinterpreting them would hand access to whoever the guess landed on.</para>
    ///
    /// <para>An account under that name makes all of that false at once, and it
    /// need not be an admin account: the invitee picks their own name, on an
    /// anonymous route, under whatever role the invitation carried.</para>
    /// </summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        ApiTokenStore.RetiredPrincipal,
    };

    /// <summary>Null when the name is acceptable, otherwise why it is not.</summary>
    public static string? Validate(string? username)
    {
        var value = (username ?? string.Empty).Trim();
        if (value.Length < MinimumLength || !value.All(IsAllowed))
        {
            return $"Username must be at least {MinimumLength} characters (letters, digits, - _ .).";
        }

        // Case-insensitively, because the duplicate-account check is: accepting a
        // different spelling would create the very account this refuses.
        if (Reserved.Contains(value))
        {
            return $"'{value}' is a reserved name and cannot be used for an account.";
        }

        return null;
    }

    /// <summary>
    /// The character set deliberately excludes ':', which is what keeps an account
    /// from ever colliding with a token principal — those are <c>token:&lt;id&gt;</c>
    /// (see <see cref="ApiTokenStore.PrincipalPrefix"/>).
    /// </summary>
    private static bool IsAllowed(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.';
}
