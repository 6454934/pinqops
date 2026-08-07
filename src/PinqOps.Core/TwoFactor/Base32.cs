using System.Text;

namespace PinqOps.TwoFactor;

/// <summary>
/// RFC 4648 base32, which is how every authenticator app expects a TOTP secret.
///
/// <para>Not base64: the secret is read off a screen and typed into a phone when
/// the camera will not focus, and base32's alphabet has no lowercase, no digits
/// that look like letters, and no characters that need escaping in a URI.</para>
/// </summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>The bytes as base32, without padding — which is what authenticator apps show.</summary>
    public static string Encode(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder((data.Length * 8 / 5) + 1);
        var buffer = 0;
        var bits = 0;

        foreach (var value in data)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                builder.Append(Alphabet[(buffer >> bits) & 31]);
            }
        }

        if (bits > 0)
        {
            builder.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Decodes base32, forgiving the shapes a person produces: lowercase, spaces
    /// between groups, and the padding some apps add. Anything else is a no.
    /// </summary>
    public static bool TryDecode(string? text, out byte[] data)
    {
        data = [];
        if (text is null)
        {
            return false;
        }

        var bytes = new List<byte>(text.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;

        foreach (var character in text)
        {
            if (character is ' ' or '-' or '=')
            {
                continue;
            }

            var index = Alphabet.IndexOf(char.ToUpperInvariant(character), StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)((buffer >> bits) & 0xFF));
            }
        }

        data = [.. bytes];
        return true;
    }
}
