using System.Net.Http.Headers;
using System.Net.Http.Json;
using PinqOps.Web;
using Xunit;
using AppFixture = PinqOps.Tests.Web.ResourceVisibilityEndpointTests.AppFixture;

namespace PinqOps.Tests.Web;

/// <summary>
/// The environment listing, when the request names a host.
///
/// <para>The switcher's list is the one place an operator learns which hosts this
/// install reaches at all, so granting an environment to a team has to hold whether
/// or not the request that asks for the list happens to name one. These drive the
/// listing through the real <c>Program</c> with two remote hosts registered, one
/// held by the caller's team and one held by another, because that is the smallest
/// install where the two answers can differ.</para>
/// </summary>
[Collection(TestServerCollection.Name)]
public sealed class EnvironmentListingVisibilityTests : IClassFixture<AppFixture>, IAsyncLifetime
{
    /// <summary>The remote host the caller's own team holds.</summary>
    private const string HeldEnvironmentId = "staging";

    /// <summary>The remote host another team holds, which the caller must never learn of.</summary>
    private const string OtherTeamEnvironmentId = "prod";

    private const int SshPort = 22;

    private const string SshUser = "deploy";

    private readonly AppFixture _app;

    public EnvironmentListingVisibilityTests(AppFixture app) => _app = app;

    public async Task InitializeAsync()
    {
        await Register(HeldEnvironmentId, "Staging", "staging.internal");
        await Register(OtherTeamEnvironmentId, "Production", "prod.internal");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Naming a host must not disclose the hosts other teams hold.
    ///
    /// <para>A deployer whose team holds staging asks for the switcher's list and
    /// names staging while doing it. The answer used to carry production too — its
    /// id, its name, its transport, its read-only flag and whether a key and a
    /// pinned host key are stored for it — because each row was measured against
    /// the host the request selected rather than the host the grant was written
    /// against. The same caller asking without naming a host never saw it, so an
    /// install could look correctly partitioned and still hand the whole estate to
    /// anyone who typed <c>?env=</c>.</para>
    /// </summary>
    [Fact]
    public async Task NamingAnEnvironmentDoesNotDiscloseTheOnesOtherTeamsHold()
    {
        await ClaimBothEnvironments();
        try
        {
            var naming = await _app.Rows(
                $"/api/environments?env={HeldEnvironmentId}", "items", _app.DeployerSession);

            Assert.DoesNotContain(OtherTeamEnvironmentId, naming);
            Assert.Contains(HeldEnvironmentId, naming);

            // The two paths can never be allowed to diverge again: naming a host
            // the caller already holds decides which host the request runs
            // against, never which rows come back.
            var namingNone = await _app.Rows("/api/environments", "items", _app.DeployerSession);
            Assert.Equal(namingNone, naming);
        }
        finally
        {
            await ReleaseBothEnvironments();
        }
    }

    /// <summary>
    /// An admin still sees every host, named or not — the property that keeps a
    /// mis-grant repairable by the one person meant to repair it.
    /// </summary>
    [Fact]
    public async Task AnAdminStillSeesEveryEnvironment()
    {
        await ClaimBothEnvironments();
        try
        {
            var naming = await _app.Rows(
                $"/api/environments?env={HeldEnvironmentId}", "items", _app.AdminSession);

            Assert.Contains(ManagedEnvironment.LocalId, naming);
            Assert.Contains(HeldEnvironmentId, naming);
            Assert.Contains(OtherTeamEnvironmentId, naming);
        }
        finally
        {
            await ReleaseBothEnvironments();
        }
    }

    private async Task ClaimBothEnvironments()
    {
        await _app.Grant(ResourceKinds.Environment, HeldEnvironmentId, AppFixture.Team);
        await _app.Grant(ResourceKinds.Environment, OtherTeamEnvironmentId, AppFixture.OtherTeam);
    }

    private async Task ReleaseBothEnvironments()
    {
        await _app.Revoke(ResourceKinds.Environment, HeldEnvironmentId, AppFixture.Team);
        await _app.Revoke(ResourceKinds.Environment, OtherTeamEnvironmentId, AppFixture.OtherTeam);
    }

    /// <summary>
    /// Registers a remote host the way an admin does, so the listing under test is
    /// filled by the product's own write path rather than by a hand-built config.
    /// </summary>
    private async Task Register(string id, string name, string host)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/environments");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _app.AdminSession);
        request.Content = JsonContent.Create(new
        {
            id,
            name,
            transport = ManagedEnvironment.TransportSsh,
            host,
            user = SshUser,
            port = SshPort,
        });

        using var response = await _app.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }
}
