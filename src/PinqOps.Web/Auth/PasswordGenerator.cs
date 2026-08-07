using System.Security.Cryptography;

namespace PinqOps.Web;

/// <summary>
/// Generates credentials for catalog app installs. Alphanumeric only, so a
/// value is safe inside <c>NEO4J_AUTH=neo4j/x</c>, connection URLs and shell-
/// free <c>docker run -e</c> arguments alike.
/// </summary>
public static class PasswordGenerator
{
    private const string Alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int Length = 20;

    public static string Generate()
    {
        var chars = new char[Length];
        for (var i = 0; i < Length; i++)
        {
            // GetInt32 is rejection-sampled, so every alphabet symbol is exactly
            // equally likely (no modulo bias).
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }
}
