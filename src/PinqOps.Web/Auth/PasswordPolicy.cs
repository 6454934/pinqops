namespace PinqOps.Web;

/// <summary>
/// The rule for a new dashboard password, in one place so the four routes that
/// set one cannot drift apart.
///
/// Length does the work here rather than a composition rule ("one upper, one
/// digit, one symbol"), which mostly produces <c>Password1!</c> and is weaker
/// than a longer passphrase. The deny-list only catches the handful of guesses
/// that any credential-stuffing run opens with — it is not a breach corpus, and
/// pretending otherwise would be worse than saying so. Existing passwords are
/// unaffected until they are changed; the stored hashes keep verifying.
/// </summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 12;

    private static readonly HashSet<string> Rejected = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "password1", "password123", "passw0rd", "p@ssword", "p@ssw0rd",
        "administrator", "changeme", "letmein", "welcome", "qwertyuiop", "qwerty123",
        "123456789012", "1234567890", "iloveyou", "monkey123", "dragon123",
        "pinqops", "pinqops123", "dockerdocker", "portainer", "admin1234",
    };

    /// <summary>Null when the password is acceptable, otherwise why it is not.</summary>
    public static string? Validate(string? password)
    {
        if (password is null || password.Length < MinimumLength)
        {
            return $"Choose a password of at least {MinimumLength} characters — length matters more than symbols.";
        }

        if (Rejected.Contains(password))
        {
            return "That password is one of the first things an attacker tries. Choose another.";
        }

        // "aaaaaaaaaaaa" clears a length check while carrying almost no entropy.
        if (password.Distinct().Count() < 5)
        {
            return "That password repeats too few distinct characters. Choose another.";
        }

        return null;
    }
}
