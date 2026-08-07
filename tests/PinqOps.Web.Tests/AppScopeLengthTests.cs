using PinqOps.Secrets;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// An app's id has to be usable as a secret scope, because that is how a secret
/// is bound to one app.
///
/// <para>Nothing enforces that at either end. The id is minted from
/// <c>&lt;owner&gt;-&lt;repo&gt;</c> with no length limit, and the scope check
/// imposes one of its own — on the stated premise that an app id is "already
/// constrained to a compose project name", which constrains the characters and
/// says nothing about the length. GitHub allows an owner of 39 characters and a
/// repository of 100, so an id that no scope will accept is an ordinary
/// repository, not a contrived one.</para>
///
/// <para>What that costs is quiet and permanent: the sync catches the refusal and
/// reports the app, but that app never receives a single secret, and the message
/// blames a "secret scope" the operator never chose and cannot change.</para>
/// </summary>
public class AppScopeLengthTests
{
    private static string IdFor(string owner, string name) =>
        AppConnection.SlugFor(new GitHubRepository(owner, name, "github.com"));

    [Fact]
    public void AnOrdinaryAppIdIsAValidScope() =>
        Assert.True(SecretScopes.IsValid(IdFor("acme", "shop")));

    /// <summary>
    /// GitHub's own ceilings: 39 for an owner, 100 for a repository. An id built
    /// from them is 140 characters, and it is still an id pinqops will mint.
    /// </summary>
    [Fact]
    public void TheLongestIdGitHubCanProduceIsStillAValidScope()
    {
        var id = IdFor(new string('a', 39), new string('b', 100));

        Assert.Equal(140, id.Length);
        Assert.True(
            SecretScopes.IsValid(id),
            $"an app id of {id.Length} characters is not accepted as a secret scope, so that app can never "
            + "receive one");
    }

    /// <summary>
    /// The smallest case that already fails: a long-but-real owner and a
    /// middling repository name.
    /// </summary>
    [Fact]
    public void ARealisticLongNameIsAValidScope() =>
        Assert.True(SecretScopes.IsValid(IdFor(new string('a', 39), new string('b', 26))));

    /// <summary>
    /// Stated as the rule rather than as a number, so raising one limit without the
    /// other is what fails.
    /// </summary>
    [Fact]
    public void TheScopeCeilingCoversEveryIdThatCanBeMinted()
    {
        var longest = IdFor(new string('a', 39), new string('b', 100));

        Assert.True(
            SecretScopes.MaximumLength >= longest.Length,
            $"the scope ceiling is {SecretScopes.MaximumLength} but an app id can be {longest.Length}");
    }

    /// <summary>Raising the ceiling must not make the character rule any looser.</summary>
    [Theory]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("has:colon")]
    [InlineData("")]
    public void AScopeStillHasToLookLikeOne(string scope) =>
        Assert.False(SecretScopes.IsValid(scope));
}
