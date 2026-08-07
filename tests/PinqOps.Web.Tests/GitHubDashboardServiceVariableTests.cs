using System.Net;
using System.Text.Json;
using PinqOps.Web;
using PinqOps.Web.Tests.Fakes;
using Xunit;

namespace PinqOps.Web.Tests;

public class GitHubDashboardServiceVariableTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pinqops-ghvar-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (GitHubDashboardService Service, SequencedHttpMessageHandler Handler, AppConnection App) Setup(
        string repoUrl = "https://github.com/acme/shop")
    {
        var store = new UiConfigStore(Path.Combine(_dir, "ui.json"));
        store.Update(c => c.Pat = "ghp_test");
        var handler = new SequencedHttpMessageHandler();
        var service = new GitHubDashboardService(store, new HttpClient(handler));
        var app = new AppConnection
        {
            Id = "acme-shop", RepoUrl = repoUrl, ComposeFile = "/x", RunnerDirectory = "/y",
        };
        return (service, handler, app);
    }

    [Fact]
    public async Task SetRepositoryVariable_CreatesWithPost()
    {
        var (service, handler, app) = Setup();
        handler.Enqueue(HttpStatusCode.Created);

        await service.SetRepositoryVariableAsync(app, "APP_COMPOSE_PATH", "/opt/pinqops/apps/acme-shop/docker-compose.yml");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/repos/acme/shop/actions/variables", request.Path);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("APP_COMPOSE_PATH", body.RootElement.GetProperty("name").GetString());
        Assert.Equal("/opt/pinqops/apps/acme-shop/docker-compose.yml", body.RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public async Task SetRepositoryVariable_ExistingVariable_FallsBackToPatch()
    {
        var (service, handler, app) = Setup();
        handler
            .Enqueue(HttpStatusCode.Conflict, """{"message":"Variable already exists"}""")
            .Enqueue(HttpStatusCode.NoContent);

        await service.SetRepositoryVariableAsync(app, "APP_COMPOSE_PATH", "/opt/x.yml");

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Patch, handler.Requests[1].Method);
        Assert.Equal("/repos/acme/shop/actions/variables/APP_COMPOSE_PATH", handler.Requests[1].Path);
        Assert.Equal(handler.Requests[0].Body, handler.Requests[1].Body);
    }

    [Fact]
    public async Task SetRepositoryVariable_OtherErrors_Propagate()
    {
        var (service, handler, app) = Setup();
        handler.Enqueue(HttpStatusCode.Forbidden, """{"message":"Resource not accessible"}""");

        await Assert.ThrowsAsync<GitHubApiException>(
            () => service.SetRepositoryVariableAsync(app, "APP_COMPOSE_PATH", "/opt/x.yml"));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Context_UsesTheAppsRepositoryHost()
    {
        // A GHES app must hit its own host's /api/v3 base, not api.github.com —
        // once the operator has allowed that host.
        using var _ = AllowEnterpriseHost("ghe.example.com");
        var (service, handler, app) = Setup("https://ghe.example.com/acme/shop");
        handler.Enqueue(HttpStatusCode.Created);

        await service.SetRepositoryVariableAsync(app, "K", "V");

        Assert.Equal("/api/v3/repos/acme/shop/actions/variables", Assert.Single(handler.Requests).Path);
    }

    // Every request carries the stored PAT, and the host comes from a
    // caller-supplied repository URL. Without an allowlist, setting a repository
    // URL is enough to choose where the token gets sent.
    [Fact]
    public async Task Context_RefusesAHostTheOperatorHasNotAllowed()
    {
        using var _ = AllowEnterpriseHost(null);
        var (service, handler, app) = Setup("https://attacker.example.com/acme/shop");

        await Assert.ThrowsAsync<ArgumentException>(() => service.SetRepositoryVariableAsync(app, "K", "V"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Context_RefusesAHostOtherThanTheAllowedOne()
    {
        using var _ = AllowEnterpriseHost("ghe.example.com");
        var (service, handler, app) = Setup("https://attacker.example.com/acme/shop");

        await Assert.ThrowsAsync<ArgumentException>(() => service.SetRepositoryVariableAsync(app, "K", "V"));
        Assert.Empty(handler.Requests);
    }

    /// <summary>Sets the allowed enterprise host for one test and restores it after.</summary>
    private static IDisposable AllowEnterpriseHost(string? host)
    {
        var previous = Environment.GetEnvironmentVariable(GitHubDashboardService.EnterpriseHostVariable);
        Environment.SetEnvironmentVariable(GitHubDashboardService.EnterpriseHostVariable, host);
        return new Restore(() =>
            Environment.SetEnvironmentVariable(GitHubDashboardService.EnterpriseHostVariable, previous));
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
