using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Tests.Web;

/// <summary>
/// How long an API token outlives the person who made it.
///
/// <para>Removing an account or lowering its role revoked its browser sessions and
/// stopped there. A token is a second credential the same person holds, it can be
/// minted at admin scope with no expiry, and it authenticates as a synthetic
/// principal with no account behind it — so nothing about losing the account touched
/// it. Someone removed from the server therefore kept indefinite admin over the whole
/// API, including resetting anybody's password and stripping anybody's second
/// factor.</para>
/// </summary>
[Collection(TestServerCollection.Name)]
public sealed class TokenLifetimeTests : IClassFixture<TokenLifetimeTests.AppFixture>
{
    private readonly AppFixture _app;

    public TokenLifetimeTests(AppFixture app) => _app = app;

    [Fact]
    public async Task ATokenStopsWorkingWhenItsCreatorIsRemoved()
    {
        var token = await _app.MintTokenAs(AppFixture.Second);
        Assert.Equal(HttpStatusCode.OK, await _app.Status("/api/me", token));

        await _app.AsOwner(HttpMethod.Delete, $"/api/users/{AppFixture.Second}", body: null);

        Assert.Equal(HttpStatusCode.Unauthorized, await _app.Status("/api/me", token));
    }

    /// <summary>
    /// Demotion too: the point of taking somebody's admin away is that they no
    /// longer have it, and a token minted at admin scope is admin.
    /// </summary>
    [Fact]
    public async Task ATokenStopsWorkingWhenItsCreatorIsDemoted()
    {
        var token = await _app.MintTokenAs(AppFixture.Third);
        Assert.Equal(HttpStatusCode.OK, await _app.Status("/api/me", token));

        await _app.AsOwner(HttpMethod.Post, $"/api/users/{AppFixture.Third}/role", new { role = UserRoles.Viewer });

        Assert.Equal(HttpStatusCode.Unauthorized, await _app.Status("/api/me", token));
    }

    /// <summary>
    /// Only that person's tokens. Removing one admin must not take the CI token
    /// another one minted down with it.
    /// </summary>
    [Fact]
    public async Task SomebodyElsesTokenIsLeftAlone()
    {
        var mine = await _app.MintTokenAs(AppFixture.Owner);
        var theirs = await _app.MintTokenAs(AppFixture.Fourth);

        await _app.AsOwner(HttpMethod.Delete, $"/api/users/{AppFixture.Fourth}", body: null);

        Assert.Equal(HttpStatusCode.Unauthorized, await _app.Status("/api/me", theirs));
        Assert.Equal(HttpStatusCode.OK, await _app.Status("/api/me", mine));
    }

    /// <summary>
    /// A username outlives its account, and a team membership is keyed by the name.
    ///
    /// <para>So a removed user's row stayed in the team directory, the name went back
    /// into the pool, and whoever took it next — an invitee the admin gave no team at
    /// all — inherited every grant that team holds. The same shape as the reserved
    /// principal an invitation could once claim: a name that means something after
    /// the thing it named is gone.</para>
    /// </summary>
    [Fact]
    public async Task ARemovedUsersTeamMembershipGoesWithThem()
    {
        await _app.AsOwner(HttpMethod.Post, "/api/teams", new { id = AppFixture.Team, name = AppFixture.Team });
        await _app.AsOwner(
            HttpMethod.Post, $"/api/teams/{AppFixture.Team}/members", new { principal = AppFixture.Fifth });
        Assert.Contains(AppFixture.Team, _app.TeamsOf(AppFixture.Fifth));

        await _app.AsOwner(HttpMethod.Delete, $"/api/users/{AppFixture.Fifth}", body: null);

        Assert.Empty(_app.TeamsOf(AppFixture.Fifth));
    }

    public sealed class AppFixture : WebApplicationFactory<Program>, IAsyncLifetime
    {
        public const string Owner = "boss";
        public const string Second = "bob";
        public const string Third = "carol";
        public const string Fourth = "dave";
        public const string Fifth = "erin";
        public const string Team = "platform";
        private const string Password = "admin-password-01";

        private readonly string _directory;

        public HttpClient Client { get; private set; } = null!;

        private string _ownerSession = "";

        public AppFixture()
        {
            _directory = Path.Combine(Path.GetTempPath(), "pinqops-token-life-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Environment.SetEnvironmentVariable("PINQOPS_UI_CONFIG", Path.Combine(_directory, "ui.json"));
            Environment.SetEnvironmentVariable("PINQOPS_AUDIT_LOG", Path.Combine(_directory, "audit.jsonl"));

            new UiConfigStore(Path.Combine(_directory, "ui.json")).Update(config =>
            {
                foreach (var name in (string[])[Owner, Second, Third, Fourth, Fifth])
                {
                    config.Users.Add(new UserAccount
                    {
                        Username = name,
                        PasswordHash = PasswordHasher.Hash(Password),
                        Role = UserRoles.Admin,
                    });
                }
            });
        }

        public async Task InitializeAsync()
        {
            Client = CreateClient();
            _ownerSession = await Login(Owner);
        }

        /// <summary>An admin-scoped token with no expiry, minted by <paramref name="username"/>.</summary>
        public async Task<string> MintTokenAs(string username)
        {
            var session = string.Equals(username, Owner, StringComparison.Ordinal) ? _ownerSession : await Login(username);

            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/tokens");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);
            request.Content = JsonContent.Create(new { name = $"ci-{username}", scope = "admin" });

            using var response = await Client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            return document.RootElement.GetProperty("token").GetString()!;
        }

        /// <summary>The teams a principal belongs to, read from the running server's own store.</summary>
        public IReadOnlyList<string> TeamsOf(string principal) =>
            Services.GetRequiredService<TeamStore>().TeamsOf(principal);

        public async Task<HttpStatusCode> Status(string path, string bearer)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var response = await Client.SendAsync(request);
            return response.StatusCode;
        }

        public async Task AsOwner(HttpMethod method, string path, object? body)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _ownerSession);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            using var response = await Client.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        private async Task<string> Login(string username)
        {
            using var response = await Client.PostAsJsonAsync("/api/auth/login", new { username, password = Password });
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
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
            }
        }
    }
}
