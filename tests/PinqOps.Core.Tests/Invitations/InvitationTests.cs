using PinqOps.Invitations;
using Xunit;

namespace PinqOps.Tests.Invitations;

public class InvitationTokenTests
{
    [Fact]
    public void ALinkIsAnIdAndASecret()
    {
        var (token, _) = InvitationToken.New("a1b2c3d4");

        Assert.True(InvitationToken.TrySplit(token, out var id, out var secret));
        Assert.Equal("a1b2c3d4", id);
        Assert.Equal(InvitationToken.SecretBytes * 2, secret.Length);
    }

    [Fact]
    public void EveryLinkIsItsOwn() =>
        Assert.NotEqual(InvitationToken.New("a1").Token, InvitationToken.New("a1").Token);

    [Fact]
    public void TheSecretMatchesTheHashItWasIssuedWith()
    {
        var (token, hash) = InvitationToken.New("a1b2c3d4");
        InvitationToken.TrySplit(token, out _, out var secret);

        Assert.True(InvitationToken.Matches(secret, hash));
    }

    [Fact]
    public void AnotherSecretDoesNot()
    {
        var (_, hash) = InvitationToken.New("a1b2c3d4");
        InvitationToken.TrySplit(InvitationToken.New("a1b2c3d4").Token, out _, out var other);

        Assert.False(InvitationToken.Matches(other, hash));
    }

    /// <summary>A missing or truncated record must accept nothing, not everything.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void AnEmptyStoredHashMatchesNothing(string? stored) =>
        Assert.False(InvitationToken.Matches("abcdef", stored!));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-dot-here")]
    [InlineData(".secret")]
    [InlineData("id.")]
    [InlineData("nothex.abcdef")]
    [InlineData("abcdef.nothex")]
    public void SomethingThatIsNotALinkIsRefused(string? token) =>
        Assert.False(InvitationToken.TrySplit(token, out _, out _));

    /// <summary>
    /// The hash is what is stored, so the file must not contain the secret that
    /// opens it.
    /// </summary>
    [Fact]
    public void TheStoredHashIsNotTheSecret()
    {
        var (token, hash) = InvitationToken.New("a1b2c3d4");
        InvitationToken.TrySplit(token, out _, out var secret);

        Assert.DoesNotContain(secret, hash, StringComparison.OrdinalIgnoreCase);
    }
}

public class InvitationStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private static Invitation Pending() => new()
    {
        Id = "a1",
        Email = "new@example.com",
        Role = "viewer",
        CreatedAt = Now.AddHours(-1),
        ExpiresAt = Now.AddHours(1),
    };

    [Fact]
    public void AFreshOneIsPendingAndUsable()
    {
        Assert.Equal(InvitationStatus.Pending, Pending().StatusAt(Now));
        Assert.True(Pending().IsUsable(Now));
    }

    [Fact]
    public void PastItsExpiryItIsNeitherPendingNorUsable()
    {
        var invitation = Pending();
        invitation.ExpiresAt = Now.AddSeconds(-1);

        Assert.Equal(InvitationStatus.Expired, invitation.StatusAt(Now));
        Assert.False(invitation.IsUsable(Now));
    }

    [Fact]
    public void OnceAcceptedItIsSpent()
    {
        var invitation = Pending();
        invitation.AcceptedAt = Now;

        Assert.Equal(InvitationStatus.Accepted, invitation.StatusAt(Now));
        Assert.False(invitation.IsUsable(Now));
    }

    [Fact]
    public void OnceWithdrawnItIsRefused()
    {
        var invitation = Pending();
        invitation.RevokedAt = Now;

        Assert.Equal(InvitationStatus.Revoked, invitation.StatusAt(Now));
        Assert.False(invitation.IsUsable(Now));
    }

    /// <summary>
    /// What happened to it is what happened to it: an accepted invitation does not
    /// become "expired" a week later, because the list is a record of what people
    /// did rather than of what the clock says now.
    /// </summary>
    [Fact]
    public void AnAcceptedOneStaysAcceptedAfterItsExpiryPasses()
    {
        var invitation = Pending();
        invitation.AcceptedAt = Now;

        Assert.Equal(InvitationStatus.Accepted, invitation.StatusAt(Now.AddDays(30)));
    }
}

public class InvitationPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private static List<Invitation> SentBy(string actor, int count, TimeSpan ago) =>
        [.. Enumerable.Range(0, count).Select(_ => new Invitation { CreatedBy = actor, CreatedAt = Now - ago })];

    [Fact]
    public void SendingAFewIsFine() => Assert.Null(InvitationPolicy.CheckRate(SentBy("ada", 3, TimeSpan.Zero), "ada", Now));

    /// <summary>
    /// This endpoint is a way to make the server send mail to an address of the
    /// caller's choosing. Without a cap it is a way to make it send a lot of it.
    /// </summary>
    [Fact]
    public void SendingTooManyInAnHourIsRefused() =>
        Assert.NotNull(InvitationPolicy.CheckRate(
            SentBy("ada", InvitationPolicy.MaximumPerWindow, TimeSpan.Zero), "ada", Now));

    [Fact]
    public void TheWindowMovesOn() =>
        Assert.Null(InvitationPolicy.CheckRate(
            SentBy("ada", InvitationPolicy.MaximumPerWindow, TimeSpan.FromHours(2)), "ada", Now));

    /// <summary>One person hitting the cap must not stop everybody else.</summary>
    [Fact]
    public void ItIsCountedPerSender() =>
        Assert.Null(InvitationPolicy.CheckRate(
            SentBy("ada", InvitationPolicy.MaximumPerWindow, TimeSpan.Zero), "grace", Now));

    [Theory]
    [InlineData(null, InvitationPolicy.DefaultValidHours)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(24, 24)]
    [InlineData(100000, InvitationPolicy.MaximumValidHours)]
    public void ValidityIsClampedToSomethingSane(int? requested, int expected) =>
        Assert.Equal(expected, InvitationPolicy.ValidHours(requested));
}

public class InvitationStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory;
    private readonly InvitationStore _store;

    public InvitationStoreTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-invite-tests").FullName;
        _store = new InvitationStore(Path.Combine(_directory, "invitations.json"));
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private Invitation Add(string id, DateTimeOffset expiresAt, DateTimeOffset? acceptedAt = null)
    {
        var invitation = new Invitation
        {
            Id = id,
            Email = $"{id}@example.com",
            Role = "viewer",
            CreatedAt = Now.AddDays(-1),
            ExpiresAt = expiresAt,
            AcceptedAt = acceptedAt,
        };

        _store.Update<object?>(invitations => { invitations.Add(invitation); return null; });
        return invitation;
    }

    [Fact]
    public void WhatWasStoredComesBack()
    {
        Add("a1", Now.AddHours(1));

        Assert.Equal("a1@example.com", Assert.Single(_store.Load()).Email);
    }

    [Fact]
    public void AMissingFileMeansNoInvitations() =>
        Assert.Empty(new InvitationStore(Path.Combine(_directory, "absent.json")).Load());

    /// <summary>A corrupt file must mean "no invitations", never one that accepts anything.</summary>
    [Fact]
    public void ACorruptFileMeansNoInvitations()
    {
        var path = Path.Combine(_directory, "broken.json");
        File.WriteAllText(path, "{ not json");

        Assert.Empty(new InvitationStore(path).Load());
    }

    [Fact]
    public void FinishedOnesAreSweptOnceTheyAreOldEnough()
    {
        Add("old", Now.AddDays(-40), acceptedAt: Now.AddDays(-40));
        Add("recent", Now.AddDays(-1), acceptedAt: Now.AddDays(-1));
        Add("live", Now.AddHours(1));

        _store.Sweep(Now);

        Assert.Equal(["recent", "live"], _store.Load().Select(invitation => invitation.Id));
    }

    [Fact]
    public void EveryIdIsItsOwn() => Assert.NotEqual(InvitationStore.NewId(), InvitationStore.NewId());
}
