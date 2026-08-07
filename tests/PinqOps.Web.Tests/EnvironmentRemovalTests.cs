using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// What a de-registered host leaves behind.
///
/// <para>Grants and ownership records are keyed by host id, and an id is a name an
/// operator chooses — <c>prod</c>, <c>staging</c>, the obvious one. Rebuilding a
/// server and registering it under the name it had is the ordinary thing to do. If
/// what the old host's records said survives that, the new machine's containers
/// arrive already belonging to whoever held the old ones, with nobody having granted
/// anything and nothing to look at that would say so.</para>
///
/// <para>Deleting a team already takes its grants with it in the same write, on
/// exactly this reasoning. The host path did not.</para>
/// </summary>
public class EnvironmentRemovalTests : IDisposable
{
    private const string Host = "prod";
    private const string Container = "payments-api";

    private readonly string _directory;
    private readonly TeamStore _teams;
    private readonly ContainerOwnershipStore _ownership;

    public EnvironmentRemovalTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-environment-removal-").FullName;
        _teams = new TeamStore(Path.Combine(_directory, "teams.json"));
        _ownership = new ContainerOwnershipStore(Path.Combine(_directory, "container-owners.json"));

        _teams.Update<object?>(directory =>
        {
            directory.Teams.Add(new Team { Id = "payments", Name = "Payments" });
            directory.Grants.Add(new ResourceGrant
            {
                Kind = ResourceKinds.Container,
                EnvironmentId = Host,
                ResourceId = Container,
                TeamId = "payments",
                Access = GrantAccess.Manage,
                GrantedBy = "boss",
            });
            directory.Grants.Add(new ResourceGrant
            {
                Kind = ResourceKinds.Container,
                EnvironmentId = ManagedEnvironment.LocalId,
                ResourceId = Container,
                TeamId = "payments",
                Access = GrantAccess.Manage,
                GrantedBy = "boss",
            });
            return null;
        });

        _ownership.Set(Host, Container, "carol", ContainerOwnershipStore.AccessPrivate);
        _ownership.Set(ManagedEnvironment.LocalId, Container, "carol", ContainerOwnershipStore.AccessPrivate);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The grant does not outlive the host it was written for, so the next server to
    /// carry that name starts unclaimed.
    /// </summary>
    [Fact]
    public void AGrantOnADeRegisteredHostDoesNotSurviveIt()
    {
        Assert.Equal(1, _teams.RemoveEnvironment(Host));

        Assert.Empty(_teams.GrantsFor(ResourceKinds.Container, Host, Container));
    }

    /// <summary>And neither does the personal ownership record.</summary>
    [Fact]
    public void AnOwnershipRecordOnADeRegisteredHostDoesNotSurviveIt()
    {
        Assert.Equal(1, _ownership.RemoveEnvironment(Host));

        Assert.Null(_ownership.Get(Host, Container));
    }

    /// <summary>
    /// And nothing on any other host moves. The two records here name the same
    /// container on two machines, which is exactly the case the key was given an
    /// environment for.
    /// </summary>
    [Fact]
    public void TheSameNameOnAnotherHostIsLeftAlone()
    {
        _teams.RemoveEnvironment(Host);
        _ownership.RemoveEnvironment(Host);

        Assert.Single(_teams.GrantsFor(ResourceKinds.Container, ManagedEnvironment.LocalId, Container));
        Assert.NotNull(_ownership.Get(ManagedEnvironment.LocalId, Container));
    }

    /// <summary>Removing a host that granted nothing is not an error.</summary>
    [Fact]
    public void AHostWithNothingRecordedAgainstItRemovesCleanly()
    {
        Assert.Equal(0, _teams.RemoveEnvironment("never-registered"));
        Assert.Equal(0, _ownership.RemoveEnvironment("never-registered"));
    }
}
