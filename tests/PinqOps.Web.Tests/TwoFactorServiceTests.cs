using Microsoft.Extensions.Logging.Abstractions;
using PinqOps.TwoFactor;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// Enrolling an account and checking the code at the second step.
///
/// <para>The property that matters most is not that a correct code works — it is
/// that a correct code stops working the moment it has been used, and that a
/// half-finished enrolment cannot lock anybody out of their own server.</para>
/// </summary>
public class TwoFactorServiceTests : IDisposable
{
    private const string User = "ada";

    private readonly string _directory;
    private readonly UiConfigStore _store;
    private readonly TwoFactorService _twoFactor;

    public TwoFactorServiceTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-2fa-tests").FullName;
        _store = new UiConfigStore(Path.Combine(_directory, "ui.json"));
        _store.Update(config => config.Users.Add(new UserAccount
        {
            Username = User,
            PasswordHash = PasswordHasher.Hash("a-long-enough-password"),
            Role = UserRoles.Admin,
        }));

        _twoFactor = new TwoFactorService(_store, NullLogger<TwoFactorService>.Instance);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string CodeFor(string secret, int stepOffset = 0)
    {
        Base32.TryDecode(secret, out var bytes);
        return Totp.Compute(bytes, Totp.CounterFor(DateTimeOffset.UtcNow) + stepOffset);
    }

    private (string Secret, IReadOnlyList<string> RecoveryCodes) Enrol()
    {
        var secret = _twoFactor.Begin(User).Secret;
        return (secret, _twoFactor.Confirm(User, CodeFor(secret)));
    }

    // ---- enrolment -------------------------------------------------------------

    [Fact]
    public void StartingEnrolmentGivesBackSomethingAnAppCanRead()
    {
        var (secret, uri, svg) = _twoFactor.Begin(User);

        Assert.True(Base32.TryDecode(secret, out var bytes));
        Assert.Equal(Totp.SecretBytes, bytes.Length);
        Assert.Contains("otpauth://totp/pinqops:ada?", uri, StringComparison.Ordinal);
        Assert.StartsWith("<svg", svg, StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole reason enrolment is two steps: a QR that failed to scan, or
    /// scanned into the wrong app, must not be able to lock somebody out of their
    /// own server.
    /// </summary>
    [Fact]
    public void StartingEnrolmentDoesNotTurnAnythingOn()
    {
        _twoFactor.Begin(User);

        Assert.False(_twoFactor.IsEnabledFor(User));
        Assert.Equal(TwoFactorResult.NotEnrolled, _twoFactor.Verify(User, "000000"));
    }

    [Fact]
    public void AWrongCodeDoesNotFinishEnrolment()
    {
        _twoFactor.Begin(User);

        Assert.Throws<ArgumentException>(() => _twoFactor.Confirm(User, "000000"));
        Assert.False(_twoFactor.IsEnabledFor(User));
    }

    [Fact]
    public void ConfirmingWithoutStartingIsRefused() =>
        Assert.Throws<InvalidOperationException>(() => _twoFactor.Confirm(User, "123456"));

    [Fact]
    public void ACodeFromTheNewSecretTurnsItOnAndHandsOverTheRecoveryCodes()
    {
        var (_, codes) = Enrol();

        Assert.True(_twoFactor.IsEnabledFor(User));
        Assert.Equal(RecoveryCode.Count, codes.Count);
    }

    /// <summary>
    /// Starting again replaces the secret, so an abandoned attempt leaves nothing
    /// behind that would still produce working codes.
    /// </summary>
    [Fact]
    public void StartingAgainReplacesTheSecret()
    {
        var first = _twoFactor.Begin(User).Secret;
        var second = _twoFactor.Begin(User).Secret;

        Assert.NotEqual(first, second);
        Assert.Throws<ArgumentException>(() => _twoFactor.Confirm(User, CodeFor(first)));
    }

    [Fact]
    public void EnrollingTwiceIsRefusedRatherThanSilentlyReplacingIt()
    {
        Enrol();

        Assert.Throws<InvalidOperationException>(() => _twoFactor.Begin(User));
    }

    // ---- signing in ------------------------------------------------------------

    [Fact]
    public void TheCurrentCodeIsAccepted()
    {
        var (secret, _) = Enrol();

        // A step on, because Confirm has already spent the current one.
        Assert.Equal(TwoFactorResult.Accepted, _twoFactor.Verify(User, CodeFor(secret, 1)));
    }

    [Fact]
    public void AWrongCodeIsNot()
    {
        Enrol();

        Assert.Equal(TwoFactorResult.Wrong, _twoFactor.Verify(User, "000000"));
    }

    /// <summary>
    /// Without this a code stays usable for the rest of its window, so anyone who
    /// watched it being typed can sign in again within the minute.
    /// </summary>
    [Fact]
    public void ACodeCannotBeUsedTwice()
    {
        var (secret, _) = Enrol();
        var code = CodeFor(secret, 1);

        Assert.Equal(TwoFactorResult.Accepted, _twoFactor.Verify(User, code));
        Assert.Equal(TwoFactorResult.Wrong, _twoFactor.Verify(User, code));
    }

    /// <summary>The enrolment code counts as used too — it was typed in the open.</summary>
    [Fact]
    public void TheCodeThatFinishedEnrolmentIsAlreadySpent()
    {
        var secret = _twoFactor.Begin(User).Secret;
        var code = CodeFor(secret);
        _twoFactor.Confirm(User, code);

        Assert.Equal(TwoFactorResult.Wrong, _twoFactor.Verify(User, code));
    }

    // ---- recovery codes --------------------------------------------------------

    [Fact]
    public void ARecoveryCodeWorksWhenThePhoneIsGone()
    {
        var (_, codes) = Enrol();

        Assert.Equal(TwoFactorResult.AcceptedRecoveryCode, _twoFactor.Verify(User, codes[3]));
    }

    [Fact]
    public void ARecoveryCodeWorksExactlyOnce()
    {
        var (_, codes) = Enrol();

        Assert.Equal(TwoFactorResult.AcceptedRecoveryCode, _twoFactor.Verify(User, codes[0]));
        Assert.Equal(TwoFactorResult.Wrong, _twoFactor.Verify(User, codes[0]));
        Assert.Equal(RecoveryCode.Count - 1, _twoFactor.Find(User)!.RecoveryCodeHashes.Count);
    }

    /// <summary>
    /// A correct six digits must never cost a recovery code — they are checked
    /// only once the code has failed.
    /// </summary>
    [Fact]
    public void ACorrectCodeSpendsNoRecoveryCode()
    {
        var (secret, _) = Enrol();

        _twoFactor.Verify(User, CodeFor(secret, 1));

        Assert.Equal(RecoveryCode.Count, _twoFactor.Find(User)!.RecoveryCodeHashes.Count);
    }

    [Fact]
    public void FreshRecoveryCodesReplaceTheOldOnes()
    {
        var (_, old) = Enrol();
        var fresh = _twoFactor.RegenerateRecoveryCodes(User);

        Assert.Equal(TwoFactorResult.Wrong, _twoFactor.Verify(User, old[0]));
        Assert.Equal(TwoFactorResult.AcceptedRecoveryCode, _twoFactor.Verify(User, fresh[0]));
    }

    // ---- turning it off and what is on disk ------------------------------------

    [Fact]
    public void TurningItOffForgetsTheSecretAndTheCodes()
    {
        var (secret, codes) = Enrol();
        _twoFactor.Disable(User);

        Assert.False(_twoFactor.IsEnabledFor(User));
        Assert.Null(_twoFactor.Find(User)!.TwoFactorSecret);
        Assert.Empty(_twoFactor.Find(User)!.RecoveryCodeHashes);
        Assert.Equal(TwoFactorResult.NotEnrolled, _twoFactor.Verify(User, CodeFor(secret, 1)));
        Assert.Equal(TwoFactorResult.NotEnrolled, _twoFactor.Verify(User, codes[0]));
    }

    /// <summary>
    /// A TOTP secret is a second password: anyone holding it can produce that
    /// account's codes forever, so a copied-away config file must not carry it in
    /// the clear.
    /// </summary>
    [Fact]
    public void TheSecretIsNotInTheConfigFileInPlainText()
    {
        var (secret, codes) = Enrol();
        var onDisk = File.ReadAllText(_store.Path_);

        Assert.DoesNotContain(secret, onDisk, StringComparison.Ordinal);
        Assert.DoesNotContain(RecoveryCode.Normalize(codes[0]), onDisk, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ItSurvivesAReload()
    {
        var (secret, _) = Enrol();
        var reopened = new TwoFactorService(
            new UiConfigStore(_store.Path_), NullLogger<TwoFactorService>.Instance);

        Assert.True(reopened.IsEnabledFor(User));
        Assert.Equal(TwoFactorResult.Accepted, reopened.Verify(User, CodeFor(secret, 1)));
    }

    [Fact]
    public void AnAccountThatDoesNotExistIsSaidSoRatherThanIgnored() =>
        Assert.Throws<KeyNotFoundException>(() => _twoFactor.Begin("nobody"));
}

/// <summary>The half-finished sign-ins waiting for a code.</summary>
public class TwoFactorChallengeStoreTests
{
    [Fact]
    public void AChallengeResolvesToTheAccountItWasMintedFor()
    {
        var challenges = new TwoFactorChallengeStore();

        Assert.Equal("ada", challenges.Resolve(challenges.Create("ada")));
    }

    /// <summary>
    /// A mistyped digit must not cost the challenge — otherwise it means entering
    /// the password again, and the throttle is what limits the guessing.
    /// </summary>
    [Fact]
    public void ResolvingDoesNotSpendIt()
    {
        var challenges = new TwoFactorChallengeStore();
        var token = challenges.Create("ada");

        Assert.Equal("ada", challenges.Resolve(token));
        Assert.Equal("ada", challenges.Resolve(token));
    }

    [Fact]
    public void OnceSpentItIsGone()
    {
        var challenges = new TwoFactorChallengeStore();
        var token = challenges.Create("ada");
        challenges.Consume(token);

        Assert.Null(challenges.Resolve(token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("deadbeef")]
    public void SomethingThatIsNotAChallengeResolvesToNothing(string? token) =>
        Assert.Null(new TwoFactorChallengeStore().Resolve(token));

    [Fact]
    public void EveryChallengeIsItsOwn()
    {
        var challenges = new TwoFactorChallengeStore();

        Assert.NotEqual(challenges.Create("ada"), challenges.Create("ada"));
    }
}
