using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Tests.Web;

/// <summary>
/// Taking your own second factor off, which needs a current code — a borrowed
/// session should not be enough to strip the protection off the account it is
/// signed in to.
///
/// <para>What is checked here is the answer given when that code is wrong.
/// <c>401</c> means "you are not signed in", and the dashboard believes it: any
/// 401 outside the sign-in routes drops the operator to the lock screen and
/// throws the real message away. The caller <em>is</em> signed in — that is how
/// they reached the route — so a rejected second factor is <c>403</c>, which is
/// also what the sibling route answers, because it fails through the exception
/// filter rather than by naming a status itself.</para>
/// </summary>
[Collection(TestServerCollection.Name)]
public sealed class TwoFactorDisableTests : TwoFactorLoginTestBase, IClassFixture<TwoFactorDisableTests.Fixture>
{
    public TwoFactorDisableTests(Fixture app)
        : base(app)
    {
    }

    public sealed class Fixture : TwoFactorServerFixture;

    /// <summary>
    /// A session for the enrolled account, minted the way a completed sign-in mints
    /// one rather than by signing in. Signing in would spend a TOTP counter, and the
    /// acceptance window is one step either side of now — so enrolling, signing in
    /// and then disabling would need three distinct current codes where only two
    /// exist. What is under test is the answer the disable route gives, not the
    /// route that issued the token.
    /// </summary>
    private string SignedIn() =>
        App.Services.GetRequiredService<SessionStore>()
            .Create(TwoFactorServerFixture.Account, UserRoles.Admin);

    private async Task<(HttpStatusCode Status, string Body)> DisableAsync(string token, string? code)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/2fa/disable");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new { code });

        using var response = await App.Client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AWrongCodeIsRefusedWithoutEndingTheSession()
    {
        var secret = Enrol();
        var token = SignedIn();

        var (status, body) = await DisableAsync(token, "000000");

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("not right", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ABlankCodeIsRefusedTheSameWay()
    {
        var secret = Enrol();
        var token = SignedIn();

        var (status, _) = await DisableAsync(token, code: null);

        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    /// <summary>
    /// The session survives the refusal, which is the point: a mistyped code must
    /// leave the operator where they were, free to type the next one.
    /// </summary>
    [Fact]
    public async Task TheSessionStillWorksAfterAWrongCode()
    {
        var secret = Enrol();
        var token = SignedIn();
        await DisableAsync(token, "000000");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await App.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Taking a second factor off an account is exactly what an audit trail is
    /// for, and it left no line.
    ///
    /// <para>The cause is worth naming: the audit middleware decided what to
    /// record by asking the <em>authorization</em> classifier what scope a route
    /// needs, and every self-service write is deliberately classified at the read
    /// scope so a viewer can protect their own login. Two different questions, one
    /// answer — so lowering a route's scope silently stopped it being recorded.
    /// Changing a password went the same way.</para>
    /// </summary>
    [Fact]
    public async Task TakingTheSecondFactorOffIsRecorded()
    {
        var secret = Enrol();
        var token = SignedIn();

        var (status, _) = await DisableAsync(token, CodeFor(secret, 1));
        Assert.Equal(HttpStatusCode.OK, status);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/audit");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await App.Client.SendAsync(request);
        var trail = await response.Content.ReadAsStringAsync();

        Assert.Contains("/api/2fa/disable", trail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACurrentCodeTakesItOff()
    {
        var secret = Enrol();
        var token = SignedIn();

        // One step past the one that signed in. The counter only moves forward, so
        // replaying the sign-in code would be refused on those grounds rather than
        // on the ones under test — and the acceptance window is +/-1, so this is the
        // only code that is both unused and current.
        var (status, _) = await DisableAsync(token, CodeFor(secret, 1));

        Assert.Equal(HttpStatusCode.OK, status);
    }
}
