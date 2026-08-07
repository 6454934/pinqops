using System.Text.RegularExpressions;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// "GitHub is not connected" and "GitHub is connected but you have no app yet"
/// are two different dead ends with two different ways out, and the dashboard
/// used to hold them in one flag: <c>ghConfigured = patMasked &amp;&amp; apps.length</c>.
///
/// <para>An operator who had stored a token and not yet published a repository
/// therefore read "GitHub is not connected" on the overview banner, on both
/// empty app lists, and in the sidebar's lock tooltip, and was sent back to redo
/// the one step they had already finished. Nothing failed and nothing was
/// logged — the pages rendered exactly as written, with the wrong sentence.</para>
///
/// <para>The flag now answers the token question alone, and app-existence is
/// asked for separately wherever it is what actually matters. These tests pin
/// that split, since the file has no build step to notice it collapsing again.</para>
/// </summary>
public class DashboardConnectionStateTests
{
    /// <summary>
    /// The connection flag is derived from the stored token and nothing else.
    /// Folding the app count back in is the original defect exactly.
    /// </summary>
    [Fact]
    public void TheConnectionFlagDependsOnTheTokenAlone()
    {
        var assignment = Regex.Match(DashboardSource.Script, @"\n\s*ghConnected\s*=\s*([^;]+);");

        Assert.True(assignment.Success, "ghConnected is no longer assigned in applySettings.");

        var expression = assignment.Groups[1].Value;
        Assert.DoesNotContain("apps", expression, StringComparison.Ordinal);
        Assert.Contains("patMasked", expression, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two empty-state messages are chosen by the connection flag. Keyed off
    /// anything that also requires an app, every fresh install reads "GitHub is
    /// not connected" no matter what it has actually done.
    /// </summary>
    [Fact]
    public void TheEmptyAppListPicksItsMessageByConnectionNotByAppCount()
    {
        // Every read of the key, with the line up to it for context; the table's
        // own definition is the one occurrence followed by a colon.
        var uses = Regex.Matches(DashboardSource.Script, @"([^\n]{0,60})""pj\.noGh""(?!\s*:)");

        Assert.NotEmpty(uses);
        foreach (Match use in uses)
        {
            Assert.Contains(
                "ghConnected?",
                use.Groups[1].Value.Replace(" ", ""),
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The sidebar lock says "Not connected — click to sign in". It may only
    /// appear when that sentence is true.
    /// </summary>
    [Fact]
    public void TheNavigationLockTracksTheConnectionOnly()
    {
        var nav = DashboardSource.FunctionBody("function buildNav()");
        var locked = Regex.Match(nav, @"if\(v===""github""&&([^)]+)\)");

        Assert.True(locked.Success, "the GitHub nav lock is no longer a conditional in buildNav().");
        Assert.Equal("!ghConnected", locked.Groups[1].Value);
    }

    /// <summary>
    /// Each dead end keeps its own banner. One sentence for both states is how
    /// the wrong instruction reached half of the people who read it.
    /// </summary>
    [Fact]
    public void TheBannerSaysWhichStepIsActuallyMissing()
    {
        var banner = DashboardSource.FunctionBody("function renderGhBanner()");

        Assert.Contains("banner.connect", banner, StringComparison.Ordinal);
        Assert.Contains("banner.publish", banner, StringComparison.Ordinal);

        // Someone with a token and no app still needs telling; the banner cannot
        // go back to hiding itself the moment a token exists.
        Assert.Contains("apps.length", banner, StringComparison.Ordinal);
    }
}
