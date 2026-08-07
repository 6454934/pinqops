using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PinqOps;
using PinqOps.Secrets;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Tests.Web;

/// <summary>
/// What a secret may be scoped to, through the real <c>Program</c>.
///
/// <para>An app-scoped secret only ever reaches a container because that app's
/// <c>.env</c> is rewritten from the store, so one filed under an id nobody has
/// connected is stored, listed and revealed while never reaching anything — a
/// credential that looks deployed and is not. Creating one is checked. Rotating one
/// was not, and rotation creates when nothing is there, so the check could be walked
/// straight past by naming a scope that does not exist.</para>
/// </summary>
[Collection(TestServerCollection.Name)]
public sealed class SecretScopeEndpointTests : IClassFixture<SecretScopeEndpointTests.AppFixture>
{
    private const string UnknownScope = "ghost-app";

    private readonly AppFixture _app;

    public SecretScopeEndpointTests(AppFixture app) => _app = app;

    [Fact]
    public async Task CreatingASecretForAnAppNobodyConnectedIsRefused()
    {
        var (status, body) = await _app.Create(UnknownScope, "CREATED_KEY", "value-01");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("is not a connected app", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same answer from the other route. Rotation is a write like any other and
    /// creates the secret when none exists, so it needs the same check — otherwise
    /// the refusal above is only the front door.
    /// </summary>
    [Fact]
    public async Task RotatingASecretForAnAppNobodyConnectedIsRefused()
    {
        var (status, body) = await _app.Rotate(UnknownScope, "ROTATED_KEY");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("is not a connected app", body, StringComparison.Ordinal);
        Assert.DoesNotContain(UnknownScope, await _app.Scopes(), StringComparer.Ordinal);
    }

    [Fact]
    public async Task RotatingAGlobalSecretStillWorks()
    {
        Assert.Equal(HttpStatusCode.OK, (await _app.Rotate(SecretScopes.Global, "GLOBAL_KEY")).Status);
        Assert.Contains(SecretScopes.Global, await _app.Scopes(), StringComparer.Ordinal);
    }

    [Fact]
    public async Task RotatingASecretOnAConnectedAppStillWorks()
    {
        Assert.Equal(HttpStatusCode.OK, (await _app.Rotate(AppFixture.AppId, "APP_KEY")).Status);
        Assert.Contains(AppFixture.AppId, await _app.Scopes(), StringComparer.Ordinal);
    }

    /// <summary>
    /// The store lowercases a scope before filing it, so a rotate that took the
    /// route value verbatim would have written a second, separate secret under the
    /// caller's casing. Going through the check normalises it the same way the
    /// create route does.
    /// </summary>
    [Fact]
    public async Task ARotateThatShoutsTheAppIdLandsOnTheSameScope()
    {
        Assert.Equal(HttpStatusCode.OK, (await _app.Rotate(AppFixture.AppId.ToUpperInvariant(), "SHOUTED_KEY")).Status);

        var scopes = await _app.Scopes();
        Assert.Contains(AppFixture.AppId, scopes, StringComparer.Ordinal);
        Assert.DoesNotContain(AppFixture.AppId.ToUpperInvariant(), scopes, StringComparer.Ordinal);
    }

    public sealed class AppFixture : WebApplicationFactory<Program>, IAsyncLifetime
    {
        public const string AppId = "acme-shop";

        private readonly string _directory;

        public HttpClient Client { get; private set; } = null!;

        public string AdminSession { get; private set; } = "";

        public AppFixture()
        {
            _directory = Path.Combine(Path.GetTempPath(), "pinqops-secret-scope-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Environment.SetEnvironmentVariable("PINQOPS_UI_CONFIG", Path.Combine(_directory, "ui.json"));
            Environment.SetEnvironmentVariable("PINQOPS_AUDIT_LOG", Path.Combine(_directory, "audit.jsonl"));

            new UiConfigStore(Path.Combine(_directory, "ui.json")).Update(config =>
            {
                config.Users.Add(new UserAccount
                {
                    Username = "boss",
                    PasswordHash = PasswordHasher.Hash("admin-password-01"),
                    Role = UserRoles.Admin,
                });
                config.Apps.Add(new AppConnection
                {
                    Id = AppId,
                    RepoUrl = "https://github.com/acme/shop",
                    ComposeFile = Path.Combine(_directory, "apps", AppId, "docker-compose.yml"),
                    RunnerDirectory = Path.Combine(_directory, "runners", AppId),
                });
            });
        }

        public async Task InitializeAsync()
        {
            Client = CreateClient();
            using var response = await Client.PostAsJsonAsync(
                "/api/auth/login", new { username = "boss", password = "admin-password-01" });
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            AdminSession = document.RootElement.GetProperty("token").GetString()!;
        }

        public Task<(HttpStatusCode Status, string Body)> Create(string scope, string name, string value) =>
            Send(HttpMethod.Post, "/api/secrets", new { scope, name, value });

        public Task<(HttpStatusCode Status, string Body)> Rotate(string scope, string name) =>
            Send(HttpMethod.Post, $"/api/secrets/{scope}/{name}/rotate", body: null);

        /// <summary>The scope of every secret the store holds.</summary>
        public async Task<IReadOnlyList<string>> Scopes()
        {
            var (_, body) = await Send(HttpMethod.Get, "/api/secrets", body: null);
            using var document = JsonDocument.Parse(body);
            return [.. document.RootElement.GetProperty("items").EnumerateArray()
                .Select(row => row.GetProperty("scope").GetString() ?? string.Empty)];
        }

        private async Task<(HttpStatusCode Status, string Body)> Send(HttpMethod method, string path, object? body)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminSession);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            using var response = await Client.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
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
