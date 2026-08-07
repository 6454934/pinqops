using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

public class UserRolesTests
{
    [Theory]
    [InlineData("viewer", "read")]
    [InlineData("deployer", "deploy")]
    [InlineData("admin", "admin")]
    public void ScopeFor_MapsRolesToScopes(string role, string expectedScope)
    {
        Assert.Equal(expectedScope, UserRoles.ScopeFor(role));
    }

    [Theory]
    [InlineData("viewer", true)]
    [InlineData("deployer", true)]
    [InlineData("admin", true)]
    [InlineData("root", false)]
    [InlineData(null, false)]
    public void IsValid_AcceptsOnlyTheThreeRoles(string? role, bool expected)
    {
        Assert.Equal(expected, UserRoles.IsValid(role));
    }

    [Theory]
    [InlineData("deployer", "deployer")]
    [InlineData("nonsense", "viewer")]
    [InlineData(null, "viewer")]
    public void Normalize_DefaultsUnknownToLeastPrivilege(string? role, string expected)
    {
        Assert.Equal(expected, UserRoles.Normalize(role));
    }

    [Fact]
    public void RoleScope_ComposesWithApiScopes_ForPermissionChecks()
    {
        // A deployer's scope satisfies a deploy-required route but not an admin one.
        var deployScope = UserRoles.ScopeFor(UserRoles.Deployer);
        Assert.True(ApiScopes.Satisfies(deployScope, ApiScopes.RequiredFor("POST", "/api/deploy/rollback")));
        Assert.False(ApiScopes.Satisfies(deployScope, ApiScopes.RequiredFor("POST", "/api/users")));

        // A viewer can read but not deploy.
        var viewScope = UserRoles.ScopeFor(UserRoles.Viewer);
        Assert.True(ApiScopes.Satisfies(viewScope, ApiScopes.RequiredFor("GET", "/api/settings")));
        Assert.False(ApiScopes.Satisfies(viewScope, ApiScopes.RequiredFor("POST", "/api/deploy/rollback")));
    }
}
