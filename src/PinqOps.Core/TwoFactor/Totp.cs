using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PinqOps.TwoFactor;

/// <summary>
/// RFC 6238 time-based one-time passwords — the six digits an authenticator app
/// shows.
///
/// <para><b>SHA-1, 6 digits, 30 seconds.</b> Not because they are the strongest
/// choices but because they are the only ones every app implements. A code that a
/// phone cannot produce is not more secure, it is unusable; and the security of
/// TOTP rests on the secret and the short window, not on the digest.</para>
/// </summary>
public static class Totp
{
    public const int StepSeconds = 30;

    public const int Digits = 6;

    /// <summary>
    /// 20 bytes, which is the SHA-1 block-friendly size RFC 4226 recommends and
    /// what every app expects. It is 160 bits of entropy behind a six-digit code.
    /// </summary>
    public const int SecretBytes = 20;

    /// <summary>
    /// How many steps either side of now are accepted. One is what absorbs an
    /// out-of-sync phone clock and the seconds between reading a code and pressing
    /// enter; more would widen the window an intercepted code stays usable in.
    /// </summary>
    public const int DefaultWindow = 1;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    /// <summary>The step number a moment falls in.</summary>
    public static long CounterFor(DateTimeOffset at) => at.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>The code for one step, zero-padded to <see cref="Digits"/>.</summary>
    public static string Compute(ReadOnlySpan<byte> secret, long counter)
    {
        Span<byte> message = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(message, counter);

        Span<byte> digest = stackalloc byte[20];
        HMACSHA1.HashData(secret, message, digest);

        // RFC 4226 dynamic truncation: the low nibble of the last byte picks where
        // to read four bytes from, so the code depends on the whole digest rather
        // than on a fixed slice of it.
        var offset = digest[^1] & 0x0F;
        var binary = ((digest[offset] & 0x7F) << 24)
            | (digest[offset + 1] << 16)
            | (digest[offset + 2] << 8)
            | digest[offset + 3];

        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The step a code belongs to, or null when it belongs to none of the accepted
    /// ones.
    ///
    /// <para><b>Replay is what <paramref name="lastUsedCounter"/> is for.</b>
    /// Without it a code stays valid for the whole window it was issued in — so
    /// anyone who watched it being typed, or read it out of a log, can sign in
    /// again within the next minute. Returning the matched step makes the caller
    /// store it, and a step at or below the stored one is refused however correct
    /// the arithmetic is.</para>
    /// </summary>
    public static long? Verify(
        ReadOnlySpan<byte> secret,
        string? code,
        DateTimeOffset at,
        long lastUsedCounter = -1,
        int window = DefaultWindow)
    {
        var candidate = Normalize(code);
        if (candidate.Length != Digits || secret.Length == 0)
        {
            return null;
        }

        var centre = CounterFor(at);
        long? matched = null;

        // Every step is checked even after a match, so the time taken does not
        // depend on which step the code came from.
        for (var offset = -window; offset <= window; offset++)
        {
            var counter = centre + offset;
            var expected = Compute(secret, counter);
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(candidate))
                && counter > lastUsedCounter)
            {
                matched = counter;
            }
        }

        return matched;
    }

    /// <summary>
    /// The <c>otpauth://</c> URI an authenticator app reads, whether from a QR code
    /// or pasted by hand.
    /// </summary>
    public static string Uri(string issuer, string account, ReadOnlySpan<byte> secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);

        // The label is "issuer:account" and both halves are escaped: an account name
        // with a colon or a slash in it would otherwise end up in the wrong field,
        // and the app would show somebody else's name beside the code.
        var label = System.Uri.EscapeDataString(issuer) + ":" + System.Uri.EscapeDataString(account);
        return $"otpauth://totp/{label}"
            + $"?secret={Base32.Encode(secret)}"
            + $"&issuer={System.Uri.EscapeDataString(issuer)}"
            + $"&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }

    /// <summary>
    /// The digits out of what was typed. Apps display <c>123 456</c> and people
    /// copy the space with it; refusing that is refusing the correct code.
    /// </summary>
    public static string Normalize(string? code) =>
        new([.. (code ?? string.Empty).Where(char.IsAsciiDigit)]);
}
