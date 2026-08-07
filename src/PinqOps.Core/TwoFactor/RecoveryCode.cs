using System.Security.Cryptography;
using System.Text;

namespace PinqOps.TwoFactor;

/// <summary>
/// The codes that get somebody back in when the phone is gone.
///
/// <para><b>Single use, and hashed.</b> Each one is struck off the moment it
/// works, so a code read over somebody's shoulder is a code that has already been
/// spent. They are stored as hashes for the same reason passwords are: a copy of
/// the config file should not be a way in.</para>
///
/// <para><b>Fewer iterations than a password, deliberately.</b> These are 50 bits
/// of server-generated randomness, not something a person chose — there is no
/// dictionary to run against them, so the work factor is not what protects them.
/// What it has to do is stay cheap enough that checking one code against ten
/// hashes is a login rather than a pause, and 600,000 iterations ten times over is
/// several seconds of a signed-out operator wondering whether it worked.</para>
/// </summary>
public static class RecoveryCode
{
    /// <summary>
    /// How many are issued at once. Enough that losing the list of them is not the
    /// likely outcome, few enough that verifying against all of them is quick.
    /// </summary>
    public const int Count = 10;

    /// <summary>Characters per code, before the dash. Ten base32 characters is 50 bits.</summary>
    public const int Length = 10;

    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    /// <summary>A fresh set, in the form they are shown: <c>abcde-fghij</c>.</summary>
    public static IReadOnlyList<string> Generate()
    {
        var codes = new List<string>(Count);
        for (var index = 0; index < Count; index++)
        {
            // Base32 of enough bytes, cut to length: the alphabet has no lowercase
            // and no lookalike digits, which matters for something read off paper.
            var text = Base32.Encode(RandomNumberGenerator.GetBytes(8))[..Length].ToLowerInvariant();
            codes.Add(text[..5] + "-" + text[5..]);
        }

        return codes;
    }

    public static string Hash(string code)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Normalize(code), salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string code, string stored)
    {
        var parts = (stored ?? string.Empty).Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations) || iterations <= 0)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[1]);
            var expected = Convert.FromBase64String(parts[2]);

            // A truncated record must be a no, not a yes: an empty digest derives to
            // an empty array and FixedTimeEquals of two empty arrays is true, which
            // would make every code work.
            if (expected.Length != HashSize || salt.Length == 0)
            {
                return false;
            }

            var actual = Rfc2898DeriveBytes.Pbkdf2(
                Normalize(code), salt, iterations, HashAlgorithmName.SHA256, HashSize);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// The code as it is compared: lowercase, no dashes or spaces. Somebody reading
    /// one off paper types the dash sometimes and not others, and both are the same
    /// code.
    /// </summary>
    public static string Normalize(string? code)
    {
        var builder = new StringBuilder(Length);
        foreach (var character in code ?? string.Empty)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }
}
