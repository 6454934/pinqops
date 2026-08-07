using System.Security.Cryptography;
using System.Text;

namespace PinqOps;

/// <summary>
/// Symmetric encryption (AES-256-GCM) for the secrets pinqops keeps on disk —
/// the GitHub token and the generated catalog-app passwords.
///
/// <para><b>What this does and does not buy.</b> The key sits in a 0600 file
/// beside the data it protects, so it is no defence against someone who can
/// already read that directory as the dashboard user — the process has to be
/// able to decrypt unattended, and anything it can do, so can they. What it does
/// stop is a config file leaking on its own: copied into a backup or a support
/// bundle, synced somewhere, pasted into an issue, or read off a stale disk. A
/// stolen <c>ui.json</c> no longer hands over a GitHub token with push access.
/// Set <c>PINQOPS_MASTER_PASSPHRASE</c> to derive the key instead of storing it,
/// which does protect the data at rest — at the cost of supplying the passphrase
/// on every start.</para>
///
/// <para>Values are stored as <c>enc.v1:</c> + base64(nonce ‖ ciphertext ‖ tag).
/// <see cref="Unprotect"/> passes anything without that prefix through unchanged,
/// so existing plaintext files keep working and re-encrypt on the next write.</para>
/// </summary>
public sealed class SecretBox
{
    /// <summary>Marks a value as encrypted. Versioned so the scheme can change.</summary>
    public const string Prefix = "enc.v1:";

    /// <summary>Set to derive the key from a passphrase instead of a key file.</summary>
    public const string PassphraseVariable = "PINQOPS_MASTER_PASSPHRASE";

    private const string KeyFileName = "secret.key";
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int SaltSize = 16;
    private const int PassphraseIterations = 600_000;

    private readonly byte[] _key;

    public SecretBox(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeySize)
        {
            throw new ArgumentException($"The key must be {KeySize} bytes.", nameof(key));
        }

        _key = key;
    }

    /// <summary>
    /// The box for a config directory. Uses <see cref="PassphraseVariable"/> when
    /// it is set (the salt lives in the key file, which then holds no key
    /// material), otherwise a random key generated on first use.
    /// </summary>
    public static SecretBox ForDirectory(string directory) =>
        ForDirectory(directory, Environment.GetEnvironmentVariable(PassphraseVariable));

    /// <summary>
    /// The same, with the passphrase supplied rather than read from the environment.
    ///
    /// <para>Only a test names one. <see cref="PassphraseVariable"/> belongs to the
    /// process, so a test that set it to exercise this path changed the mode every
    /// other test running at that moment was in — and the two modes keep different
    /// things in the same file, so whichever of them created the file first left the
    /// next one reading it as the wrong shape. That surfaced as a key file the
    /// product called invalid, at random, in tests that have nothing to do with
    /// passphrases.</para>
    /// </summary>
    internal static SecretBox ForDirectory(string directory, string? passphrase)
    {
        // A config path with no directory part (PINQOPS_UI_CONFIG=ui.json) yields
        // an empty string here, which CreateDirectory rejects — the key belongs
        // beside the file, so that means the current directory.
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = ".";
        }

        Directory.CreateDirectory(directory);
        var keyFile = Path.Combine(directory, KeyFileName);

        if (!string.IsNullOrEmpty(passphrase))
        {
            return new SecretBox(Rfc2898DeriveBytes.Pbkdf2(
                passphrase,
                ReadOrCreate(keyFile, SaltSize, usingPassphrase: true),
                PassphraseIterations,
                HashAlgorithmName.SHA256,
                KeySize));
        }

        return new SecretBox(ReadOrCreate(keyFile, KeySize, usingPassphrase: false));
    }

    /// <summary>True when the value is already encrypted by this scheme.</summary>
    public static bool IsProtected(string? value) =>
        value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Encrypts a value. Null and empty pass through: an absent secret is absent,
    /// and encrypting it would turn "not configured" into something that looks
    /// configured.
    /// </summary>
    public string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || IsProtected(plaintext))
        {
            return plaintext;
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Encrypt(nonce, plainBytes, cipher, tag);
        }

        var payload = new byte[NonceSize + cipher.Length + TagSize];
        nonce.CopyTo(payload, 0);
        cipher.CopyTo(payload, NonceSize);
        tag.CopyTo(payload, NonceSize + cipher.Length);
        return Prefix + Convert.ToBase64String(payload);
    }

    /// <summary>
    /// Decrypts a value written by <see cref="Protect"/>. Anything without the
    /// prefix is returned unchanged, which is what migrates the existing
    /// plaintext files — they re-encrypt on the next write.
    /// </summary>
    public string? Unprotect(string? stored)
    {
        if (!IsProtected(stored))
        {
            return stored;
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(stored![Prefix.Length..]);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("A stored secret is corrupt (not valid base64).");
        }

        if (payload.Length < NonceSize + TagSize)
        {
            throw new InvalidOperationException("A stored secret is corrupt (too short).");
        }

        var cipher = new byte[payload.Length - NonceSize - TagSize];
        var plain = new byte[cipher.Length];
        Array.Copy(payload, NonceSize, cipher, 0, cipher.Length);

        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(
                payload.AsSpan(0, NonceSize),
                cipher,
                payload.AsSpan(NonceSize + cipher.Length, TagSize),
                plain);
        }
        catch (CryptographicException)
        {
            // Wrong key (a changed passphrase, a restored config without its key
            // file) or a tampered value. Say which, because "authentication
            // failed" on its own sends people looking in the wrong place.
            throw new InvalidOperationException(
                "A stored secret could not be decrypted — the key file or passphrase does not match the data. "
                + "Restore the matching key, or clear the secret and set it again.");
        }

        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>
    /// The bytes in <paramref name="path"/>, creating them 0600 on first use.
    /// Written through <see cref="SecureFile"/> so the bytes never land on a
    /// world-readable inode, even briefly.
    /// </summary>
    private static byte[] ReadOrCreate(string path, int size, bool usingPassphrase)
    {
        if (File.Exists(path))
        {
            var existing = Convert.FromBase64String(SecureFile.ReadAllText(path).Trim());
            if (existing.Length == size)
            {
                return existing;
            }

            // The two modes keep different things in this one file: a key when there
            // is no passphrase, the salt for one when there is. So the length not
            // matching almost always means the mode changed under an install that
            // already had secrets — and "not a valid key file" blames the file for
            // that, which sends an operator looking for corruption that is not there.
            throw new InvalidOperationException(usingPassphrase
                ? $"'{path}' holds a key from before {PassphraseVariable} was set, not the salt for it. "
                + $"Unset {PassphraseVariable} to go on using that key — the secrets it protects cannot be "
                + "read without it. To start again with a passphrase, move the file aside first."
                : $"'{path}' holds the salt for {PassphraseVariable}, not a key. Set {PassphraseVariable} "
                + "again — the secrets it protects cannot be read without it. To start again without a "
                + "passphrase, move the file aside first.");
        }

        var created = RandomNumberGenerator.GetBytes(size);
        SecureFile.WriteAllText(path, Convert.ToBase64String(created));
        return created;
    }
}
