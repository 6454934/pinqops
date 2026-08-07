using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// Which systemd unit belongs to a runner directory.
///
/// <para>The method's own summary states the rule and the reason for it: the scan
/// is "filtered to the repository the directory is registered to — never 'the
/// first actions.runner.* unit', which may belong to another repository". A
/// directory with no registration has no repository to filter by, and the filter
/// was written so that having nothing to compare against admitted everything
/// rather than nothing.</para>
///
/// <para>Which is a state that occurs on any host with more than one app: an app
/// added but not yet given a runner has an empty directory, and every page that
/// asks about its runner was answered with a different app's unit — its name, and
/// its running/stopped chip.</para>
/// </summary>
public class RunnerUnitMatchTests
{
    private const string TwoRunners =
        "actions.runner.acme-app-a.host-a.service loaded active running GitHub Actions Runner\n"
        + "actions.runner.acme-app-b.host-a.service loaded active running GitHub Actions Runner\n";

    [Fact]
    public void ARegisteredDirectoryFindsItsOwnUnit() =>
        Assert.Equal(
            "actions.runner.acme-app-b.host-a.service",
            LocalRunnerService.MatchingUnit(TwoRunners, "https://github.com/acme/app-b"));

    /// <summary>
    /// The case the whole filter exists for: no registration means no repository
    /// to match, which is nothing — not anything.
    /// </summary>
    [Fact]
    public void AnUnregisteredDirectoryMatchesNothing() =>
        Assert.Null(LocalRunnerService.MatchingUnit(TwoRunners, registeredUrl: null));

    /// <summary>A URL that is not a repository is no better than none.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("https://example.com/")]
    public void AnUnreadableRegistrationMatchesNothing(string registeredUrl) =>
        Assert.Null(LocalRunnerService.MatchingUnit(TwoRunners, registeredUrl));

    [Fact]
    public void ARepositoryWithNoRunnerOnThisHostMatchesNothing() =>
        Assert.Null(LocalRunnerService.MatchingUnit(TwoRunners, "https://github.com/acme/app-c"));

    [Fact]
    public void NoUnitsAtAllMatchesNothing() =>
        Assert.Null(LocalRunnerService.MatchingUnit(string.Empty, "https://github.com/acme/app-a"));

    /// <summary>
    /// A name that merely starts the same way is a different repository:
    /// <c>app-a</c> must not be answered with <c>app-ab</c>'s unit. The trailing
    /// dot in the expected prefix is what keeps them apart.
    /// </summary>
    [Fact]
    public void ALongerRepositoryNameIsNotAMatch()
    {
        const string Units = "actions.runner.acme-app-ab.host-a.service loaded active running Runner\n";

        Assert.Null(LocalRunnerService.MatchingUnit(Units, "https://github.com/acme/app-a"));
    }

    /// <summary>Lines that are not units are skipped rather than mistaken for one.</summary>
    [Fact]
    public void NonUnitLinesAreIgnored()
    {
        const string Units =
            "\n"
            + "some-other.service loaded active running Something Else\n"
            + "actions.runner.acme-app-a.host-a.service loaded active running Runner\n";

        Assert.Equal(
            "actions.runner.acme-app-a.host-a.service",
            LocalRunnerService.MatchingUnit(Units, "https://github.com/acme/app-a"));
    }
}
