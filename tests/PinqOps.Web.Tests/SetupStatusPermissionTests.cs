using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PinqOps;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Tests.Web;

/// <summary>
/// The setup card, through the real <c>Program</c>, when GitHub refuses the
/// token.
///
/// <para>Listing runners was already wrapped so that a token without
/// Administration:read "must only degrade the runner row, not kill the card".
/// The repository check beside it was not, so a token that could not read the
/// repository answered the whole endpoint with <c>502</c> — taking down the
/// compose and runner rows too, which are read from this server and were never
/// in doubt. The operator lost the card and the reason at the same time.</para>
/// </summary>
[Collection(TestServerCollection.Name)]
public class SetupStatusPermissionTests : IAsyncLifetime
{
    private const string AppId = "acme-shop";
    private const string Password = "admin-password-01";

    private readonly string _directory =
        Directory.CreateTempSubdirectory("pinqops-setupstatus-").FullName;

    private Factory _factory = null!;
    private HttpClient _client = null!;
    private string _session = "";

    public async Task InitializeAsync()
    {
        var composeFile = Path.Combine(_directory, "apps", AppId, "docker-compose.yml");
        Environment.SetEnvironmentVariable("PINQOPS_UI_CONFIG", Path.Combine(_directory, "ui.json"));
        Environment.SetEnvironmentVariable("PINQOPS_AUDIT_LOG", Path.Combine(_directory, "audit.jsonl"));

        new UiConfigStore(Path.Combine(_directory, "ui.json")).Update(config =>
        {
            config.Pat = "ghp_test";
            config.Users.Add(new UserAccount
            {
                Username = "boss",
                PasswordHash = PasswordHasher.Hash(Password),
                Role = UserRoles.Admin,
            });
            config.Apps.Add(new AppConnection
            {
                Id = AppId,
                RepoUrl = "https://github.com/acme/shop",
                ComposeFile = composeFile,
                RunnerDirectory = Path.Combine(_directory, "runners", AppId),
            });
        });

        _factory = new Factory();
        _client = _factory.CreateClient();

        using var login = await _client.PostAsJsonAsync(
            "/api/auth/login", new { username = "boss", password = Password });
        login.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await login.Content.ReadAsStreamAsync());
        _session = document.RootElement.GetProperty("token").GetString()!;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that outlives the test run is not a test failure.
        }
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Status()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/setup/status?appId={AppId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session);
        using var response = await _client.SendAsync(request);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return (response.StatusCode, document.RootElement.Clone());
    }

    /// <summary>
    /// The card answers, rather than the request failing.
    /// </summary>
    [Fact]
    public async Task ATokenThatCannotReadTheRepositoryStillGetsACard()
    {
        var (status, body) = await Status();

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("configured").GetBoolean());
        Assert.Equal(AppId, body.GetProperty("appId").GetString());
    }

    /// <summary>
    /// The rows that do not depend on GitHub survive — they are read from this
    /// server, and a GitHub permission problem says nothing about them.
    /// </summary>
    [Fact]
    public async Task TheLocallyKnownRowsSurvive()
    {
        var (_, body) = await Status();

        Assert.False(body.GetProperty("composeExists").GetBoolean());
        Assert.False(body.GetProperty("runnerInstalled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("repo").ValueKind);
    }

    /// <summary>
    /// And the reason is on the card, in the form that names the missing
    /// permission — the operator has to be able to act on it.
    /// </summary>
    [Fact]
    public async Task TheReasonIsReportedAndNamesThePermission()
    {
        var (_, body) = await Status();

        var reason = body.GetProperty("repoError").GetString();

        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Contains("403", reason, StringComparison.Ordinal);
        Assert.Contains("Contents", reason, StringComparison.Ordinal);
    }

    /// <summary>Whatever the dashboard asks GitHub, GitHub refuses it.</summary>
    private sealed class ForbiddenHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(
                    """{"message":"Resource not accessible by personal access token"}"""),
            });
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<GitHubDashboardService>();
                services.AddSingleton(provider => new GitHubDashboardService(
                    provider.GetRequiredService<UiConfigStore>(),
                    new HttpClient(new ForbiddenHandler())));
            });
        }
    }
}
