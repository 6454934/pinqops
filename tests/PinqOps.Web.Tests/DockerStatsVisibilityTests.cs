using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// The load table has to hide what the container table hides.
///
/// <para>The listing was filtered by team so a container one team has claimed stops
/// appearing for everyone else. The stats beside it were not, and the dashboard
/// fetches both onto the same page — so the names the listing had just been changed
/// to withhold came back anyway, with their CPU, memory, network and process counts
/// next to them, to anyone who could sign in.</para>
///
/// <para>The trap in fixing it is that the two rows do not name the container the
/// same way: <c>docker ps</c> reports <c>Names</c>, plural and comma-joined, and
/// <c>docker stats</c> reports one <c>Name</c>. Reusing the listing's reader here
/// finds nothing, and a row whose identity cannot be worked out is dropped — so the
/// bug would be replaced by an empty table for every non-admin.</para>
/// </summary>
public class DockerStatsVisibilityTests : IDisposable
{
    private const string Claimed = "payments-api";
    private const string Unclaimed = "shop-web";

    private readonly string _directory;
    private readonly TeamStore _teams;

    public DockerStatsVisibilityTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-stats-visibility-").FullName;
        _teams = new TeamStore(Path.Combine(_directory, "teams.json"));
        _teams.Update<object?>(directory =>
        {
            var team = new Team { Id = "payments", Name = "Payments" };
            team.Members.Add(new TeamMember { Principal = "carol", Role = TeamRoles.Owner });
            directory.Teams.Add(team);
            directory.Grants.Add(new ResourceGrant
            {
                Kind = ResourceKinds.Container,
                EnvironmentId = ManagedEnvironment.LocalId,
                ResourceId = Claimed,
                TeamId = "payments",
                Access = GrantAccess.Manage,
                GrantedBy = "boss",
            });
            return null;
        });
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>A <c>docker stats --format "{{json .}}"</c> row, as docker emits it.</summary>
    private static JsonElement StatsRow(string name) => JsonDocument.Parse(
        $$"""
        {"BlockIO":"0B / 0B","CPUPerc":"0.15%","Container":"9f2b1c","ID":"9f2b1c","MemPerc":"1.20%",
         "MemUsage":"48MiB / 3.8GiB","Name":"{{name}}","NetIO":"1.2kB / 900B","PIDs":"7"}
        """).RootElement;

    private static HttpContext Caller(string scope, string user)
    {
        var context = new DefaultHttpContext();
        context.Items["scope"] = scope;
        context.Items["user"] = user;
        return context;
    }

    private IReadOnlyList<JsonElement> VisibleTo(HttpContext caller) =>
        new ResourceVisibility(_teams).Visible(
            caller, ResourceKinds.Container, [StatsRow(Claimed), StatsRow(Unclaimed)], DockerEndpoints.StatsName);

    /// <summary>
    /// Someone outside the team that claimed it gets its load no more than they get
    /// its name.
    /// </summary>
    [Fact]
    public void AClaimedContainersLoadIsNotShownToEveryoneElse()
    {
        var names = VisibleTo(Caller("deploy", "dave")).Select(DockerEndpoints.StatsName).ToList();

        Assert.DoesNotContain(Claimed, names);
        Assert.Contains(Unclaimed, names);
    }

    /// <summary>And a member of that team still sees it — the grant is not a ban.</summary>
    [Fact]
    public void AMemberOfTheTeamThatClaimedItStillSeesIt()
    {
        var names = VisibleTo(Caller("deploy", "carol")).Select(DockerEndpoints.StatsName).ToList();

        Assert.Contains(Claimed, names);
        Assert.Contains(Unclaimed, names);
    }

    /// <summary>
    /// The reader has to be the one for this row's shape. Handed a listing row it
    /// finds no name, and a row with no name is dropped — which would empty the
    /// table rather than filter it.
    /// </summary>
    [Fact]
    public void TheNameIsReadFromTheColumnStatsActuallyUses()
    {
        Assert.Equal(Claimed, DockerEndpoints.StatsName(StatsRow(Claimed)));
        Assert.Null(DockerEndpoints.StatsName(
            JsonDocument.Parse($$"""{"Names":"{{Claimed}}"}""").RootElement));
    }
}
