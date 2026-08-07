using System.Text.RegularExpressions;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// The publish wizard is the whole first-run experience: pick a repository and
/// watch eight steps set the server up. What it shows while it runs — and what
/// it shows when it stops early — is the only thing the operator has to go on.
/// </summary>
public class DashboardWizardTests
{
    private static string Script => DashboardSource.Script;

    /// <summary>
    /// The progress bar counts steps that ran. It must not count the ones the
    /// run marks on its way out: those are marked <em>because</em> it failed, so
    /// counting them made the bar fill up as the run fell over — a failure at
    /// step four of eight drew an 88% bar.
    /// </summary>
    [Fact]
    public void SkippedStepsDoNotCountTowardsTheProgressBar()
    {
        var body = DashboardSource.FunctionBody("const wizMark=(");

        var counter = Regex.Match(body, @"querySelectorAll\(""([^""]+)""\)").Groups[1].Value;
        Assert.NotEmpty(counter);
        Assert.Contains(".wiz-step.ok", counter, StringComparison.Ordinal);
        Assert.Contains(".wiz-step.warn", counter, StringComparison.Ordinal);
        Assert.DoesNotContain(".wiz-step.skip", counter, StringComparison.Ordinal);
        Assert.DoesNotContain(".wiz-step.err", counter, StringComparison.Ordinal);

        // "skip" needs its own icon, or it is indistinguishable from pending.
        Assert.Contains("state===\"skip\"", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A bar frozen part-way in the normal colour still reads as progress, so it
    /// turns red the moment any step fails.
    /// </summary>
    [Fact]
    public void TheProgressBarShowsThatTheRunFailed()
    {
        var body = DashboardSource.FunctionBody("const wizMark=(");

        Assert.Contains("classList.toggle(\"failed\"", body, StringComparison.Ordinal);
        Assert.Contains(".wiz-step.err", body, StringComparison.Ordinal);
        Assert.Contains(".bar i.failed", DashboardSource.Html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every mark the publish run makes after it has already failed means "we
    /// never got here". Marking those "warn" both inflated the bar and put an
    /// exclamation next to steps that were simply never attempted.
    /// </summary>
    [Fact]
    public void TheRunMarksUnreachedStepsAsSkippedRatherThanWarned()
    {
        var run = DashboardSource.FunctionBody("async function runPublish(");

        foreach (var step in new[] { "var", "compose", "runner", "verify", "deploy" })
        {
            Assert.Contains($"wizMark(\"{step}\",\"skip\"", run, StringComparison.Ordinal);
        }

        // A real warning still exists — a missing Dockerfile is a warning on a
        // step that genuinely ran — so "warn" must not have been swept away.
        Assert.Contains("wizMark(\"dockerfile\",\"warn\"", Script, StringComparison.Ordinal);
    }

    /// <summary>
    /// Installing the runner downloads a package and is the longest step in the
    /// wizard. Its progress lines went only to the log panel, which is collapsed
    /// by default, so the step showed a spinner and nothing else for minutes.
    /// </summary>
    [Fact]
    public void TheRunnerInstallShowsItsProgressOnTheStepItself()
    {
        var run = DashboardSource.FunctionBody("async function runPublish(");

        var install = run.IndexOf("installRunnerWithProgress(", StringComparison.Ordinal);
        Assert.True(install >= 0, "the publish run no longer installs the runner with progress.");

        var callback = run[install..(install + 200)];
        Assert.Contains("wizSay(line)", callback, StringComparison.Ordinal);
        Assert.Contains("wizMark(\"runner\",\"run\",esc(line)", callback, StringComparison.Ordinal);
    }

    /// <summary>
    /// A rollback pulls an image and recreates the containers, so it runs for
    /// minutes. Its only sign of life was a toast, and a toast is gone after
    /// five seconds — leaving the page apparently idle while the running app
    /// was replaced underneath it.
    /// </summary>
    [Fact]
    public void ARollbackReportsForAsLongAsItRuns()
    {
        Assert.Contains("id=\"dep-rollback-progress\"", DashboardSource.Html, StringComparison.Ordinal);

        var render = DashboardSource.FunctionBody("function renderRollbackProgress(");
        Assert.Contains("dh.rbRunning", render, StringComparison.Ordinal);
        Assert.Contains("class=\"spin\"", render, StringComparison.Ordinal);

        // The live phase from the job replaces the generic line as it arrives.
        var poll = DashboardSource.FunctionBody("async function pollRollbackOnce(");
        Assert.Contains("rollbackJob.phase=job.phase", poll, StringComparison.Ordinal);
        Assert.Contains("renderRollbackProgress()", poll, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two overlapping rollbacks race to recreate the same containers, and the
    /// second confirm was reachable for the whole time the first was running.
    /// </summary>
    [Fact]
    public void OnlyOneRollbackCanRunAtATime()
    {
        var start = DashboardSource.FunctionBody("function rollBackTo(");
        Assert.Contains("if(rollbackJob){toast(t(\"dh.rbBusy\")", start, StringComparison.Ordinal);

        // The buttons in the hero and the history rows disable while it runs.
        var hero = DashboardSource.FunctionBody("function renderDeployHero(");
        Assert.Contains("rollbackJob?\"disabled\":\"\"", hero, StringComparison.Ordinal);
    }

    /// <summary>
    /// The job outlives the panel it was started from. Leaving must stop the poll
    /// rather than leave an interval ticking against a page nobody is looking at,
    /// and coming back must pick it up again instead of losing track of a running
    /// rollback.
    ///
    /// <para>Deploy is a tab of the app page rather than a view of its own, so two
    /// things can take it off screen — changing view, and changing tab. Both route
    /// through <c>syncRollbackPoll</c>, which is where the condition lives; a
    /// caller that stops calling it is the regression this pins.</para>
    /// </summary>
    [Fact]
    public void LeavingTheDeployTabStopsTheRollbackPollAndReturningResumesIt()
    {
        foreach (var caller in new[] { "function setView(", "function setAppTab(" })
        {
            Assert.Contains("syncRollbackPoll()", DashboardSource.FunctionBody(caller), StringComparison.Ordinal);
        }

        var sync = DashboardSource.FunctionBody("function syncRollbackPoll(");

        Assert.Contains("appTab===\"deploy\"", sync, StringComparison.Ordinal);
        Assert.Contains("stopRollbackPoll()", sync, StringComparison.Ordinal);
        Assert.Contains("resumeRollbackPoll()", sync, StringComparison.Ordinal);

        // A poll that resolves after the job was cleared belongs to nothing.
        var poll = DashboardSource.FunctionBody("async function pollRollbackOnce(");
        Assert.Contains("rollbackJob.jobId!==jobId)return", poll, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bar divides by this list, and the reset loop clears only what it
    /// names, so a step row without a key here is a step the bar cannot see.
    /// </summary>
    [Fact]
    public void EveryWizardStepRowHasAKey()
    {
        var keys = Regex.Match(Script, @"const WIZ_KEYS=\[(.*?)\];").Groups[1].Value;
        Assert.NotEmpty(keys);

        var declared = Regex.Matches(keys, "\"([a-z]+)\"").Select(match => match.Groups[1].Value).ToList();
        var rows = Regex.Matches(DashboardSource.Html, "id=\"wiz-([a-z]+)\"")
            .Select(match => match.Groups[1].Value)
            .Where(id => id != "bar" && id != "log" && id != "summary")
            .ToList();

        Assert.Equal(declared.Order(), rows.Order());
    }
}
