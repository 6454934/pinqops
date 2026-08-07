using System.Net;
using System.Text;
using PinqOps.Web;
using PinqOps.Web.Tests.Fakes;
using Xunit;

namespace PinqOps.Web.Tests;

public class GitHubDashboardServiceContentTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pinqops-ghcontent-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (GitHubDashboardService Service, SequencedHttpMessageHandler Handler, AppConnection App) Setup()
    {
        var store = new UiConfigStore(Path.Combine(_dir, "ui.json"));
        store.Update(c => c.Pat = "ghp_test");
        var handler = new SequencedHttpMessageHandler();
        var service = new GitHubDashboardService(store, new HttpClient(handler));
        var app = new AppConnection
        {
            Id = "acme-shop", RepoUrl = "https://github.com/acme/shop", ComposeFile = "/x", RunnerDirectory = "/y",
        };
        return (service, handler, app);
    }

    private static string ContentsResponse(string fileContent)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(fileContent));
        // GitHub wraps the base64 at 60 chars with newlines; mimic that.
        var wrapped = string.Join("\\n", Enumerable.Range(0, (b64.Length + 59) / 60)
            .Select(i => b64.Substring(i * 60, Math.Min(60, b64.Length - i * 60))));
        return $$"""{"content":"{{wrapped}}","encoding":"base64"}""";
    }

    [Fact]
    public async Task GetFileContent_DecodesBase64()
    {
        var (service, handler, app) = Setup();
        handler.Enqueue(HttpStatusCode.OK, ContentsResponse("EXPOSE 3000\nCMD npm start"));

        var content = await service.GetFileContentAsync(app, "Dockerfile");

        Assert.Equal("EXPOSE 3000\nCMD npm start", content);
        Assert.Equal("/repos/acme/shop/contents/Dockerfile", Assert.Single(handler.Requests).Path);
    }

    [Fact]
    public async Task GetFileContent_MissingFile_IsNull()
    {
        var (service, handler, app) = Setup();
        handler.Enqueue(HttpStatusCode.NotFound, """{"message":"Not Found"}""");

        Assert.Null(await service.GetFileContentAsync(app, "Dockerfile"));
    }

    [Fact]
    public async Task GetDockerfileExposedPort_DelegatesToFileContent()
    {
        var (service, handler, app) = Setup();
        // The repository variables are consulted first, to learn where to look.
        handler.Enqueue(HttpStatusCode.OK, """{"variables":[]}""");
        handler.Enqueue(HttpStatusCode.OK, ContentsResponse("FROM node:22\nEXPOSE 3000"));

        Assert.Equal(3000, await service.GetDockerfileExposedPortAsync(app));
        Assert.Equal("/repos/acme/shop/contents/Dockerfile", handler.Requests[^1].Path);
    }

    /// <summary>
    /// The case the wizard creates for a monorepo: it commits the Dockerfile into
    /// the subdirectory the operator picked and records that on the repository.
    /// Reading the root instead found nothing, so the container port fell back to
    /// the default and the app was published on a port its image does not listen
    /// on — while the wizard reported success.
    /// </summary>
    [Fact]
    public async Task GetDockerfileExposedPort_ReadsTheBuildContextsDockerfile()
    {
        var (service, handler, app) = Setup();
        handler.Enqueue(
            HttpStatusCode.OK,
            """{"variables":[{"name":"PINQOPS_BUILD_CONTEXT","value":"apps/web"}]}""");
        handler.Enqueue(HttpStatusCode.OK, ContentsResponse("FROM node:22\nEXPOSE 3000"));

        Assert.Equal(3000, await service.GetDockerfileExposedPortAsync(app));
        Assert.Equal("/repos/acme/shop/contents/apps/web/Dockerfile", handler.Requests[^1].Path);
    }

    /// <summary>
    /// A token that cannot read the repository's variables must leave the page
    /// working: the answer is where to look, not what to do.
    /// </summary>
    [Fact]
    public async Task GetDockerfileExposedPort_FallsBackToTheRootWhenVariablesAreUnreadable()
    {
        var (service, handler, app) = Setup();
        handler.Enqueue(HttpStatusCode.Forbidden, """{"message":"Resource not accessible"}""");
        handler.Enqueue(HttpStatusCode.OK, ContentsResponse("FROM node:22\nEXPOSE 3000"));

        Assert.Equal(3000, await service.GetDockerfileExposedPortAsync(app));
        Assert.Equal("/repos/acme/shop/contents/Dockerfile", handler.Requests[^1].Path);
    }

    [Fact]
    public async Task GetRepoTree_ReturnsBlobPathsAndTruncatedFlag()
    {
        var (service, handler, app) = Setup();
        handler.Enqueue(HttpStatusCode.OK, """
            {"tree":[
              {"path":"package.json","type":"blob"},
              {"path":"src","type":"tree"},
              {"path":"src/index.js","type":"blob"}
            ],"truncated":true}
            """);

        var (paths, truncated) = await service.GetRepoTreeAsync(app, "main");

        Assert.Equal(["package.json", "src/index.js"], paths);
        Assert.True(truncated);
        Assert.Equal("/repos/acme/shop/git/trees/main?recursive=1", Assert.Single(handler.Requests).Path);
    }

    [Fact]
    public async Task CreateFile_PutsBase64ContentAtThePath()
    {
        var (service, handler, app) = Setup();
        handler.Enqueue(HttpStatusCode.Created, """{"commit":{"html_url":"https://github.com/acme/shop/commit/abc"}}""");

        await service.CreateFileAsync(app, "Dockerfile", "chore: add Dockerfile", "FROM alpine\nEXPOSE 8080");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("/repos/acme/shop/contents/Dockerfile", request.Path);
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("FROM alpine\nEXPOSE 8080")), request.Body);
    }
}
