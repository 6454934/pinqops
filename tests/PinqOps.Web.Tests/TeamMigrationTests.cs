using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// The day teams appear on an existing install. The whole point is that nothing
/// changes: everyone lands in one team, nothing is granted to it, and because a
/// resource with no grant behaves exactly as it did before, no listing and no
/// action moves.
/// </summary>
public class TeamMigrationTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;
    private readonly TeamStore _store;

    public TeamMigrationTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-migration-").FullName;
        _path = Path.Combine(_directory, "teams.json");
        _store = new TeamStore(_path);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static UserAccount User(string username, string role) =>
        new() { Username = username, PasswordHash = "x", Role = role };

    private static readonly UserAccount[] ExistingUsers =
    [
        User("boss", UserRoles.Admin),
        User("deployer1", UserRoles.Deployer),
        User("viewer1", UserRoles.Viewer),
    ];

    [Fact]
    public void EveryExistingUserLandsInTheDefaultTeam()
    {
        Assert.True(_store.SeedDefaultTeam(ExistingUsers));

        var team = Assert.Single(_store.Teams);
        Assert.Equal(TeamStore.DefaultTeamId, team.Id);
        Assert.Equal(
            ["boss", "deployer1", "viewer1"],
            team.Members.Select(member => member.Principal).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void GlobalAdminsBecomeTeamOwners()
    {
        _store.SeedDefaultTeam(ExistingUsers);

        var members = Assert.Single(_store.Teams).Members;
        Assert.Equal(TeamRoles.Owner, members.Single(member => member.Principal == "boss").Role);
        Assert.Equal(TeamRoles.Member, members.Single(member => member.Principal == "deployer1").Role);
        Assert.Equal(TeamRoles.Member, members.Single(member => member.Principal == "viewer1").Role);
    }

    /// <summary>
    /// The property that makes this safe to ship. A grant is what narrows anything,
    /// and the migration creates none — so the day teams appear, every listing and
    /// every action is exactly what it was.
    /// </summary>
    [Fact]
    public void NothingIsGranted()
    {
        _store.SeedDefaultTeam(ExistingUsers);

        Assert.Empty(_store.Grants);
    }

    [Fact]
    public void SeedingIsIdempotent()
    {
        Assert.True(_store.SeedDefaultTeam(ExistingUsers));
        Assert.False(_store.SeedDefaultTeam(ExistingUsers));

        Assert.Single(_store.Teams);
    }

    /// <summary>A team an operator deliberately deleted must not come back on the
    /// next restart.</summary>
    [Fact]
    public void ADeletedDefaultTeamIsNotRecreatedWhileOtherTeamsExist()
    {
        _store.SeedDefaultTeam(ExistingUsers);
        _store.Update<object?>(directory =>
        {
            directory.Teams.Add(new Team { Id = "platform", Name = "platform" });
            return null;
        });
        _store.RemoveTeam(TeamStore.DefaultTeamId);

        Assert.False(_store.SeedDefaultTeam(ExistingUsers));
        Assert.Equal(["platform"], _store.Teams.Select(team => team.Id));
    }

    /// <summary>
    /// A fresh install has no users until the setup code is redeemed. Seeding an
    /// empty team then would leave the first admin outside it.
    /// </summary>
    [Fact]
    public void AnInstallWithNoUsersGetsNoTeam()
    {
        Assert.False(_store.SeedDefaultTeam([]));

        Assert.Empty(_store.Teams);
    }

    [Fact]
    public void TheSeededTeamSurvivesARestart()
    {
        _store.SeedDefaultTeam(ExistingUsers);

        Assert.Equal([TeamStore.DefaultTeamId], new TeamStore(_path).TeamsOf("boss"));
    }
}

/// <summary>
/// Resolving the app a request targets, once apps can be scoped to a team.
/// </summary>
public class AppResolverVisibilityTests
{
    private static UiConfig Config(params string[] appIds) => new()
    {
        Apps =
        [
            .. appIds.Select(id => new AppConnection
            {
                Id = id,
                RepoUrl = $"https://github.com/acme/{id}",
                ComposeFile = $"/opt/pinqops/apps/{id}/docker-compose.yml",
                RunnerDirectory = $"/opt/pinqops/runners/{id}",
            }),
        ],
    };

    private static Func<AppConnection, bool> Only(params string[] visible) =>
        app => visible.Contains(app.Id, StringComparer.Ordinal);

    /// <summary>With no rule supplied, nothing changes — which is what every
    /// background caller and every install with no grants gets.</summary>
    [Fact]
    public void WithoutAVisibilityRuleTheFirstAppWins()
    {
        Assert.Equal("shop", AppResolver.Resolve(Config("shop", "billing"), null).Id);
    }

    /// <summary>
    /// The first <em>visible</em> app, not simply the first: a member of one team
    /// must not silently operate on another team's app because it sorts first.
    /// </summary>
    [Fact]
    public void WithoutAnIdTheFirstVisibleAppWins()
    {
        Assert.Equal("billing", AppResolver.Resolve(Config("shop", "billing"), null, Only("billing")).Id);
    }

    [Fact]
    public void AnIdTheCallerCanSeeResolves()
    {
        Assert.Equal("billing", AppResolver.Resolve(Config("shop", "billing"), "billing", Only("billing")).Id);
    }

    /// <summary>
    /// An app the caller cannot see is reported in exactly the words an id that
    /// does not exist gets. Distinguishing them would let anyone enumerate every app
    /// on the server by trying ids and reading which refusal came back.
    /// </summary>
    [Fact]
    public void AnAppTheCallerCannotSeeIsIndistinguishableFromOneThatDoesNotExist()
    {
        var config = Config("shop", "billing");

        var hidden = Assert.Throws<ArgumentException>(
            () => AppResolver.Resolve(config, "shop", Only("billing")));
        var missing = Assert.Throws<ArgumentException>(
            () => AppResolver.Resolve(config, "nonexistent", Only("billing")));

        Assert.Equal("Unknown app 'shop'.", hidden.Message);
        Assert.Equal("Unknown app 'nonexistent'.", missing.Message);
    }

    [Fact]
    public void ACallerWhoCanSeeNoAppIsToldToConnectOne()
    {
        Assert.Throws<InvalidOperationException>(
            () => AppResolver.Resolve(Config("shop"), null, Only("nothing")));
    }
}

public class RetiredPrincipalTests
{
    /// <summary>
    /// Records written when every token shared one principal name nobody who can
    /// authenticate now, so they resolve to unowned — admin-only, and therefore
    /// safe. They are labelled rather than rewritten: reinterpreting one would hand
    /// access to whoever the guess landed on.
    /// </summary>
    [Fact]
    public void TheSharedTokenPrincipalIsRecognisedAsRetired()
    {
        Assert.True(ApiTokenStore.IsRetiredPrincipal(ApiTokenStore.RetiredPrincipal));
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("token:abc123")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElseIsNotRetired(string? principal)
    {
        Assert.False(ApiTokenStore.IsRetiredPrincipal(principal));
    }

    /// <summary>A real token principal can never collide with it, because the
    /// username validator excludes ':'.</summary>
    [Fact]
    public void TheRetiredNameIsNotATokenPrincipal()
    {
        Assert.False(ApiTokenStore.IsTokenPrincipal(ApiTokenStore.RetiredPrincipal));
    }
}

public class TokenCreatorTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("pinqops-token-creator-").FullName;

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private ApiTokenStore Store() => new(Path.Combine(_directory, "tokens.json"));

    /// <summary>
    /// Recorded at creation because it can only be recorded then — who minted a
    /// token is not something that can be worked out afterwards.
    /// </summary>
    [Fact]
    public void ATokenRemembersWhoMintedIt()
    {
        var store = Store();

        var (token, _) = store.Create("agent", "deploy", DateTimeOffset.UtcNow, createdBy: "boss");

        Assert.Equal("boss", token.CreatedBy);
        Assert.Equal("boss", store.List().Single(t => t.Id == token.Id).CreatedBy);
    }

    /// <summary>
    /// Tokens minted before this existed have no creator, and empty is read as
    /// "unknown" rather than as anybody.
    /// </summary>
    [Fact]
    public void ATokenWithNoRecordedCreatorIsEmptyNotNull()
    {
        var (token, _) = Store().Create("agent", "read", DateTimeOffset.UtcNow);

        Assert.Equal(string.Empty, token.CreatedBy);
    }
}
