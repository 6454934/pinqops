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
using PinqOps.Web.Tests.Fakes;
using Xunit;

namespace PinqOps.Tests.Web;

/// <summary>
/// Secrets entered before an app is published still have to reach its first
/// deploy.
///
/// <para><see cref="SecretSyncService"/> skips an app whose compose directory
/// does not exist yet — an ordinary state for an app that is connected but not
/// published — and it only runs on a secret write. Creating the compose project
/// did not run it, so everything typed into the app's Secrets tab before
/// publishing stayed out of the <c>.env</c> until some later, unrelated secret
/// write happened to sync it.</para>
///
/// <para>The dashboard listed those secrets the whole time, and the container
/// came up without them. What made it hard to see is that the second secret
/// anyone added repaired the first one, so the state never survived long enough
/// to be reported.</para>
/// </summary>
[Collection(TestServerCollection.Name)]
public class ComposeCreationSeedsSecretsTests : IAsyncLifetime
{
    private const string AppId = "acme-shop";
    private const string Password = "admin-password-01";
    private const string SecretName = "DATABASE_URL";
    private const string SecretValue = "postgres://user:pw@db:5432/shop";

    private readonly string _directory =
        Directory.CreateTempSubdirectory("pinqops-composeseed-").FullName;

    private string _composeFile = "";
    private Factory _factory = null!;
    private HttpClient _client = null!;
    private string _session = "";

    private string EnvFile => Path.Combine(Path.GetDirectoryName(_composeFile)!, ".env");

    public async Task InitializeAsync()
    {
        _composeFile = Path.Combine(_directory, "apps", AppId, "docker-compose.yml");
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
                ComposeFile = _composeFile,
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
            // A leftover temp directory is not a test failure.
        }
    }

    private async Task<HttpStatusCode> Send(HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await _client.SendAsync(request);
        return response.StatusCode;
    }

    private Task<HttpStatusCode> StoreSecret(string name, string value) =>
        Send(HttpMethod.Post, "/api/secrets", new { name, value, appId = AppId });

    private Task<HttpStatusCode> CreateCompose() =>
        Send(HttpMethod.Post, $"/api/setup/create-compose?appId={AppId}",
            new { hostPort = 8199, containerPort = 3000 });

    /// <summary>
    /// The sequence a first-time operator actually performs: connect the app,
    /// fill in its secrets, then publish.
    /// </summary>
    [Fact]
    public async Task ASecretStoredBeforePublishingReachesTheEnvFile()
    {
        Assert.Equal(HttpStatusCode.OK, await StoreSecret(SecretName, SecretValue));

        // Nothing to write into yet — the app has no compose directory. This is the
        // ordinary state the sync skips, not a failure.
        Assert.False(File.Exists(EnvFile));

        Assert.Equal(HttpStatusCode.OK, await CreateCompose());

        Assert.Contains($"{SecretName}={SecretValue}", await File.ReadAllTextAsync(EnvFile), StringComparison.Ordinal);
    }

    /// <summary>
    /// The ports the wizard chose are still seeded — the secret pass must add to
    /// that file, not replace it.
    /// </summary>
    [Fact]
    public async Task TheSeededPortsSurviveTheSecretPass()
    {
        Assert.Equal(HttpStatusCode.OK, await StoreSecret(SecretName, SecretValue));
        Assert.Equal(HttpStatusCode.OK, await CreateCompose());

        var env = await File.ReadAllTextAsync(EnvFile);

        Assert.Contains("PINQOPS_HOST_PORT=8199", env, StringComparison.Ordinal);
        Assert.Contains("PINQOPS_CONTAINER_PORT=3000", env, StringComparison.Ordinal);
        Assert.Contains(SecretName, env, StringComparison.Ordinal);
    }

    /// <summary>
    /// An app published with no secrets at all still gets a valid <c>.env</c> —
    /// the sync must not be the thing that creates or clears it.
    /// </summary>
    [Fact]
    public async Task PublishingWithNoSecretsStillSeedsThePorts()
    {
        Assert.Equal(HttpStatusCode.OK, await CreateCompose());

        var env = await File.ReadAllTextAsync(EnvFile);

        Assert.Contains("PINQOPS_HOST_PORT=8199", env, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretName, env, StringComparison.Ordinal);
    }

    /// <summary>GitHub is unreachable here; the Dockerfile read is only a hint.</summary>
    private sealed class ForbiddenHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("""{"message":"Resource not accessible by personal access token"}"""),
            });
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.ConfigureTestServices(services =>
            {
                // No real networks, containers or GitHub: this test is about what
                // lands in the .env, and both of those are incidental to it.
                services.RemoveAll<IProcessRunner>();
                services.AddSingleton<IProcessRunner>(new FakeProcessRunner());
                services.RemoveAll<GitHubDashboardService>();
                services.AddSingleton(provider => new GitHubDashboardService(
                    provider.GetRequiredService<UiConfigStore>(),
                    new HttpClient(new ForbiddenHandler())));
            });
        }
    }
}
