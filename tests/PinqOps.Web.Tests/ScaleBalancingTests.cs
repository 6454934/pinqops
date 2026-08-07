using PinqOps.Deploy;
using PinqOps.Proxy;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// What the proxy routes point at after someone saves a copy count.
///
/// <para>Under blue-green the containers answer on a <em>colour-qualified</em>
/// alias and on nothing else — that qualification is what lets two versions exist
/// at once without splitting traffic between them. So the copy count, which
/// rewrites the same routes the cutover just set, has to preserve the colour. Both
/// ways of losing it take the app off the air: the unqualified alias resolves to
/// no container, and clearing the replica set falls back to a static upstream
/// naming a container no colour ever creates.</para>
///
/// <para>The rule is <see cref="ColorReconciler"/>'s — blue-green on means the
/// routes point at <c>Alias(alias, ActiveColor)</c> — and this is the same rule,
/// because the reconciler is what converges these routes at every restart. If the
/// two disagree, saving a copy count breaks the app until the dashboard restarts,
/// and restarting the dashboard silently undoes what the operator saved.</para>
/// </summary>
public class ScaleBalancingTests
{
    private const string Policy = LoadBalancingPolicies.RoundRobin;

    private const string Alias = "shop";

    private static DeploySettings Colored(string color) =>
        new() { BlueGreen = true, ActiveColor = color };

    // ---- an ordinary app is unchanged ---------------------------------------

    [Fact]
    public void OneCopyNeedsNoReplicaSet() =>
        Assert.Null(DeployEndpoints.RoutedBalancing(new DeploySettings(), Alias, 1, Policy));

    [Fact]
    public void MoreThanOneCopyPointsAtTheAppsAlias()
    {
        var balancing = DeployEndpoints.RoutedBalancing(new DeploySettings(), Alias, 3, Policy);

        Assert.Equal(Alias, balancing?.Alias);
        Assert.Equal(Policy, balancing?.Policy);
    }

    [Fact]
    public void AnAppThatPublishesItsOwnPortHasNoAliasToPointAt() =>
        Assert.Null(DeployEndpoints.RoutedBalancing(new DeploySettings(), null, 3, Policy));

    // ---- blue-green keeps its colour ----------------------------------------

    [Theory]
    [InlineData(DeployColors.Blue)]
    [InlineData(DeployColors.Green)]
    public void AColouredAppPointsAtTheColourItIsServing(string color)
    {
        var balancing = DeployEndpoints.RoutedBalancing(Colored(color), Alias, 3, Policy);

        Assert.Equal($"{Alias}-{color}", balancing?.Alias);
    }

    /// <summary>
    /// One copy included. The cutover uses the dynamic form whatever the count —
    /// "one copy resolves to one container" — so dropping to a static upstream here
    /// would point the route at <c>shop-app-1</c>, which under blue-green is a
    /// container name nothing runs under.
    /// </summary>
    [Fact]
    public void AColouredAppKeepsItsReplicaSetAtOneCopy()
    {
        var balancing = DeployEndpoints.RoutedBalancing(Colored(DeployColors.Green), Alias, 1, Policy);

        Assert.NotNull(balancing);
        Assert.Equal("shop-green", balancing.Alias);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void ItAgreesWithTheReconciler(int replicas)
    {
        var settings = Colored(DeployColors.Green);

        Assert.Equal(
            DeployColors.Alias(Alias, settings.ActiveColor),
            DeployEndpoints.RoutedBalancing(settings, Alias, replicas, Policy)?.Alias);
    }

    /// <summary>
    /// Blue-green cannot be turned on for an app that publishes its own port, so
    /// there is nothing to qualify and nothing to point at.
    /// </summary>
    [Fact]
    public void AColouredAppWithNoAliasStillHasNoReplicaSet() =>
        Assert.Null(DeployEndpoints.RoutedBalancing(Colored(DeployColors.Blue), null, 3, Policy));

    // ---- and the page is told the same name ---------------------------------

    /// <summary>
    /// The Copies page says "the proxy resolves the copies through the network
    /// name X". Under blue-green the name in the app's <c>.env</c> is not that
    /// name, so quoting it sends an operator looking for something that does not
    /// answer.
    /// </summary>
    [Fact]
    public void ThePageIsToldTheNameTheRoutesResolve()
    {
        var settings = Colored(DeployColors.Green);

        Assert.Equal("shop-green", DeployEndpoints.RoutedAlias(settings, Alias));
        Assert.Equal(
            DeployEndpoints.RoutedBalancing(settings, Alias, 3, Policy)?.Alias,
            DeployEndpoints.RoutedAlias(settings, Alias));
    }

    [Fact]
    public void AnOrdinaryAppIsQuotedItsOwnAlias() =>
        Assert.Equal(Alias, DeployEndpoints.RoutedAlias(new DeploySettings(), Alias));

    [Fact]
    public void AnAppWithNoAliasIsQuotedNone() =>
        Assert.Null(DeployEndpoints.RoutedAlias(Colored(DeployColors.Blue), null));
}
