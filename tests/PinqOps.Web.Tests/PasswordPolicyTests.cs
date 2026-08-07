using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

public class PasswordPolicyTests
{
    [Fact]
    public void AcceptsALongPassphrase() =>
        Assert.Null(PasswordPolicy.Validate("correct horse battery staple"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("elevenchars")]
    public void RejectsAnythingUnderTheMinimum(string? password) =>
        Assert.NotNull(PasswordPolicy.Validate(password));

    [Fact]
    public void AcceptsExactlyTheMinimum() =>
        Assert.Null(PasswordPolicy.Validate(new string("abcdefghijkl")));

    // The first things a credential-stuffing run tries.
    [Theory]
    [InlineData("password123")]
    [InlineData("PASSWORD123")]
    [InlineData("123456789012")]
    [InlineData("administrator")]
    [InlineData("pinqops123")]
    public void RejectsTheObviousGuesses(string password) =>
        Assert.NotNull(PasswordPolicy.Validate(password));

    // Clears a length check while carrying almost no entropy.
    [Theory]
    [InlineData("aaaaaaaaaaaaaaaa")]
    [InlineData("abababababababab")]
    public void RejectsTooFewDistinctCharacters(string password) =>
        Assert.NotNull(PasswordPolicy.Validate(password));

    // Length is what is asked for, not a composition rule that mostly produces
    // "Password1!".
    [Fact]
    public void DoesNotRequireSymbolsOrDigits() =>
        Assert.Null(PasswordPolicy.Validate("thequickbrownfox"));
}
