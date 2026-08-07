using System.Security.Cryptography;
using PinqOps;
using Xunit;

namespace PinqOps.Core.Tests;

public class SecretBoxTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("pinqops-secretbox-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static SecretBox WithRandomKey() => new(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void ProtectThenUnprotect_RoundTrips()
    {
        var box = WithRandomKey();

        Assert.Equal("ghp_secret", box.Unprotect(box.Protect("ghp_secret")));
    }

    [Fact]
    public void Protect_DoesNotLeaveThePlaintextInTheStoredValue()
    {
        var stored = WithRandomKey().Protect("ghp_secret");

        Assert.StartsWith(SecretBox.Prefix, stored);
        Assert.DoesNotContain("ghp_secret", stored);
    }

    // A fresh nonce per call, so two encryptions of the same value do not reveal
    // that they are the same value.
    [Fact]
    public void Protect_IsNotDeterministic()
    {
        var box = WithRandomKey();

        Assert.NotEqual(box.Protect("same"), box.Protect("same"));
    }

    // An absent secret must stay absent: encrypting it would turn "not
    // configured" into something that looks configured.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Protect_PassesThroughNullAndEmpty(string? value)
    {
        Assert.Equal(value, WithRandomKey().Protect(value));
    }

    [Fact]
    public void Protect_IsIdempotent()
    {
        var box = WithRandomKey();
        var once = box.Protect("secret");

        Assert.Equal(once, box.Protect(once));
    }

    // This is what migrates the existing plaintext files.
    [Fact]
    public void Unprotect_PassesThroughAnUnencryptedValue()
    {
        Assert.Equal("plain-token", WithRandomKey().Unprotect("plain-token"));
    }

    [Fact]
    public void Unprotect_WithTheWrongKey_FailsLoudly()
    {
        var stored = WithRandomKey().Protect("secret");

        var exception = Assert.Throws<InvalidOperationException>(() => WithRandomKey().Unprotect(stored));
        Assert.Contains("does not match", exception.Message);
    }

    // GCM authenticates the ciphertext, so an edited value is rejected rather
    // than decrypting to something else.
    [Fact]
    public void Unprotect_DetectsTampering()
    {
        var box = WithRandomKey();
        var stored = box.Protect("secret")!;
        var payload = Convert.FromBase64String(stored[SecretBox.Prefix.Length..]);
        payload[^1] ^= 0xFF;
        var tampered = SecretBox.Prefix + Convert.ToBase64String(payload);

        Assert.Throws<InvalidOperationException>(() => box.Unprotect(tampered));
    }

    [Fact]
    public void Unprotect_CorruptValue_FailsLoudly()
    {
        Assert.Throws<InvalidOperationException>(() => WithRandomKey().Unprotect(SecretBox.Prefix + "not base64!"));
        Assert.Throws<InvalidOperationException>(() => WithRandomKey().Unprotect(SecretBox.Prefix + "AAAA"));
    }

    [Fact]
    public void ForDirectory_CreatesAKeyFileOnceAndReusesIt()
    {
        var first = SecretBox.ForDirectory(_directory);
        var stored = first.Protect("secret");

        var second = SecretBox.ForDirectory(_directory);

        Assert.Equal("secret", second.Unprotect(stored));
    }

    [Fact]
    public void ForDirectory_KeyFileIsOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix file modes only.
        }

        SecretBox.ForDirectory(_directory);

        var keyFile = Directory.GetFiles(_directory, "secret.key").Single();
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyFile));
    }

    // With a passphrase the key is derived rather than stored, so the file left
    // behind is a salt and is useless on its own.
    //
    // The passphrase is passed in rather than set in the environment. That variable
    // belongs to the process, and setting it here changed the mode every other test
    // running at that moment was in — the two modes keep different things in the same
    // file, so whichever got there first left the next one reading it as the wrong
    // shape. It surfaced as "not a valid pinqops key file", at random, in tests with
    // nothing to do with passphrases.
    [Fact]
    public void ForDirectory_WithAPassphrase_DerivesTheKey()
    {
        var stored = SecretBox.ForDirectory(_directory, "correct horse battery staple").Protect("secret");

        Assert.Equal("secret", SecretBox.ForDirectory(_directory, "correct horse battery staple").Unprotect(stored));
        Assert.Throws<InvalidOperationException>(
            () => SecretBox.ForDirectory(_directory, "a different passphrase").Unprotect(stored));
    }

    /// <summary>
    /// Turning a passphrase on, or off, on an install that already has secrets: the
    /// one file means different things in the two modes, so it cannot be read. What
    /// matters is that the refusal says which of those happened — "not a valid key
    /// file" blamed the file and sent an operator looking for corruption that was
    /// not there.
    /// </summary>
    [Fact]
    public void ForDirectory_WhenThePassphraseModeChanged_SaysWhichWayRoundItIs()
    {
        SecretBox.ForDirectory(_directory, passphrase: null);

        var turnedOn = Assert.Throws<InvalidOperationException>(
            () => SecretBox.ForDirectory(_directory, "a passphrase"));
        Assert.Contains(SecretBox.PassphraseVariable, turnedOn.Message, StringComparison.Ordinal);
        Assert.Contains("holds a key from before", turnedOn.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ForDirectory_WhenThePassphraseWasRemoved_SaysToSetItAgain()
    {
        SecretBox.ForDirectory(_directory, "a passphrase");

        var turnedOff = Assert.Throws<InvalidOperationException>(
            () => SecretBox.ForDirectory(_directory, passphrase: null));
        Assert.Contains("holds the salt", turnedOff.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_RejectsAKeyOfTheWrongSize() =>
        Assert.Throws<ArgumentException>(() => new SecretBox(RandomNumberGenerator.GetBytes(16)));
}
