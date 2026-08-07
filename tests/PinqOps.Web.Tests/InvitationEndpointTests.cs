using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PinqOps.Invitations;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Tests.Web;

/// <summary>
/// Inviting somebody, and what happens when they follow the link.
///
/// <para>The properties worth asserting are the ones a careless implementation
/// gets wrong: that the link works exactly once, that withdrawing it takes effect,
/// that it stops working when it expires, and that a wrong link is refused the
/// same way as an expired one — telling them apart tells somebody holding a guess
/// which half of it was right.</para>
/// </summary>
[Collection(TestServerCollection.Name)]
public sealed class InvitationEndpointTests : IClassFixture<InvitationEndpointTests.AppFixture>, IAsyncLifetime
{
    private readonly AppFixture _app;

    public InvitationEndpointTests(AppFixture app) => _app = app;

    public Task InitializeAsync()
    {
        // One clean slate per test: these share a server, and an invitation left
        // pending by one would count against another's rate limit.
        _app.Services.GetRequiredService<InvitationStore>().Update<object?>(invitations =>
        {
            invitations.Clear();
            return null;
        });

        _app.Services.GetRequiredService<UiConfigStore>().Update(config =>
            config.Users.RemoveAll(user => !string.Equals(user.Username, AppFixture.Admin, StringComparison.Ordinal)));

        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        HttpMethod method, string path, object? body = null, string? token = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await _app.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        return (response.StatusCode, document.RootElement.Clone());
    }

    /// <summary>Sends an invitation and returns the link out of the response.</summary>
    private async Task<(string Id, string Link)> InviteAsync(string email = "new@example.com", string role = "viewer", string? teamId = null)
    {
        var (status, body) = await SendAsync(
            HttpMethod.Post, "/api/invites", new { email, role, teamId }, _app.AdminSession);

        Assert.Equal(HttpStatusCode.OK, status);
        return (body.GetProperty("id").GetString()!, body.GetProperty("link").GetString()!);
    }

    /// <summary>The token out of a link, which is what the two anonymous routes take.</summary>
    private static string TokenOf(string link) =>
        Uri.UnescapeDataString(new Uri(link).Query.Split("invite=")[1]);

    // ---- sending ---------------------------------------------------------------

    [Fact]
    public async Task OnlyAnAdminMaySendOne()
    {
        var body = new { email = "new@example.com", role = "viewer" };

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/api/invites", body, _app.ViewerSession)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/api/invites", token: _app.ViewerSession)).Status);
    }

    /// <summary>
    /// The link comes back even when the relay is not set up, so an admin can pass
    /// it on by hand. An invitation that silently went nowhere would be worse than
    /// one that has to be copied.
    /// </summary>
    [Fact]
    public async Task TheLinkComesBackEvenWhenThereIsNoRelay()
    {
        var (_, body) = await SendAsync(
            HttpMethod.Post, "/api/invites", new { email = "new@example.com", role = "viewer" }, _app.AdminSession);

        Assert.Contains("?invite=", body.GetProperty("link").GetString()!, StringComparison.Ordinal);
        Assert.False(body.GetProperty("emailed").GetBoolean());
        Assert.False(body.GetProperty("mailProblem").ValueKind == JsonValueKind.Null);
    }

    [Theory]
    [InlineData("not-an-address", "viewer")]
    [InlineData("new@example.com", "superuser")]
    public async Task SomethingThatIsNotAnInvitationIsRefused(string email, string role) =>
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await SendAsync(HttpMethod.Post, "/api/invites", new { email, role }, _app.AdminSession)).Status);

    [Fact]
    public async Task AnUnknownTeamIsRefusedRatherThanSilentlyDropped() =>
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await SendAsync(
                HttpMethod.Post,
                "/api/invites",
                new { email = "new@example.com", role = "viewer", teamId = "no-such-team" },
                _app.AdminSession)).Status);

    [Fact]
    public async Task ItAppearsInTheListAsPending()
    {
        var (id, _) = await InviteAsync();

        var (_, body) = await SendAsync(HttpMethod.Get, "/api/invites", token: _app.AdminSession);
        var listed = body.GetProperty("items").EnumerateArray().Single();

        Assert.Equal(id, listed.GetProperty("id").GetString());
        Assert.Equal(InvitationStatus.Pending, listed.GetProperty("status").GetString());
        Assert.Equal(AppFixture.Admin, listed.GetProperty("createdBy").GetString());
    }

    /// <summary>The list is rendered from this, so it must not carry a working link.</summary>
    [Fact]
    public async Task TheListNeverCarriesTheLink()
    {
        var (_, link) = await InviteAsync();

        var (_, body) = await SendAsync(HttpMethod.Get, "/api/invites", token: _app.AdminSession);

        Assert.DoesNotContain(TokenOf(link), body.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // ---- accepting -------------------------------------------------------------

    [Fact]
    public async Task TheLinkSaysWhoItIsFor()
    {
        var (_, link) = await InviteAsync("ada@example.com", "deployer");

        var (status, body) = await SendAsync(HttpMethod.Get, $"/api/auth/invite?token={Uri.EscapeDataString(TokenOf(link))}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("ada@example.com", body.GetProperty("email").GetString());
        Assert.Equal("deployer", body.GetProperty("role").GetString());
    }

    [Fact]
    public async Task FollowingItCreatesTheAccountWithTheRoleTheInvitationSet()
    {
        var (_, link) = await InviteAsync(role: "deployer");

        var (status, body) = await SendAsync(
            HttpMethod.Post,
            "/api/auth/invite/accept",
            new { token = TokenOf(link), username = "ada", password = "a-long-enough-password" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("deployer", body.GetProperty("role").GetString());
        Assert.NotEmpty(body.GetProperty("token").GetString()!);

        var users = _app.Services.GetRequiredService<UiConfigStore>().Current.Users;
        Assert.Equal("deployer", users.Single(user => user.Username == "ada").Role);
    }

    /// <summary>
    /// The invitation decides the role. A body that asks for a better one has to
    /// change nothing — otherwise an invitation to be a viewer is an invitation to
    /// be an admin.
    /// </summary>
    [Fact]
    public async Task TheInviteeCannotChooseTheirOwnRole()
    {
        var (_, link) = await InviteAsync(role: "viewer");

        await SendAsync(
            HttpMethod.Post,
            "/api/auth/invite/accept",
            new { token = TokenOf(link), username = "ada", password = "a-long-enough-password", role = "admin" });

        var users = _app.Services.GetRequiredService<UiConfigStore>().Current.Users;
        Assert.Equal("viewer", users.Single(user => user.Username == "ada").Role);
    }

    [Fact]
    public async Task ItWorksExactlyOnce()
    {
        var (_, link) = await InviteAsync();
        var token = TokenOf(link);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(
            HttpMethod.Post, "/api/auth/invite/accept",
            new { token, username = "ada", password = "a-long-enough-password" })).Status);

        Assert.Equal(HttpStatusCode.Gone, (await SendAsync(
            HttpMethod.Post, "/api/auth/invite/accept",
            new { token, username = "grace", password = "a-long-enough-password" })).Status);
    }

    [Fact]
    public async Task WithdrawingItStopsTheLinkWorking()
    {
        var (id, link) = await InviteAsync();

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/invites/{id}", token: _app.AdminSession)).Status);

        Assert.Equal(HttpStatusCode.Gone, (await SendAsync(
            HttpMethod.Post, "/api/auth/invite/accept",
            new { token = TokenOf(link), username = "ada", password = "a-long-enough-password" })).Status);
    }

    [Fact]
    public async Task AnExpiredLinkStopsWorking()
    {
        var (id, link) = await InviteAsync();
        _app.Services.GetRequiredService<InvitationStore>().Update<object?>(invitations =>
        {
            invitations.Find(invitation => invitation.Id == id)!.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            return null;
        });

        Assert.Equal(HttpStatusCode.Gone, (await SendAsync(
            HttpMethod.Get, $"/api/auth/invite?token={Uri.EscapeDataString(TokenOf(link))}")).Status);
    }

    /// <summary>
    /// A made-up link, a link with the right id and the wrong secret, and one that
    /// never existed all answer the same way. Telling them apart tells somebody
    /// holding a guess which half of it was right.
    /// </summary>
    [Fact]
    public async Task AWrongLinkIsRefusedTheSameWayAsAnExpiredOne()
    {
        var (id, _) = await InviteAsync();
        var wrongSecret = $"{id}.{new string('a', 64)}";

        Assert.Equal(HttpStatusCode.Gone, (await SendAsync(HttpMethod.Get, $"/api/auth/invite?token={wrongSecret}")).Status);
        Assert.Equal(HttpStatusCode.Gone, (await SendAsync(HttpMethod.Get, "/api/auth/invite?token=deadbeef.abcdef")).Status);
        Assert.Equal(HttpStatusCode.Gone, (await SendAsync(HttpMethod.Get, "/api/auth/invite?token=nonsense")).Status);
    }

    /// <summary>
    /// A taken username must leave the invitation usable, so the same person can
    /// come back and pick another one.
    /// </summary>
    [Fact]
    public async Task ANameThatIsTakenDoesNotBurnTheInvitation()
    {
        var (_, link) = await InviteAsync();
        var token = TokenOf(link);

        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(
            HttpMethod.Post, "/api/auth/invite/accept",
            new { token, username = AppFixture.Admin, password = "a-long-enough-password" })).Status);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(
            HttpMethod.Post, "/api/auth/invite/accept",
            new { token, username = "ada", password = "a-long-enough-password" })).Status);
    }

    [Fact]
    public async Task AWeakPasswordIsRefusedAndTheInvitationSurvives()
    {
        var (_, link) = await InviteAsync();
        var token = TokenOf(link);

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(
            HttpMethod.Post, "/api/auth/invite/accept", new { token, username = "ada", password = "short" })).Status);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(
            HttpMethod.Get, $"/api/auth/invite?token={Uri.EscapeDataString(token)}")).Status);
    }

    [Fact]
    public async Task AnAcceptedInvitationCannotBeWithdrawn()
    {
        var (id, link) = await InviteAsync();
        await SendAsync(
            HttpMethod.Post, "/api/auth/invite/accept",
            new { token = TokenOf(link), username = "ada", password = "a-long-enough-password" });

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await SendAsync(HttpMethod.Delete, $"/api/invites/{id}", token: _app.AdminSession)).Status);
    }

    [Fact]
    public async Task TheAcceptedRowRecordsWhoTookIt()
    {
        var (id, link) = await InviteAsync();
        await SendAsync(
            HttpMethod.Post, "/api/auth/invite/accept",
            new { token = TokenOf(link), username = "ada", password = "a-long-enough-password" });

        var (_, body) = await SendAsync(HttpMethod.Get, "/api/invites", token: _app.AdminSession);
        var listed = body.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("id").GetString() == id);

        Assert.Equal(InvitationStatus.Accepted, listed.GetProperty("status").GetString());
        Assert.Equal("ada", listed.GetProperty("acceptedAs").GetString());
    }

    public sealed class AppFixture : WebApplicationFactory<Program>, IAsyncLifetime
    {
        internal const string Admin = "boss";

        private readonly string _directory;

        public HttpClient Client { get; private set; } = null!;

        public string AdminSession { get; private set; } = "";

        public string ViewerSession { get; private set; } = "";

        public AppFixture()
        {
            _directory = Path.Combine(Path.GetTempPath(), "pinqops-invites-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Environment.SetEnvironmentVariable("PINQOPS_UI_CONFIG", Path.Combine(_directory, "ui.json"));
            Environment.SetEnvironmentVariable("PINQOPS_AUDIT_LOG", Path.Combine(_directory, "audit.jsonl"));

            new UiConfigStore(Path.Combine(_directory, "ui.json")).Update(config =>
            {
                config.Users.Add(Account(Admin, "admin-password-01", UserRoles.Admin));
                config.Users.Add(Account("viewer1", "viewer-password-1", UserRoles.Viewer));
            });
        }

        private static UserAccount Account(string username, string password, string role) => new()
        {
            Username = username,
            PasswordHash = PasswordHasher.Hash(password),
            Role = role,
        };

        public async Task InitializeAsync()
        {
            Client = CreateClient();
            AdminSession = await Login(Admin, "admin-password-01");
            ViewerSession = await Login("viewer1", "viewer-password-1");
        }

        private async Task<string> Login(string username, string password)
        {
            using var response = await Client.PostAsJsonAsync("/api/auth/login", new { username, password });
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.GetProperty("token").GetString()!;
        }

        public new async Task DisposeAsync()
        {
            await base.DisposeAsync();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // A temp directory that will not go is not a test failure.
            }
        }
    }
}
