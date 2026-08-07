using Microsoft.AspNetCore.Http;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

public class ResourceVisibilityTests : IDisposable
{
    private const string Kind = ResourceKinds.App;

    private readonly string _directory;
    private readonly TeamStore _teams;
    private readonly ResourceVisibility _visibility;

    public ResourceVisibilityTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-visibility-").FullName;
        _teams = new TeamStore(Path.Combine(_directory, "teams.json"));
        _visibility = new ResourceVisibility(_teams);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static HttpContext Caller(string scope, string user)
    {
        var context = new DefaultHttpContext();
        context.Items["scope"] = scope;
        context.Items["user"] = user;
        return context;
    }

    private void Team(string id, params string[] members) =>
        _teams.Update<object?>(directory =>
        {
            directory.Teams.Add(new Team
            {
                Id = id,
                Name = id,
                Members = [.. members.Select(member => new TeamMember { Principal = member })],
            });
            return null;
        });

    private void Grant(string teamId, string resourceId, string kind = Kind) =>
        _teams.Update<object?>(directory =>
        {
            directory.Grants.Add(new ResourceGrant
            {
                Kind = kind,
                EnvironmentId = "local",
                ResourceId = resourceId,
                TeamId = teamId,
            });
            return null;
        });

    /// <summary>
    /// The rule that makes teams safe to ship: an install with no grants looks
    /// exactly like an install with no teams. Nothing disappears from anyone's
    /// listing until somebody deliberately claims it.
    /// </summary>
    [Fact]
    public void AnUnclaimedResourceIsVisibleToEveryone()
    {
        Team("platform", "alice");

        Assert.True(_visibility.CanView(Caller("read", "alice"), Kind, "shop"));
        Assert.True(_visibility.CanView(Caller("deploy", "carol"), Kind, "shop"));
    }

    [Fact]
    public void AClaimedResourceIsHiddenFromEveryoneOutsideTheTeam()
    {
        Team("platform", "alice");
        Grant("platform", "shop");

        Assert.True(_visibility.CanView(Caller("deploy", "alice"), Kind, "shop"));
        Assert.False(_visibility.CanView(Caller("deploy", "carol"), Kind, "shop"));
    }

    [Fact]
    public void AnAdminSeesEverythingClaimedOrNot()
    {
        Team("platform", "alice");
        Grant("platform", "shop");

        Assert.True(_visibility.CanView(Caller("admin", "boss"), Kind, "shop"));
    }

    /// <summary>Claiming one resource does not hide the others.</summary>
    [Fact]
    public void ClaimingOneResourceLeavesTheRestAlone()
    {
        Team("platform", "alice");
        Grant("platform", "shop");

        Assert.False(_visibility.CanView(Caller("deploy", "carol"), Kind, "shop"));
        Assert.True(_visibility.CanView(Caller("deploy", "carol"), Kind, "billing"));
    }

    [Fact]
    public void AGrantOfAnotherKindDoesNotClaimThisOne()
    {
        Team("platform", "alice");
        Grant("platform", "shop", ResourceKinds.Container);

        Assert.True(_visibility.CanView(Caller("deploy", "carol"), ResourceKinds.App, "shop"));
        Assert.False(_visibility.CanView(Caller("deploy", "carol"), ResourceKinds.Container, "shop"));
    }

    /// <summary>A row whose identity cannot be worked out is omitted, because there
    /// is no way to tell whose it is.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ARowWithNoDerivableIdIsHiddenFromANonAdmin(string? resourceId)
    {
        Assert.False(_visibility.CanView(Caller("deploy", "carol"), Kind, resourceId));
    }

    // ---- filtering a list ---------------------------------------------------

    private static readonly string[] Rows = ["shop", "billing", "internal"];

    [Fact]
    public void FilteringDropsOnlyWhatAnotherTeamClaimed()
    {
        Team("platform", "alice");
        Team("payments", "carol");
        Grant("platform", "shop");
        Grant("payments", "billing");

        Assert.Equal(
            ["billing", "internal"],
            _visibility.Visible(Caller("deploy", "carol"), Kind, Rows, row => row));
    }

    [Fact]
    public void FilteringKeepsEverythingWhenNothingIsClaimed()
    {
        Assert.Equal(Rows, _visibility.Visible(Caller("deploy", "carol"), Kind, Rows, row => row));
    }

    /// <summary>
    /// An admin short-circuits to the whole list untouched — including a row too
    /// malformed to identify, which the fail-closed rule would otherwise drop from
    /// the one person meant to see everything and fix it.
    /// </summary>
    [Fact]
    public void AnAdminGetsTheListUntouchedIncludingUnidentifiableRows()
    {
        Team("platform", "alice");
        Grant("platform", "shop");

        Assert.Equal(Rows, _visibility.Visible(Caller("admin", "boss"), Kind, Rows, _ => null));
    }

    [Fact]
    public void ANonAdminLosesRowsThatCannotBeIdentified()
    {
        Assert.Empty(_visibility.Visible(Caller("deploy", "carol"), Kind, Rows, _ => null));
    }
}
