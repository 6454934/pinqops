using PinqOps.Deploy;
using Xunit;

namespace PinqOps.Tests.Deploy;

/// <summary>
/// What a project has to look like before it can be run as two colours at once. The
/// gate exists because the failures it prevents are silent: nothing warns that a
/// database started empty, and nothing warns that two containers wanted one name
/// until compose is already halfway through.
/// </summary>
public class BlueGreenEligibilityTests
{
    /// <summary>The shape pinqops generates, once the proxy publishes its port.</summary>
    private const string Enrolled = """
        name: "shop"

        services:
          app:
            image: ${PINQOPS_IMAGE:-ghcr.io/acme/shop}:${PINQOPS_TAG:-latest}
            restart: unless-stopped
            expose:
              # pinqops: the proxy publishes this port — - "${PINQOPS_HOST_PORT:-8080}:${PINQOPS_CONTAINER_PORT:-80}"
              - "${PINQOPS_CONTAINER_PORT:-80}"
            networks:
              default:
              pinqops-apps:
                aliases:
                  - "${PINQOPS_ALIAS:-shop}"

        networks:
          pinqops-apps:
            external: true
        """;

    [Fact]
    public void TheProjectPinqopsGeneratesIsEligible()
    {
        var verdict = BlueGreenEligibility.Check(Enrolled);

        Assert.True(verdict.Eligible);
        Assert.Empty(verdict.Blockers);
    }

    [Fact]
    public void AProjectThatStillPublishesItsOwnPortIsRefused()
    {
        var yaml = Enrolled.Replace("expose:", "ports:\n      - \"8080:80\"", StringComparison.Ordinal);

        Assert.Contains(
            BlueGreenEligibility.Check(yaml).Blockers,
            blocker => blocker.Contains("publishes its own host port"));
    }

    [Fact]
    public void ASecondServiceIsRefusedAndNamed()
    {
        var yaml = Enrolled.Replace(
            "networks:\n      default:",
            "networks:\n      default:\n  worker:\n    image: ghcr.io/acme/worker:latest",
            StringComparison.Ordinal);

        var blocker = Assert.Single(BlueGreenEligibility.Check(yaml).Blockers);
        Assert.Contains("2 services", blocker);
        Assert.Contains("worker", blocker);
    }

    /// <summary>
    /// The sharpest edge in the design and the one that reads as the dullest line in
    /// the file: compose names a volume after its project, so the colours would not
    /// share it — a database would start empty on the first switch and swap back to
    /// stale data on the next.
    /// </summary>
    [Fact]
    public void AVolumeDeclaredInTheProjectIsRefused()
    {
        var yaml = Enrolled + "\n\nvolumes:\n  appdata:\n";

        var blocker = Assert.Single(BlueGreenEligibility.Check(yaml).Blockers);
        Assert.Contains("appdata", blocker);
        Assert.Contains("data would appear to vanish", blocker);
    }

    /// <summary>
    /// The same declaration written the other common way. <c>appdata: {}</c> and
    /// <c>appdata: null</c> mean exactly what <c>appdata:</c> means to compose, and
    /// the generated file invites the operator to add whatever their application
    /// needs — so the spelling they happen to use decides whether they are warned
    /// that their database is about to start empty.
    /// </summary>
    [Theory]
    [InlineData("  appdata: {}")]
    [InlineData("  appdata: null")]
    [InlineData("  appdata:   {}   ")]
    public void AVolumeDeclaredWithAnInlineBodyIsRefusedToo(string declaration)
    {
        var yaml = Enrolled + "\n\nvolumes:\n" + declaration + "\n";

        var blocker = Assert.Single(BlueGreenEligibility.Check(yaml).Blockers);
        Assert.Contains("appdata", blocker);
        Assert.Contains("data would appear to vanish", blocker);
    }

    /// <summary>
    /// And the inline spelling of the thing that is fine stays fine: an external
    /// volume is one volume whichever way it is written.
    /// </summary>
    [Fact]
    public void AnExternalVolumeWrittenInlineIsStillExternal()
    {
        var yaml = Enrolled + "\n\nvolumes:\n  appdata: {external: true}\n";

        Assert.True(BlueGreenEligibility.Check(yaml).Eligible);
    }

    /// <summary>
    /// A whole block written as one flow mapping is more than a text scan can read.
    /// Refusing is the only safe answer: the alternative is deciding there are no
    /// volumes because none could be seen, which is the reading that loses the data.
    /// </summary>
    [Fact]
    public void AVolumeBlockWrittenAsOneMappingIsRefusedRatherThanRead()
    {
        var yaml = Enrolled + "\n\nvolumes: {appdata: {}}\n";

        Assert.False(BlueGreenEligibility.Check(yaml).Eligible);
    }

    /// <summary>An empty block declares nothing, so there is nothing to refuse.</summary>
    [Fact]
    public void AnEmptyVolumeBlockIsNotAVolume()
    {
        Assert.True(BlueGreenEligibility.Check(Enrolled + "\n\nvolumes: {}\n").Eligible);
    }

    [Fact]
    public void AnExternalVolumeIsFineBecauseBothColoursGetTheSameOne()
    {
        var yaml = Enrolled + "\n\nvolumes:\n  appdata:\n    external: true\n";

        Assert.True(BlueGreenEligibility.Check(yaml).Eligible);
    }

    [Fact]
    public void AVolumeThatSaysExternalFalseIsStillProjectScoped()
    {
        var yaml = Enrolled + "\n\nvolumes:\n  appdata:\n    external: false\n";

        Assert.False(BlueGreenEligibility.Check(yaml).Eligible);
    }

    /// <summary>
    /// A service's own <c>volumes:</c> list is a set of mounts, not a declaration —
    /// a bind mount is a host path and both colours read the same one.
    /// </summary>
    [Fact]
    public void ABindMountUnderAServiceIsNotAVolumeDeclaration()
    {
        var yaml = Enrolled.Replace(
            "    networks:",
            "    volumes:\n      - ./config:/etc/app:ro\n    networks:",
            StringComparison.Ordinal);

        Assert.True(BlueGreenEligibility.Check(yaml).Eligible);
    }

    [Fact]
    public void AFixedContainerNameIsRefused()
    {
        var yaml = Enrolled.Replace(
            "    restart: unless-stopped",
            "    restart: unless-stopped\n    container_name: shop-app",
            StringComparison.Ordinal);

        Assert.Contains(BlueGreenEligibility.Check(yaml).Blockers, blocker => blocker.Contains("container_name:"));
    }

    [Fact]
    public void EveryReasonIsReportedRatherThanOnlyTheFirst()
    {
        var yaml = Enrolled.Replace("expose:", "ports:\n      - \"8080:80\"", StringComparison.Ordinal)
            + "\n\nvolumes:\n  appdata:\n";

        // Fixing one and being told about the next is three rounds of "deploy,
        // refused, fix" for something that could be said once.
        Assert.Equal(2, BlueGreenEligibility.Check(yaml).Blockers.Count);
    }

    [Fact]
    public void AFileWithNothingInItIsNotClaimedToBeEligibleOrNot()
    {
        // No services is not a project worth switching between colours, but it is
        // also not one of the four things this refuses — and it must not throw.
        Assert.True(BlueGreenEligibility.Check(string.Empty).Eligible);
    }
}
