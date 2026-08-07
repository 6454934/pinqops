using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// What may be an account name.
///
/// <para>The rule that matters most here is not a shape but a reservation. Every
/// API token used to authenticate as one shared principal, the literal
/// <c>api-token</c>, and container ownership records written in that era still
/// name it. Those records are safe for exactly one reason, stated where the
/// constant lives: nobody can authenticate as that principal any more, so they
/// resolve to unowned, which is admin-only. The team migration leans on the same
/// property — it deliberately does not convert those rows into grants, because
/// reinterpreting them would hand access to whoever the guess landed on.</para>
///
/// <para>An account created under that name makes every one of those statements
/// false at once, and the route that would have allowed it is the anonymous one:
/// accepting an invitation, where the invitee picks their own name and the role
/// they were invited as need not be admin.</para>
/// </summary>
public class UsernamePolicyTests
{
    [Theory]
    [InlineData("ada")]
    [InlineData("ada.lovelace")]
    [InlineData("ada-lovelace")]
    [InlineData("ada_lovelace")]
    [InlineData("a1")]
    public void AnOrdinaryNameIsAccepted(string username) =>
        Assert.Null(UsernamePolicy.Validate(username));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a")]
    [InlineData("ada lovelace")]
    [InlineData("ada@example.com")]
    [InlineData("ada/../root")]
    public void AMalformedNameIsRefused(string? username) =>
        Assert.NotNull(UsernamePolicy.Validate(username));

    /// <summary>
    /// The retired token principal. Refused however it is spelled: the account
    /// lookup that would collide with it compares case-insensitively, so accepting
    /// "API-Token" would create exactly the account this refuses.
    /// </summary>
    [Theory]
    [InlineData("api-token")]
    [InlineData("API-TOKEN")]
    [InlineData("Api-Token")]
    [InlineData("  api-token  ")]
    public void TheRetiredTokenPrincipalIsRefused(string username)
    {
        Assert.NotNull(UsernamePolicy.Validate(username));
        Assert.Contains("reserved", UsernamePolicy.Validate(username)!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Stated against the constant rather than the literal, so renaming the
    /// principal cannot leave the reservation pointing at the old spelling.
    /// </summary>
    [Fact]
    public void TheReservationTracksTheConstant() =>
        Assert.NotNull(UsernamePolicy.Validate(ApiTokenStore.RetiredPrincipal));

    /// <summary>
    /// A token's own principal is <c>token:&lt;id&gt;</c>, and ':' is not an
    /// allowed character — which is why the prefix needs no reservation of its own.
    /// This pins that reasoning, since it is what makes the reserved list short.
    /// </summary>
    [Fact]
    public void ATokenPrincipalCannotBeSpelledAsAnAccountName() =>
        Assert.NotNull(UsernamePolicy.Validate(ApiTokenStore.PrincipalPrefix + "abc123"));
}
