using PinqOps.Tests.Fakes;
using Xunit;

namespace PinqOps.Tests;

public class RegistrationTokenResolverTests
{
    private static readonly GitHubRepository Repository =
        GitHubRepositoryParser.Parse("https://github.com/pinqponq/pinqops");

    private static RegistrationTokenResolver Build(
        FakeProcessRunner runner,
        FakeGitHubApiClient apiClient,
        FakePrompt prompt) =>
        new(new GhCli(runner), apiClient, prompt);

    private static FakeProcessRunner GhUnavailableRunner() =>
        new((fileName, _) => fileName == "gh"
            ? throw new InvalidOperationException("gh not installed")
            : new ProcessResult(0, string.Empty, string.Empty));

    [Fact]
    public async Task ResolveAsync_PreSuppliedToken_UsedVerbatim_NoGhNoApi()
    {
        var runner = new FakeProcessRunner();
        var apiClient = new FakeGitHubApiClient();
        var resolver = Build(runner, apiClient, new FakePrompt());
        var options = SetupOptions.Create(registrationToken: "pre-token");

        var token = await resolver.ResolveAsync(Repository, options);

        Assert.Equal("pre-token", token);
        Assert.Empty(apiClient.Calls);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task ResolveAsync_GhAuthenticated_MintsViaGh()
    {
        var runner = new FakeProcessRunner((fileName, arguments) =>
            new ProcessResult(0, fileName == "gh" && arguments.Contains("api") ? "gh-token" : string.Empty, string.Empty));
        var apiClient = new FakeGitHubApiClient();
        var resolver = Build(runner, apiClient, new FakePrompt());

        var token = await resolver.ResolveAsync(Repository, SetupOptions.Create());

        Assert.Equal("gh-token", token);
        Assert.Empty(apiClient.Calls);
    }

    [Fact]
    public async Task ResolveAsync_GhUnavailable_FallsBackToPatAndMintsViaApi()
    {
        var apiClient = new FakeGitHubApiClient("api-token");
        var resolver = Build(GhUnavailableRunner(), apiClient, new FakePrompt());
        var options = SetupOptions.Create(personalAccessToken: "my-pat");

        var token = await resolver.ResolveAsync(Repository, options);

        Assert.Equal("api-token", token);
        Assert.Single(apiClient.Calls);
        Assert.Equal("my-pat", apiClient.Calls[0].PersonalAccessToken);
    }

    [Fact]
    public async Task ResolveAsync_PatFlowsOnlyToApi_NeverReturnedOrLogged()
    {
        var runner = GhUnavailableRunner();
        var apiClient = new FakeGitHubApiClient("api-token");
        var resolver = Build(runner, apiClient, new FakePrompt());
        var options = SetupOptions.Create(personalAccessToken: "top-secret-pat");

        var token = await resolver.ResolveAsync(Repository, options);

        Assert.NotEqual("top-secret-pat", token);
        Assert.DoesNotContain(runner.Invocations, invocation => invocation.CommandLine.Contains("top-secret-pat"));
    }

    [Fact]
    public async Task ResolveAsync_NoGhNoPat_Interactive_PromptsForPastedToken()
    {
        // First AskSecret (PAT prompt) is empty, second (paste prompt) returns the token.
        var prompt = new FakePrompt(string.Empty, "pasted-token");
        var apiClient = new FakeGitHubApiClient();
        var resolver = Build(GhUnavailableRunner(), apiClient, prompt);

        var token = await resolver.ResolveAsync(Repository, SetupOptions.Create());

        Assert.Equal("pasted-token", token);
        Assert.Empty(apiClient.Calls);
    }

    [Fact]
    public async Task ResolveAsync_NoGh_UsesPatFromPrompt()
    {
        var prompt = new FakePrompt("prompted-pat");
        var apiClient = new FakeGitHubApiClient("api-token");
        var resolver = Build(GhUnavailableRunner(), apiClient, prompt);

        var token = await resolver.ResolveAsync(Repository, SetupOptions.Create());

        Assert.Equal("api-token", token);
        Assert.Single(apiClient.Calls);
        Assert.Equal("prompted-pat", apiClient.Calls[0].PersonalAccessToken);
    }

    [Fact]
    public async Task ResolveAsync_NonInteractive_NoToken_Throws()
    {
        var resolver = Build(GhUnavailableRunner(), new FakeGitHubApiClient(), new FakePrompt());
        var options = SetupOptions.Create(nonInteractive: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(Repository, options));
    }

    [Fact]
    public async Task ResolveAsync_NoGhFlag_SkipsGhEvenWhenAvailable()
    {
        // Runner would succeed for gh, but --no-gh must skip branch 1 entirely.
        var runner = new FakeProcessRunner((fileName, arguments) =>
            new ProcessResult(0, fileName == "gh" && arguments.Contains("api") ? "gh-token" : string.Empty, string.Empty));
        var apiClient = new FakeGitHubApiClient("api-token");
        var resolver = Build(runner, apiClient, new FakePrompt());
        var options = SetupOptions.Create(personalAccessToken: "my-pat", useGhCli: false);

        var token = await resolver.ResolveAsync(Repository, options);

        Assert.Equal("api-token", token);
        Assert.DoesNotContain(runner.Invocations, invocation => invocation.FileName == "gh");
    }
}
