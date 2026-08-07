using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

public class AppResolverTests
{
    private static UiConfig ConfigWith(params string[] ids) => new()
    {
        Apps = ids.Select(id => new AppConnection
        {
            Id = id, RepoUrl = $"https://github.com/o/{id}", ComposeFile = $"/c/{id}", RunnerDirectory = $"/r/{id}",
        }).ToList(),
    };

    [Fact]
    public void Resolve_ExplicitId_Hits()
    {
        Assert.Equal("b", AppResolver.Resolve(ConfigWith("a", "b"), "b").Id);
    }

    [Fact]
    public void Resolve_IsCaseInsensitive_AndTrims()
    {
        Assert.Equal("acme-shop", AppResolver.Resolve(ConfigWith("acme-shop"), " ACME-Shop ").Id);
    }

    [Fact]
    public void Resolve_UnknownId_Throws()
    {
        Assert.Throws<ArgumentException>(() => AppResolver.Resolve(ConfigWith("a"), "nope"));
    }

    [Fact]
    public void Resolve_NoId_DefaultsToTheFirstApp()
    {
        // Pre-upgrade callers send no id; they must keep working on the sole app.
        Assert.Equal("a", AppResolver.Resolve(ConfigWith("a", "b"), null).Id);
        Assert.Equal("a", AppResolver.Resolve(ConfigWith("a"), "").Id);
    }

    [Fact]
    public void Resolve_NoApps_SaysConnectFirst()
    {
        Assert.Throws<InvalidOperationException>(() => AppResolver.Resolve(new UiConfig(), null));
    }
}
