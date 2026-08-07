using System.Text.Json;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

public class TeamStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;
    private readonly TeamStore _store;

    public TeamStoreTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-team-tests").FullName;
        _path = Path.Combine(_directory, "teams.json");
        _store = new TeamStore(_path);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private void AddTeam(string id, params string[] members) =>
        _store.Update<object?>(directory =>
        {
            directory.Teams.Add(new Team
            {
                Id = id,
                Name = id,
                Members = [.. members.Select(member => new TeamMember { Principal = member })],
            });
            return null;
        });

    private void Grant(string teamId, string kind = ResourceKinds.App, string environmentId = "local", string resourceId = "shop") =>
        _store.Update<object?>(directory =>
        {
            directory.Grants.Add(new ResourceGrant
            {
                Kind = kind,
                EnvironmentId = environmentId,
                ResourceId = resourceId,
                TeamId = teamId,
            });
            return null;
        });

    // ---- membership ---------------------------------------------------------

    [Fact]
    public void APrincipalsTeamsAreFound()
    {
        AddTeam("platform", "alice", "bob");
        AddTeam("payments", "alice");

        Assert.Equal(["platform", "payments"], _store.TeamsOf("alice"));
        Assert.Equal(["platform"], _store.TeamsOf("bob"));
    }

    [Fact]
    public void APrincipalInNoTeamHasNone()
    {
        AddTeam("platform", "alice");

        Assert.Empty(_store.TeamsOf("carol"));
        Assert.Empty(_store.TeamsOf(null));
        Assert.Empty(_store.TeamsOf(""));
    }

    /// <summary>An API token is a principal in its own right, so it can be a member.</summary>
    [Fact]
    public void ATokenPrincipalCanBelongToATeam()
    {
        AddTeam("automation", "token:abc123");

        Assert.Equal(["automation"], _store.TeamsOf("token:abc123"));
    }

    // ---- grants -------------------------------------------------------------

    [Fact]
    public void AGrantIsFoundByItsFullIdentity()
    {
        AddTeam("platform");
        Grant("platform", ResourceKinds.App, "local", "shop");

        Assert.Single(_store.GrantsFor(ResourceKinds.App, "local", "shop"));
    }

    /// <summary>
    /// The environment is part of the identity. A container called postgres on
    /// staging and one on production are different resources, and a grant that
    /// ignored which host it meant would hand out production by way of staging.
    /// </summary>
    [Fact]
    public void AGrantOnOneEnvironmentDoesNotCoverAnother()
    {
        AddTeam("platform");
        Grant("platform", ResourceKinds.Container, "staging", "postgres");

        Assert.Single(_store.GrantsFor(ResourceKinds.Container, "staging", "postgres"));
        Assert.Empty(_store.GrantsFor(ResourceKinds.Container, "production", "postgres"));
    }

    [Fact]
    public void AGrantOfOneKindDoesNotCoverAnother()
    {
        AddTeam("platform");
        Grant("platform", ResourceKinds.App, "local", "shop");

        Assert.Empty(_store.GrantsFor(ResourceKinds.Container, "local", "shop"));
    }

    /// <summary>An unowned resource has no grants, which the gate reads as admin-only.</summary>
    [Fact]
    public void AResourceNobodyGrantedHasNoGrants()
    {
        AddTeam("platform");

        Assert.Empty(_store.GrantsFor(ResourceKinds.App, "local", "never-granted"));
    }

    // ---- cascade delete -----------------------------------------------------

    /// <summary>
    /// The reason teams and grants share one file. A grant left naming a deleted
    /// team would be inherited by a team of that id created later — access nobody
    /// granted.
    /// </summary>
    [Fact]
    public void DeletingATeamDeletesItsGrants()
    {
        AddTeam("platform", "alice");
        AddTeam("payments", "bob");
        Grant("platform", ResourceKinds.App, "local", "shop");
        Grant("payments", ResourceKinds.App, "local", "billing");

        Assert.True(_store.RemoveTeam("platform"));

        Assert.Empty(_store.GrantsFor(ResourceKinds.App, "local", "shop"));
        Assert.Single(_store.GrantsFor(ResourceKinds.App, "local", "billing"));
        Assert.Single(_store.Teams);
    }

    [Fact]
    public void ARecreatedTeamDoesNotInheritTheOldOnesGrants()
    {
        AddTeam("platform");
        Grant("platform", ResourceKinds.App, "local", "shop");
        _store.RemoveTeam("platform");

        AddTeam("platform");

        Assert.Empty(_store.GrantsFor(ResourceKinds.App, "local", "shop"));
    }

    [Fact]
    public void DeletingATeamThatIsNotThereReportsIt()
    {
        Assert.False(_store.RemoveTeam("nobody"));
    }

    // ---- what is ignored ----------------------------------------------------

    /// <summary>A grant naming a team that is not there is ignored, never resolved
    /// by name to something else.</summary>
    [Fact]
    public void AGrantNamingAnUnknownTeamIsIgnored()
    {
        Grant("ghost", ResourceKinds.App, "local", "shop");

        Assert.Empty(_store.GrantsFor(ResourceKinds.App, "local", "shop"));
    }

    [Fact]
    public void AGrantOfAnUnknownKindIsIgnored()
    {
        AddTeam("platform");
        Grant("platform", "notAKind", "local", "shop");

        Assert.Empty(_store.GrantsFor("notAKind", "local", "shop"));
    }

    // ---- storage ------------------------------------------------------------

    [Fact]
    public void ItRoundTripsThroughTheFile()
    {
        AddTeam("platform", "alice");
        Grant("platform", ResourceKinds.App, "local", "shop");

        var reopened = new TeamStore(_path);

        Assert.Equal(["platform"], reopened.TeamsOf("alice"));
        Assert.Single(reopened.GrantsFor(ResourceKinds.App, "local", "shop"));
    }

    [Fact]
    public void TheFileIsOwnerOnly()
    {
        AddTeam("platform");

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_path));
        }
    }

    /// <summary>
    /// Corrupt means "grants nobody anything" — every non-admin is refused while an
    /// admin still has everything and can repair it. This is read on the hot path,
    /// outside the API's exception filter, so it must not throw.
    /// </summary>
    [Fact]
    public void ACorruptFileLoadsEmptyRatherThanThrowing()
    {
        File.WriteAllText(_path, "{ this is not json");

        var store = new TeamStore(_path);

        Assert.Empty(store.Teams);
        Assert.Empty(store.Grants);
        Assert.Empty(store.TeamsOf("alice"));
        Assert.Empty(store.GrantsFor(ResourceKinds.App, "local", "shop"));
    }

    /// <summary>
    /// What an unreadable file actually means at the gate: everyone below admin is
    /// refused, and an admin still reaches everything and can repair it. Asserted
    /// through the decision rather than only the store, because "loads empty" is
    /// only safe if empty denies.
    /// </summary>
    [Fact]
    public void ACorruptFileRefusesEveryoneBelowAdmin()
    {
        AddTeam("platform", "alice");
        Grant("platform", ResourceKinds.App, "local", "shop");
        File.WriteAllText(_path, "{ this is not json");

        var store = new TeamStore(_path);
        var grants = store.GrantsFor(ResourceKinds.App, "local", "shop");

        Assert.False(ResourceAccess.CanAccess(
            "deploy", "alice", ResourceKinds.App, null, store.TeamsOf("alice"), grants, GrantAccess.Manage));
        Assert.True(ResourceAccess.CanAccess(
            "admin", "boss", ResourceKinds.App, null, store.TeamsOf("boss"), grants, GrantAccess.Manage));
    }

    // ---- concurrency --------------------------------------------------------

    /// <summary>
    /// Two writers that both loaded before either saved would lose one of the
    /// changes. Update does load-mutate-save under one lock, so every one lands.
    /// </summary>
    [Fact]
    public async Task ConcurrentWritesAllLand()
    {
        const int Writers = 24;

        await Task.WhenAll(Enumerable.Range(0, Writers).Select(index => Task.Run(() =>
            _store.Update<object?>(directory =>
            {
                directory.Teams.Add(new Team { Id = $"team-{index}", Name = $"team-{index}" });
                return null;
            }))));

        Assert.Equal(Writers, _store.Teams.Count);
        Assert.Equal(Writers, new TeamStore(_path).Teams.Count);
    }

    /// <summary>
    /// Readers hold a snapshot that is never mutated in place, so enumerating
    /// memberships while a writer is working cannot throw or observe half a change.
    /// </summary>
    [Fact]
    public async Task ReadersNeverSeeAHalfAppliedChange()
    {
        AddTeam("platform", "alice");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (var team in _store.Teams)
                {
                    // Touching the members is the part that would throw if a writer
                    // were mutating the same list.
                    _ = team.Members.Count;
                }

                _ = _store.TeamsOf("alice");
            }
        });

        var writer = Task.Run(() =>
        {
            var index = 0;
            while (!stop.IsCancellationRequested)
            {
                _store.Update<object?>(directory =>
                {
                    directory.Teams[0].Members.Add(new TeamMember { Principal = $"user-{index++}" });
                    return null;
                });
            }
        });

        var failure = await Record.ExceptionAsync(() => Task.WhenAll(reader, writer));
        Assert.Null(failure);
    }

    /// <summary>The callback mutates a clone, so a throw leaves the previous state
    /// in memory and on disk rather than a partly-applied one.</summary>
    [Fact]
    public void AFailedUpdateChangesNothing()
    {
        AddTeam("platform", "alice");

        Assert.Throws<InvalidOperationException>(() => _store.Update<object?>(directory =>
        {
            directory.Teams.Clear();
            throw new InvalidOperationException("no");
        }));

        Assert.Single(_store.Teams);
        Assert.Equal(["platform"], _store.TeamsOf("alice"));
    }

    [Fact]
    public void TheFileShapeIsTeamsAndGrants()
    {
        AddTeam("platform", "alice");
        Grant("platform", ResourceKinds.App, "local", "shop");

        using var document = JsonDocument.Parse(File.ReadAllText(_path));

        Assert.True(document.RootElement.TryGetProperty("teams", out _));
        Assert.True(document.RootElement.TryGetProperty("grants", out _));
    }
}

public class TeamModelTests
{
    [Theory]
    [InlineData("platform", "platform")]
    [InlineData("  Platform  ", "platform")]
    [InlineData("team-1", "team-1")]
    public void TeamIdsAreFoldedToLowercase(string input, string expected)
    {
        Assert.Equal(expected, TeamId.Normalize(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-leading-hyphen")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("has_underscore")]
    public void InvalidTeamIdsAreRefused(string input)
    {
        Assert.Throws<ArgumentException>(() => TeamId.Normalize(input));
    }

    [Fact]
    public void ATeamIdIsBounded()
    {
        Assert.Throws<ArgumentException>(() => TeamId.Normalize(new string('a', TeamId.MaximumLength + 1)));
    }

    /// <summary>An unrecognised access level is read as the lowest, never the
    /// highest — a hand-edited typo must not become an escalation.</summary>
    [Theory]
    [InlineData("manage", GrantAccess.Manage)]
    [InlineData("view", GrantAccess.View)]
    [InlineData("MANAGE", GrantAccess.View)]
    [InlineData("admin", GrantAccess.View)]
    [InlineData("", GrantAccess.View)]
    [InlineData(null, GrantAccess.View)]
    public void AccessNormalizesDownwards(string? input, string expected)
    {
        Assert.Equal(expected, GrantAccess.Normalize(input));
    }

    [Fact]
    public void ManageCoversViewButNotTheOtherWayRound()
    {
        Assert.True(GrantAccess.Satisfies(GrantAccess.Manage, GrantAccess.View));
        Assert.True(GrantAccess.Satisfies(GrantAccess.Manage, GrantAccess.Manage));
        Assert.True(GrantAccess.Satisfies(GrantAccess.View, GrantAccess.View));
        Assert.False(GrantAccess.Satisfies(GrantAccess.View, GrantAccess.Manage));
    }

    /// <summary>There is no team-level viewer: it would be exactly redundant with
    /// the global viewer role, which refuses every mutation before a team is read.</summary>
    [Theory]
    [InlineData("owner", TeamRoles.Owner)]
    [InlineData("member", TeamRoles.Member)]
    [InlineData("viewer", TeamRoles.Member)]
    [InlineData(null, TeamRoles.Member)]
    public void TeamRolesNormalizeToMember(string? input, string expected)
    {
        Assert.Equal(expected, TeamRoles.Normalize(input));
    }

    /// <summary>
    /// The vocabulary is settled now so a new resource type is one entry rather than
    /// a decision. They are strings, not an enum, because they are written into a
    /// file that survives upgrades and an enum's ordinals would renumber.
    /// </summary>
    [Fact]
    public void EveryDeclaredKindIsKnownAndDistinct()
    {
        Assert.All(ResourceKinds.All, kind => Assert.True(ResourceKinds.IsKnown(kind)));
        Assert.Equal(ResourceKinds.All.Length, ResourceKinds.All.Distinct(StringComparer.Ordinal).Count());
        Assert.False(ResourceKinds.IsKnown("notAKind"));
        Assert.False(ResourceKinds.IsKnown(null));
    }
}
