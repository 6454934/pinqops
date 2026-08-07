using Xunit;

namespace PinqOps.Tests;

public class AppNetworkTests
{
    [Theory]
    [InlineData("acme-shop", "pinqops-app-acme-shop")]
    [InlineData("ACME-Shop", "pinqops-app-acme-shop")]
    [InlineData("shop2048", "pinqops-app-shop2048")]
    public void TheNameIsDerivedFromTheAppId(string appId, string expected)
    {
        Assert.Equal(expected, AppNetwork.NameFor(appId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    public void AnAppIdThatWouldNotMakeANetworkNameIsRefused(string appId)
    {
        Assert.Throws<ArgumentException>(() => AppNetwork.NameFor(appId));
    }

    [Theory]
    [InlineData("pinqops-app-acme-shop", true)]
    [InlineData("pinqops-apps", false)]
    [InlineData("bridge", false)]
    [InlineData("pinqops-app-bad name", false)]
    [InlineData(null, false)]
    public void OnlyPinqopsAppNetworksAreRecognised(string? name, bool expected)
    {
        Assert.Equal(expected, AppNetwork.IsAppNetwork(name));
    }

    /// <summary>The shared network is not an app network, so the proxy's reconnect
    /// sweep never confuses the two.</summary>
    [Fact]
    public void TheSharedNetworkIsNotAnAppNetwork()
    {
        Assert.False(AppNetwork.IsAppNetwork(ComposeTemplate.SharedNetwork));
    }
}

public class ComposeTemplateNetworkTests
{
    private static string Yaml(string? network) =>
        ComposeTemplate.Yaml("acme", "shop", "shop", 8080, 3000, network);

    /// <summary>
    /// The default is unchanged, which is what keeps every project created before
    /// app networks existed working — their compose files are not rewritten, and
    /// rewriting them would cut them off from the database they already use.
    /// </summary>
    [Fact]
    public void TheDefaultIsStillTheSharedNetwork()
    {
        var yaml = Yaml(null);

        Assert.Contains("pinqops-apps:\n        aliases:", yaml, StringComparison.Ordinal);
        Assert.Contains("  pinqops-apps:\n    external: true", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultMatchesWhatTheOlderOverloadProduced()
    {
        Assert.Equal(ComposeTemplate.Yaml("acme", "shop", "shop", 8080, 3000), Yaml(null));
    }

    [Fact]
    public void AnAppNetworkReplacesTheSharedOneEverywhere()
    {
        var yaml = Yaml("pinqops-app-shop");

        Assert.Contains("pinqops-app-shop:\n        aliases:", yaml, StringComparison.Ordinal);
        Assert.Contains("  pinqops-app-shop:\n    external: true", yaml, StringComparison.Ordinal);
        // Not on both — being on the shared network too would undo the isolation.
        Assert.DoesNotContain("pinqops-apps", yaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// The alias is the name the proxy forwards to, and it is per-project on
    /// purpose: two projects sharing one would share traffic.
    /// </summary>
    [Fact]
    public void TheAppAnswersToAPerProjectAlias()
    {
        Assert.Contains(
            "- \"${PINQOPS_ALIAS:-shop}\"",
            ComposeTemplate.Yaml("acme", "shop", "shop", 8080, 3000),
            StringComparison.Ordinal);

        Assert.Contains(
            "- \"${PINQOPS_ALIAS:-shop-pr-8}\"",
            ComposeTemplate.Yaml("acme", "shop", "shop-pr-8", 9108, 3000),
            StringComparison.Ordinal);
    }

    /// <summary>The comment explains which of the two shapes it is, because the
    /// file is one an operator edits.</summary>
    [Fact]
    public void TheGeneratedFileSaysWhichNetworkItIsOn()
    {
        Assert.Contains("shared network the catalog apps live on", Yaml(null), StringComparison.Ordinal);
        Assert.Contains("This app's own network", Yaml("pinqops-app-shop"), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyNetworkFallsBackToTheSharedOne()
    {
        Assert.Equal(Yaml(null), Yaml("  "));
    }
}
