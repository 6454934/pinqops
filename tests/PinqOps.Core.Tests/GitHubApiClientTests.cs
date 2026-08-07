using System.Net;
using PinqOps.Tests.Fakes;
using Xunit;

namespace PinqOps.Tests;

public class GitHubApiClientTests
{
    private static readonly GitHubRepository Repository =
        GitHubRepositoryParser.Parse("https://github.com/pinqponq/pinqops");

    [Fact]
    public async Task CreateRegistrationTokenAsync_ReturnsToken_AndSendsAuthenticatedPost()
    {
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.Created, "{\"token\":\"ABC123\"}");
        using var httpClient = new HttpClient(handler);
        using var client = new GitHubApiClient(httpClient);

        var token = await client.CreateRegistrationTokenAsync(Repository, "secret-pat");

        Assert.Equal("ABC123", token);
        var request = handler.LastRequest!;
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(Repository.RegistrationTokenUrl, request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("secret-pat", request.Headers.Authorization.Parameter);
        Assert.True(request.Headers.Contains("X-GitHub-Api-Version"));
        Assert.Contains("pinqops", request.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task CreateRegistrationTokenAsync_DoesNotLeakPatIntoTheUri()
    {
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.Created, "{\"token\":\"ABC123\"}");
        using var httpClient = new HttpClient(handler);
        using var client = new GitHubApiClient(httpClient);

        await client.CreateRegistrationTokenAsync(Repository, "secret-pat");

        Assert.DoesNotContain("secret-pat", handler.LastRequest!.RequestUri!.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task CreateRegistrationTokenAsync_ThrowsWithStatus_OnFailure(HttpStatusCode statusCode)
    {
        var handler = new RecordingHttpMessageHandler(statusCode, "{\"message\":\"nope\"}");
        using var httpClient = new HttpClient(handler);
        using var client = new GitHubApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<GitHubApiException>(
            () => client.CreateRegistrationTokenAsync(Repository, "secret-pat"));

        Assert.Equal((int)statusCode, exception.StatusCode);
    }

    [Fact]
    public async Task CreateRegistrationTokenAsync_Throws_WhenTokenMissing()
    {
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.Created, "{\"expires_at\":\"soon\"}");
        using var httpClient = new HttpClient(handler);
        using var client = new GitHubApiClient(httpClient);

        await Assert.ThrowsAsync<GitHubApiException>(
            () => client.CreateRegistrationTokenAsync(Repository, "secret-pat"));
    }
}
