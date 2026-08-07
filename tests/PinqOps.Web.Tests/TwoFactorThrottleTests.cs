using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Tests.Web;

/// <summary>
/// The two routes that verify a second factor outside sign-in: taking the factor
/// off, and replacing the recovery codes.
///
/// <para>Six digits is a million combinations, and three of them are current in any
/// 30-second step. The sign-in step has always been throttled for that reason; these
/// two were not throttled at all. A borrowed session — the unlocked laptop the
/// disable route's own comment names — could therefore work through the space at no
/// cost, and a session token is not bound to an address, so the global per-address
/// request limiter is not a ceiling either. The end of that is an account with its
/// second factor removed. Replacing the recovery codes is worse per guess: each
/// wrong one still costs the server ten PBKDF2 hashes.</para>
///
/// <para><b>A server per test, not per class.</b> A throttle is state that is
/// meant to survive: a success forgives the account it verified and deliberately
/// leaves the client's own counter alone, so one test that deliberately locks a
/// bucket would lock every test after it. Sharing a fixture here would make these
/// pass or fail on the order xUnit happened to pick.</para>
/// </summary>
[Collection(TestServerCollection.Name)]
public sealed class TwoFactorThrottleTests : TwoFactorLoginTestBase, IAsyncLifetime
{
    /// <summary>What <see cref="LoginThrottle"/> allows before it locks a bucket.</summary>
    private const int AllowedAttempts = 5;

    public TwoFactorThrottleTests()
        : base(new Fixture())
    {
    }

    private sealed class Fixture : TwoFactorServerFixture;

    public Task InitializeAsync() => App.InitializeAsync();

    public Task DisposeAsync() => App.DisposeAsync();

    private string SignedIn() =>
        App.Services.GetRequiredService<SessionStore>()
            .Create(TwoFactorServerFixture.Account, UserRoles.Admin);

    private async Task<HttpStatusCode> PostWithCodeAsync(string path, string token, string? code)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new { code });

        using var response = await App.Client.SendAsync(request);
        return response.StatusCode;
    }

    /// <summary>
    /// Guessing has to stop costing nothing. The refusal before the lock is 403 —
    /// the caller is signed in, the second factor is what was refused — and the one
    /// after it is 429, which is the answer that says the guessing itself was
    /// noticed.
    /// </summary>
    [Fact]
    public async Task GuessingAtTheDisableCodeIsLockedOut()
    {
        Enrol();
        var token = SignedIn();

        for (var attempt = 0; attempt < AllowedAttempts; attempt++)
        {
            Assert.Equal(HttpStatusCode.Forbidden, await PostWithCodeAsync("/api/2fa/disable", token, "000000"));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await PostWithCodeAsync("/api/2fa/disable", token, "000000"));
    }

    /// <summary>
    /// And the lock holds against a code that would otherwise be accepted, or it
    /// would only be slowing the guessing down between correct answers.
    /// </summary>
    [Fact]
    public async Task TheLockHoldsEvenForACurrentCode()
    {
        var secret = Enrol();
        var token = SignedIn();

        for (var attempt = 0; attempt <= AllowedAttempts; attempt++)
        {
            await PostWithCodeAsync("/api/2fa/disable", token, "000000");
        }

        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            await PostWithCodeAsync("/api/2fa/disable", token, CodeFor(secret, 1)));
    }

    /// <summary>
    /// Replacing the recovery codes verifies a code the same way and so needs the
    /// same ceiling — and it hashes ten new codes with PBKDF2 on the way, so an
    /// unthrottled wrong guess is a way to spend the server's CPU as well.
    /// </summary>
    [Fact]
    public async Task GuessingAtTheRecoveryCodeRouteIsLockedOutToo()
    {
        Enrol();
        var token = SignedIn();

        for (var attempt = 0; attempt < AllowedAttempts; attempt++)
        {
            await PostWithCodeAsync("/api/2fa/recovery-codes", token, "000000");
        }

        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            await PostWithCodeAsync("/api/2fa/recovery-codes", token, "000000"));
    }

    /// <summary>
    /// A correct code still works, and clears what the mistyped ones counted — a
    /// throttle that made an operator wait after they had proved who they were would
    /// be a lockout rather than a brake.
    /// </summary>
    [Fact]
    public async Task ACorrectCodeStillWorksAndForgivesTheMisses()
    {
        var secret = Enrol();
        var token = SignedIn();

        await PostWithCodeAsync("/api/2fa/disable", token, "000000");
        await PostWithCodeAsync("/api/2fa/disable", token, "000000");

        Assert.Equal(HttpStatusCode.OK, await PostWithCodeAsync("/api/2fa/disable", token, CodeFor(secret, 1)));

        // Enrolled again, and the account's counter is back to zero: three more
        // misses would have locked it if the success had not forgiven the two.
        var again = Enrol();
        await PostWithCodeAsync("/api/2fa/disable", token, "000000");
        await PostWithCodeAsync("/api/2fa/disable", token, "000000");
        Assert.Equal(HttpStatusCode.OK, await PostWithCodeAsync("/api/2fa/disable", token, CodeFor(again, 1)));
    }
}
