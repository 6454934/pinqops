using System.Text;
using PinqOps.TwoFactor;
using Xunit;

namespace PinqOps.Tests.TwoFactor;

public class Base32Tests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void TheRfcsOwnVectorsComeOut(string plain, string encoded) =>
        Assert.Equal(encoded, Base32.Encode(Encoding.ASCII.GetBytes(plain)));

    [Theory]
    [InlineData("MZXW6YTBOI")]
    [InlineData("mzxw6ytboi")]
    [InlineData("MZXW 6YTB OI")]
    [InlineData("MZXW6YTBOI======")]
    public void WhatAPersonTypesIsAccepted(string text)
    {
        Assert.True(Base32.TryDecode(text, out var data));
        Assert.Equal("foobar", Encoding.ASCII.GetString(data));
    }

    [Theory]
    [InlineData("MZXW0")]
    [InlineData("hello!")]
    [InlineData("1234")]
    public void SomethingThatIsNotBase32IsRefused(string text) => Assert.False(Base32.TryDecode(text, out _));

    [Fact]
    public void ASecretSurvivesTheRoundTrip()
    {
        var secret = Totp.NewSecret();

        Assert.True(Base32.TryDecode(Base32.Encode(secret), out var decoded));
        Assert.Equal(secret, decoded);
    }
}

public class TotpTests
{
    /// <summary>
    /// RFC 6238's published test secret: the ASCII digits "12345678901234567890".
    /// </summary>
    private static readonly byte[] Secret = Encoding.ASCII.GetBytes("12345678901234567890");

    /// <summary>
    /// The RFC's own vectors for SHA-1, truncated to the six digits every
    /// authenticator app shows. The test times are the ones the document lists, and
    /// the eight digits it prints end with these six.
    /// </summary>
    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void TheRfcsOwnVectorsComeOut(long unixSeconds, string expected) =>
        Assert.Equal(expected, Totp.Compute(Secret, unixSeconds / Totp.StepSeconds));

    [Fact]
    public void ACodeIsSixDigits() =>
        Assert.Matches("^[0-9]{6}$", Totp.Compute(Totp.NewSecret(), 1));

    [Fact]
    public void TheCurrentCodeVerifies()
    {
        var now = DateTimeOffset.UtcNow;
        var code = Totp.Compute(Secret, Totp.CounterFor(now));

        Assert.Equal(Totp.CounterFor(now), Totp.Verify(Secret, code, now));
    }

    /// <summary>
    /// A phone's clock drifts, and there are seconds between reading a code and
    /// pressing enter. One step either side is what absorbs both.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void OneStepEitherSideIsAccepted(int offset)
    {
        var now = DateTimeOffset.UtcNow;
        var code = Totp.Compute(Secret, Totp.CounterFor(now) + offset);

        Assert.NotNull(Totp.Verify(Secret, code, now));
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(2)]
    public void TwoStepsAwayIsNot(int offset)
    {
        var now = DateTimeOffset.UtcNow;
        var code = Totp.Compute(Secret, Totp.CounterFor(now) + offset);

        Assert.Null(Totp.Verify(Secret, code, now));
    }

    /// <summary>
    /// Without this, a code stays usable for the whole window it was issued in —
    /// so anyone who watched it being typed can sign in again within the minute.
    /// </summary>
    [Fact]
    public void ACodeThatHasAlreadyBeenUsedIsRefused()
    {
        var now = DateTimeOffset.UtcNow;
        var code = Totp.Compute(Secret, Totp.CounterFor(now));

        var used = Totp.Verify(Secret, code, now);
        Assert.NotNull(used);
        Assert.Null(Totp.Verify(Secret, code, now, lastUsedCounter: used.Value));
    }

    /// <summary>
    /// The step before one that has been used is refused too — otherwise replaying
    /// the previous window's code would still work.
    /// </summary>
    [Fact]
    public void AnEarlierStepIsRefusedOnceALaterOneHasBeenUsed()
    {
        var now = DateTimeOffset.UtcNow;
        var counter = Totp.CounterFor(now);
        var previous = Totp.Compute(Secret, counter - 1);

        Assert.Null(Totp.Verify(Secret, previous, now, lastUsedCounter: counter));
    }

    [Fact]
    public void TheNextStepStillWorksAfterOneIsUsed()
    {
        var now = DateTimeOffset.UtcNow;
        var counter = Totp.CounterFor(now);
        var next = Totp.Compute(Secret, counter + 1);

        Assert.Equal(counter + 1, Totp.Verify(Secret, next, now, lastUsedCounter: counter));
    }

    /// <summary>Apps display <c>123 456</c>, and people copy the space with it.</summary>
    [Fact]
    public void SpacesInATypedCodeAreIgnored()
    {
        var now = DateTimeOffset.UtcNow;
        var code = Totp.Compute(Secret, Totp.CounterFor(now));

        Assert.NotNull(Totp.Verify(Secret, code[..3] + " " + code[3..], now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    public void SomethingThatIsNotASixDigitCodeIsRefused(string code) =>
        Assert.Null(Totp.Verify(Secret, code, DateTimeOffset.UtcNow));

    [Fact]
    public void AWrongCodeIsRefused() => Assert.Null(Totp.Verify(Secret, "000000", new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero)));

    // ---- the otpauth URI -------------------------------------------------------

    [Fact]
    public void TheUriIsTheOneAuthenticatorAppsRead()
    {
        var uri = Totp.Uri("pinqops", "ada", Secret);

        Assert.StartsWith("otpauth://totp/pinqops:ada?", uri, StringComparison.Ordinal);
        Assert.Contains("secret=" + Base32.Encode(Secret), uri, StringComparison.Ordinal);
        Assert.Contains("issuer=pinqops", uri, StringComparison.Ordinal);
        Assert.Contains("algorithm=SHA1&digits=6&period=30", uri, StringComparison.Ordinal);
    }

    /// <summary>
    /// A colon or a slash in an account name would land in the wrong field, and the
    /// app would show somebody else's name beside the code.
    /// </summary>
    [Fact]
    public void AnAccountNameCarryingASeparatorIsEscaped()
    {
        var uri = Totp.Uri("pinqops", "a:b/c", Secret);

        Assert.Contains("totp/pinqops:a%3Ab%2Fc?", uri, StringComparison.Ordinal);
    }
}

public class RecoveryCodeTests
{
    [Fact]
    public void ASetIsIssuedAtOnce() => Assert.Equal(RecoveryCode.Count, RecoveryCode.Generate().Count);

    [Fact]
    public void EveryCodeIsDifferent() => Assert.Equal(RecoveryCode.Count, RecoveryCode.Generate().Distinct().Count());

    [Fact]
    public void TheyAreShownInGroupsWithNoLookalikeCharacters() =>
        Assert.All(RecoveryCode.Generate(), code => Assert.Matches("^[a-z2-7]{5}-[a-z2-7]{5}$", code));

    [Fact]
    public void ACodeVerifiesAgainstItsOwnHash()
    {
        var code = RecoveryCode.Generate()[0];

        Assert.True(RecoveryCode.Verify(code, RecoveryCode.Hash(code)));
    }

    [Fact]
    public void AnotherCodeDoesNot()
    {
        var codes = RecoveryCode.Generate();

        Assert.False(RecoveryCode.Verify(codes[1], RecoveryCode.Hash(codes[0])));
    }

    /// <summary>Somebody reading one off paper types the dash sometimes and not others.</summary>
    [Fact]
    public void TheDashAndTheCasingDoNotMatter()
    {
        var code = RecoveryCode.Generate()[0];
        var stored = RecoveryCode.Hash(code);

        Assert.True(RecoveryCode.Verify(code.Replace("-", string.Empty, StringComparison.Ordinal), stored));
        Assert.True(RecoveryCode.Verify(code.ToUpperInvariant(), stored));
        Assert.True(RecoveryCode.Verify(" " + code + " ", stored));
    }

    [Fact]
    public void TheStoredFormCarriesNoneOfTheCode()
    {
        var code = RecoveryCode.Generate()[0];

        Assert.DoesNotContain(RecoveryCode.Normalize(code), RecoveryCode.Hash(code), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An empty digest derives to an empty array, and two empty arrays compare
    /// equal — so a truncated record would accept every code.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("100000.")]
    [InlineData("100000..")]
    [InlineData("100000.AAAAAAAAAAAAAAAAAAAAAA==.")]
    [InlineData("not-a-hash")]
    public void ATruncatedOrMalformedRecordAcceptsNothing(string stored) =>
        Assert.False(RecoveryCode.Verify("abcde-fghij", stored));
}
