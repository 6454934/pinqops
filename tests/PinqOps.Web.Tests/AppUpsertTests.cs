using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

public class AppUpsertTests
{
    private static GitHubRepository Repo(string owner, string name) =>
        GitHubRepositoryParser.Parse($"https://github.com/{owner}/{name}");

    [Fact]
    public void Apply_CreatesANewAppWithDefaultPaths()
    {
        var config = new UiConfig();

        var app = AppUpsert.Apply(config, appId: null, Repo("acme", "shop"), composeFile: null, runnerDirectory: null);

        Assert.Equal("acme-shop", app.Id);
        Assert.Equal("/opt/pinqops/apps/acme-shop/docker-compose.yml", app.ComposeFile);
        Assert.Equal("/opt/pinqops/runners/acme-shop", app.RunnerDirectory);
        Assert.Same(app, Assert.Single(config.Apps));
    }

    [Fact]
    public void Apply_SameRepoUrl_IsIdempotent()
    {
        var config = new UiConfig();
        var first = AppUpsert.Apply(config, null, Repo("acme", "shop"), null, null);

        var second = AppUpsert.Apply(config, null, Repo("acme", "shop"), null, null);

        Assert.Same(first, second);
        Assert.Single(config.Apps);
    }

    [Fact]
    public void Apply_ExplicitAppId_EditsThatApp()
    {
        var config = new UiConfig();
        AppUpsert.Apply(config, null, Repo("acme", "shop"), null, null);

        var edited = AppUpsert.Apply(config, "acme-shop", Repo("acme", "shop"), "/custom/compose.yml", null);

        Assert.Equal("/custom/compose.yml", edited.ComposeFile);
        Assert.Single(config.Apps);
    }

    [Fact]
    public void Apply_UnknownAppId_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            AppUpsert.Apply(new UiConfig(), "nope", Repo("acme", "shop"), null, null));
    }

    [Fact]
    public void Apply_RejectsAComposeFileAnotherAppOwns()
    {
        var config = new UiConfig();
        AppUpsert.Apply(config, null, Repo("acme", "shop"), "/opt/x.yml", null);

        // The second repository's deploy would pin its tag onto the first one's
        // image — this must die as a form error, not at deploy time.
        Assert.Throws<ArgumentException>(() =>
            AppUpsert.Apply(config, null, Repo("acme", "blog"), "/opt/x.yml", null));
    }

    [Fact]
    public void Apply_SlugCollision_GetsAUniquifier()
    {
        var config = new UiConfig();
        // "a.b/c" and "ab/c" both reduce to "ab-c".
        AppUpsert.Apply(config, null, Repo("a.b", "c"), null, null);

        var second = AppUpsert.Apply(config, null, Repo("ab", "c"), null, null);

        Assert.Equal("ab-c-2", second.Id);
        Assert.Equal("/opt/pinqops/apps/ab-c-2/docker-compose.yml", second.ComposeFile);
    }
}
