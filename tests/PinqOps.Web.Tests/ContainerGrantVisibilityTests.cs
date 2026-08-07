using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// A grant on a container has to reach the page, not just the gate.
///
/// <para>Two things decide whether an operator may act on a container: the personal
/// ownership record, and a manage grant held by a team they are in. The gate has
/// consulted both since teams arrived; the map the dashboard uses to decide which
/// buttons to draw reported only the first. So a container reachable solely through
/// a grant listed for its team with every action missing — the API would have allowed
/// each one, and the page never offered it. The grant looked applied and did nothing
/// anybody could see.</para>
/// </summary>
public class ContainerGrantVisibilityTests : IDisposable
{
    private const string Granted = "payments-api";
    private const string Team = "payments";
    private const string Member = "carol";
    private const string Outsider = "dave";

    private readonly string _directory;
    private readonly TeamStore _teams;

    public ContainerGrantVisibilityTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-container-grants-").FullName;
        _teams = new TeamStore(Path.Combine(_directory, "teams.json"));
        _teams.Update<object?>(directory =>
        {
            var team = new Team { Id = Team, Name = "Payments" };
            team.Members.Add(new TeamMember { Principal = Member, Role = TeamRoles.Owner });
            directory.Teams.Add(team);
            directory.Grants.Add(Grant(Granted, GrantAccess.Manage));
            return null;
        });
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static ResourceGrant Grant(string container, string access) => new()
    {
        Kind = ResourceKinds.Container,
        EnvironmentId = ManagedEnvironment.LocalId,
        ResourceId = container,
        TeamId = Team,
        Access = access,
        GrantedBy = "boss",
    };

    private IReadOnlyList<string> ManagedBy(string user) =>
        DockerEndpoints.ContainersManagedByGrant(_teams, ManagedEnvironment.LocalId, user);

    [Fact]
    public void AMemberOfTheGrantedTeamIsToldTheyCanManageIt()
    {
        Assert.Contains(Granted, ManagedBy(Member));
    }

    [Fact]
    public void SomebodyOutsideThatTeamIsNot()
    {
        Assert.Empty(ManagedBy(Outsider));
    }

    /// <summary>
    /// A view grant is not a manage grant. Reporting one as manageable would draw
    /// buttons the API then refuses — worse than drawing none, because the operator
    /// learns the refusal by pressing Remove.
    /// </summary>
    [Fact]
    public void AViewGrantDoesNotOfferTheActions()
    {
        _teams.Update<object?>(directory =>
        {
            directory.Grants.Clear();
            directory.Grants.Add(Grant(Granted, GrantAccess.View));
            return null;
        });

        Assert.Empty(ManagedBy(Member));
    }

    /// <summary>
    /// The grant belongs to one host. The same container name on another server is a
    /// different container, which is the whole reason the key carries the environment.
    /// </summary>
    [Fact]
    public void AGrantOnOneHostDoesNotReachTheSameNameOnAnother()
    {
        Assert.Empty(DockerEndpoints.ContainersManagedByGrant(_teams, "prod", Member));
    }

    /// <summary>And the page has to consult it, or the server's answer goes nowhere.</summary>
    [Fact]
    public void ThePageAsksWhetherAGrantLetsItManage()
    {
        var body = DashboardSource.FunctionBody("function ctCanManage(");

        Assert.Contains("o.manage", body, StringComparison.Ordinal);
    }
}
