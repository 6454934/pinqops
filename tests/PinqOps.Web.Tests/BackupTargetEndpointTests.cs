using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Tests.Web;

/// <summary>
/// Adding a backup target, through the route the page actually posts to.
///
/// <para>There was no endpoint-level test here at all, and that is exactly how
/// docker-volume targets became uncreatable without anyone noticing: the two
/// tests that mention volumes both write a <c>BackupTarget</c> straight into the
/// config store, so they exercise everything downstream of the write boundary and
/// nothing at it.</para>
/// </summary>
[Collection(TestServerCollection.Name)]
public sealed class BackupTargetEndpointTests : IClassFixture<BackupTargetEndpointTests.AppFixture>
{
    private readonly AppFixture _app;

    public BackupTargetEndpointTests(AppFixture app) => _app = app;

    /// <summary>
    /// The exact payload the Backups page sends when an admin picks a volume from
    /// the picker and clicks Add: the option value is <c>volume|&lt;name&gt;|volume</c>,
    /// split into kind, name and engine.
    /// </summary>
    [Fact]
    public async Task AVolumeTargetCanBeAdded()
    {
        var (status, body) = await _app.AddTarget(new { kind = "volume", name = "shop-data", engine = "volume" });

        Assert.True(
            status == HttpStatusCode.OK,
            $"adding a volume target answered {(int)status}: {body}");
        Assert.Contains("vol-shop-data", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADatabaseTargetCanStillBeAdded()
    {
        var (status, body) = await _app.AddTarget(new { kind = "db", name = "pinqops-postgres", engine = "postgres" });

        Assert.True(status == HttpStatusCode.OK, $"adding a database target answered {(int)status}: {body}");
        Assert.Contains("db-postgres-pinqops-postgres", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// An engine with no dump plan is still refused — that check is why the volume
    /// case broke, and removing it altogether would let a target be scheduled that
    /// can never run.
    /// </summary>
    [Fact]
    public async Task AnEngineWithNoDumpPlanIsStillRefused()
    {
        var (status, _) = await _app.AddTarget(new { kind = "db", name = "pinqops-cassandra", engine = "cassandra" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task ANameDockerWouldRejectIsStillRefused()
    {
        var (status, _) = await _app.AddTarget(new { kind = "volume", name = "-bad name", engine = "volume" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    public sealed class AppFixture : WebApplicationFactory<Program>, IAsyncLifetime
    {
        private readonly string _directory;

        public HttpClient Client { get; private set; } = null!;

        public string AdminSession { get; private set; } = "";

        public AppFixture()
        {
            _directory = Path.Combine(Path.GetTempPath(), "pinqops-backup-targets-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Environment.SetEnvironmentVariable("PINQOPS_UI_CONFIG", Path.Combine(_directory, "ui.json"));
            Environment.SetEnvironmentVariable("PINQOPS_AUDIT_LOG", Path.Combine(_directory, "audit.jsonl"));

            new UiConfigStore(Path.Combine(_directory, "ui.json")).Update(config =>
                config.Users.Add(new UserAccount
                {
                    Username = "boss",
                    PasswordHash = PasswordHasher.Hash("admin-password-01"),
                    Role = UserRoles.Admin,
                }));
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

        /// <summary>The status and the raw body, so a failure says what the server objected to.</summary>
        public async Task<(HttpStatusCode Status, string Body)> AddTarget(object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/backups/targets");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminSession);
            request.Content = JsonContent.Create(body);

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
                // A test server's file handles can outlive the factory on Windows.
            }
        }
    }
}
