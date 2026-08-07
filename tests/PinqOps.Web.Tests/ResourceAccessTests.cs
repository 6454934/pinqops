using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// The second-stage decision, as a table. Stage one — the scope policy — has
/// already run by the time any of this is reached, so nothing here is asked whether
/// a viewer may deploy; it is asked whether <em>this</em> caller may act on
/// <em>this</em> resource.
/// </summary>
public class ResourceAccessTests
{
    private const string Container = ResourceKinds.Container;
    private const string App = ResourceKinds.App;

    private static ContainerOwnershipStore.ContainerOwnership Owned(string owner, string access) =>
        new() { Owner = owner, Access = access };

    private static ResourceGrant Grant(string teamId, string access = GrantAccess.Manage) => new()
    {
        Kind = App,
        EnvironmentId = "local",
        ResourceId = "shop",
        TeamId = teamId,
        Access = access,
    };

    private static bool CanAccess(
        string? scope,
        string? user,
        string kind = App,
        ContainerOwnershipStore.ContainerOwnership? ownership = null,
        string[]? teams = null,
        ResourceGrant[]? grants = null,
        string required = GrantAccess.Manage) =>
        ResourceAccess.CanAccess(scope, user, kind, ownership, teams ?? [], grants ?? [], required);

    // ---- admin --------------------------------------------------------------

    /// <summary>
    /// A global admin is never refused, across every team. There is no other
    /// break-glass path in a single-binary product, and every recovery story here
    /// depends on it.
    /// </summary>
    [Fact]
    public void AnAdminReachesEverything()
    {
        Assert.True(CanAccess("admin", "boss"));
        Assert.True(CanAccess("admin", "boss", Container));
        Assert.True(CanAccess("admin", "boss", Container, Owned("someone-else", "private")));
    }

    // ---- fail closed --------------------------------------------------------

    [Theory]
    [InlineData(null, "alice")]
    [InlineData("", "alice")]
    [InlineData("deploy", null)]
    [InlineData("deploy", "")]
    public void AnUnresolvedPrincipalIsRefused(string? scope, string? user)
    {
        Assert.False(CanAccess(scope, user, App, teams: ["platform"], grants: [Grant("platform")]));
    }

    /// <summary>A resource nobody has granted is admin-only — the same rule that
    /// already protects every system container on the host.</summary>
    [Fact]
    public void AResourceWithNoGrantAndNoOwnerIsAdminOnly()
    {
        Assert.False(CanAccess("deploy", "alice", teams: ["platform"]));
        Assert.False(CanAccess("deploy", "alice", Container));
    }

    // ---- personal ownership -------------------------------------------------

    /// <summary>
    /// Ownership and grants are different relations, unioned. "This container is
    /// mine" is what a solo operator actually has, and it keeps working with no
    /// teams anywhere.
    /// </summary>
    [Fact]
    public void ADeployerStillManagesAContainerItOwns()
    {
        Assert.True(CanAccess("deploy", "alice", Container, Owned("alice", "private")));
    }

    [Fact]
    public void ADeployerDoesNotManageSomeoneElsesPrivateContainer()
    {
        Assert.False(CanAccess("deploy", "alice", Container, Owned("bob", "private")));
    }

    [Fact]
    public void APublicContainerIsManageableByAnyDeployer()
    {
        Assert.True(CanAccess("deploy", "alice", Container, Owned("bob", "public")));
    }

    [Fact]
    public void AViewerManagesNothingItOwns()
    {
        Assert.False(CanAccess("read", "alice", Container, Owned("alice", "private")));
    }

    /// <summary>Ownership is a container relation; it is not consulted for anything
    /// else, so a stray record cannot leak into another kind.</summary>
    [Fact]
    public void OwnershipDoesNotApplyToOtherKinds()
    {
        Assert.False(CanAccess("deploy", "alice", App, Owned("alice", "public")));
    }

    // ---- team grants --------------------------------------------------------

    [Fact]
    public void AGrantToATeamTheCallerIsInAllows()
    {
        Assert.True(CanAccess("deploy", "alice", teams: ["platform"], grants: [Grant("platform")]));
    }

    [Fact]
    public void AGrantToATeamTheCallerIsNotInDoesNothing()
    {
        Assert.False(CanAccess("deploy", "alice", teams: ["payments"], grants: [Grant("platform")]));
    }

    [Fact]
    public void OneMatchingGrantAmongSeveralIsEnough()
    {
        Assert.True(CanAccess(
            "deploy", "alice",
            teams: ["payments"],
            grants: [Grant("platform"), Grant("payments"), Grant("security")]));
    }

    [Fact]
    public void TeamNamesAreMatchedCaseInsensitively()
    {
        Assert.True(CanAccess("deploy", "alice", teams: ["Platform"], grants: [Grant("platform")]));
    }

    // ---- access levels ------------------------------------------------------

    [Fact]
    public void AManageGrantCoversAViewRequirement()
    {
        Assert.True(CanAccess(
            "deploy", "alice", teams: ["platform"], grants: [Grant("platform")], required: GrantAccess.View));
    }

    [Fact]
    public void AViewGrantDoesNotCoverAManageRequirement()
    {
        Assert.False(CanAccess(
            "deploy", "alice",
            teams: ["platform"],
            grants: [Grant("platform", GrantAccess.View)],
            required: GrantAccess.Manage));
    }

    /// <summary>
    /// A hand-edited access level that means nothing is read as the lowest, never
    /// the highest — a typo must not become an escalation.
    /// </summary>
    [Fact]
    public void AnUnrecognisedAccessLevelIsTreatedAsTheLowest()
    {
        Assert.False(CanAccess(
            "deploy", "alice",
            teams: ["platform"],
            grants: [Grant("platform", "superuser")],
            required: GrantAccess.Manage));

        Assert.True(CanAccess(
            "deploy", "alice",
            teams: ["platform"],
            grants: [Grant("platform", "superuser")],
            required: GrantAccess.View));
    }

    // ---- the union ----------------------------------------------------------

    /// <summary>Either route in is enough, and neither cancels the other.</summary>
    [Fact]
    public void OwnershipAndAGrantAreUnioned()
    {
        // Owns it, no grant.
        Assert.True(CanAccess("deploy", "alice", Container, Owned("alice", "private")));

        // Granted it, does not own it.
        Assert.True(CanAccess(
            "deploy", "alice", Container, Owned("bob", "private"),
            teams: ["platform"],
            grants: [new ResourceGrant
            {
                Kind = Container, EnvironmentId = "local", ResourceId = "db", TeamId = "platform",
            }]));
    }
}
