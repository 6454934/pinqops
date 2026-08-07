using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

public class SessionStoreTests
{
    [Fact]
    public void Create_ThenResolve_ReturnsTheIdentity()
    {
        var sessions = new SessionStore();

        var principal = sessions.Resolve(sessions.Create("alice", UserRoles.Deployer));

        Assert.NotNull(principal);
        Assert.Equal("alice", principal.Username);
        Assert.Equal(UserRoles.Deployer, principal.Role);
    }

    [Fact]
    public void Resolve_UnknownToken_IsNull() =>
        Assert.Null(new SessionStore().Resolve("nope"));

    [Fact]
    public void Revoke_EndsThatSessionOnly()
    {
        var sessions = new SessionStore();
        var first = sessions.Create("alice", UserRoles.Admin);
        var second = sessions.Create("alice", UserRoles.Admin);

        sessions.Revoke(first);

        Assert.Null(sessions.Resolve(first));
        Assert.NotNull(sessions.Resolve(second));
    }

    [Fact]
    public void RevokeUser_EndsEverySessionOfThatUser()
    {
        var sessions = new SessionStore();
        var alice = sessions.Create("alice", UserRoles.Admin);
        var bob = sessions.Create("bob", UserRoles.Viewer);

        sessions.RevokeUser("ALICE");

        Assert.Null(sessions.Resolve(alice));
        Assert.NotNull(sessions.Resolve(bob));
    }

    /// <summary>
    /// The cap used to be global, evicting the oldest session in the whole table.
    /// Anyone holding one valid login could then sign everyone else out simply by
    /// logging in repeatedly. Per-user, a busy account only evicts itself.
    /// </summary>
    [Fact]
    public void OneUsersSessions_CannotEvictAnother()
    {
        var sessions = new SessionStore();
        var victim = sessions.Create("victim", UserRoles.Admin);

        for (var attempt = 0; attempt < 64; attempt++)
        {
            sessions.Create("noisy", UserRoles.Viewer);
        }

        Assert.NotNull(sessions.Resolve(victim));
    }

    [Fact]
    public void AUsersOwnSessionsAreCappedOldestFirst()
    {
        var sessions = new SessionStore();
        var oldest = sessions.Create("alice", UserRoles.Admin);

        for (var attempt = 0; attempt < 32; attempt++)
        {
            sessions.Create("alice", UserRoles.Admin);
        }

        Assert.Null(sessions.Resolve(oldest));
    }
}
