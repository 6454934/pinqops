using System.Security.Cryptography;

namespace PinqOps.Web;

/// <summary>
/// PBKDF2-SHA256 password hashing for the dashboard login. The stored format is
/// "iterations.salt.hash"; hashes without an iteration prefix are legacy 100k
/// hashes and are transparently upgraded on the next successful login.
/// </summary>
public static class PasswordHasher
{
    private const int Iterations = 600_000; // OWASP 2023+ guidance for PBKDF2-SHA256
    private const int LegacyIterations = 100_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');
        int iterations;
        string saltPart, hashPart;
        switch (parts.Length)
        {
            case 3 when int.TryParse(parts[0], out iterations) && iterations > 0:
                (saltPart, hashPart) = (parts[1], parts[2]);
                break;
            case 2:
                (iterations, saltPart, hashPart) = (LegacyIterations, parts[0], parts[1]);
                break;
            default:
                return false;
        }

        try
        {
            var salt = Convert.FromBase64String(saltPart);
            var expected = Convert.FromBase64String(hashPart);

            // Reject a truncated record instead of deriving to its length. An
            // empty digest segment decodes to an empty array, Pbkdf2 with
            // outputLength 0 returns another empty array, and FixedTimeEquals of
            // two empty arrays is true — so a hash whose digest was lost (a
            // truncated write, a hand-edited config) would accept every password.
            if (expected.Length != HashSize || salt.Length == 0)
            {
                return false;
            }

            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashSize);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>True when the stored hash predates the current work factor.</summary>
    public static bool NeedsRehash(string stored) =>
        !stored.StartsWith($"{Iterations}.", StringComparison.Ordinal);

    // A hash of a throwaway secret at the current work factor. Verifying against
    // it lets the login path spend the same PBKDF2 time on a username that does
    // not exist as on one that does.
    private static readonly string DummyHash = Hash("pinqops-timing-equalizer");

    /// <summary>
    /// Runs one verification against a fixed dummy hash and discards the result.
    /// Call this on the account-not-found branch of a login so response timing
    /// costs the same whether or not the username exists — otherwise the PBKDF2
    /// work only happens for real accounts and leaks which usernames are valid.
    /// </summary>
    public static void SpendVerificationTime() => Verify(string.Empty, DummyHash);
}
