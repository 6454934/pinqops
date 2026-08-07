using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

public class AppConnectionTests
{
    [Theory]
    [InlineData("acme", "shop", "acme-shop")]
    [InlineData("Acme", "Shop.Web", "acme-shopweb")]
    [InlineData("acme", "2048", "acme-2048")]
    public void SlugFor_ReducesOwnerAndRepo(string owner, string repo, string expected)
    {
        var repository = GitHubRepositoryParser.Parse($"https://github.com/{owner}/{repo}");

        Assert.Equal(expected, AppConnection.SlugFor(repository));
    }

    [Fact]
    public void SlugFor_KeepsSameNamedReposFromDifferentOwnersApart()
    {
        var a = GitHubRepositoryParser.Parse("https://github.com/acme/shop");
        var b = GitHubRepositoryParser.Parse("https://github.com/evil/shop");

        Assert.NotEqual(AppConnection.SlugFor(a), AppConnection.SlugFor(b));
    }

    [Fact]
    public void DefaultPaths_DeriveFromTheId()
    {
        Assert.Equal("/opt/pinqops/apps/acme-shop/docker-compose.yml",
            AppConnection.DefaultComposeFileFor("acme-shop"));
        Assert.Equal("/opt/pinqops/runners/acme-shop",
            AppConnection.DefaultRunnerDirectoryFor("acme-shop"));
    }
}
