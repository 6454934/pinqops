using System.Text.RegularExpressions;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// A server call with no visible busy state is indistinguishable from a frozen
/// page: the button stays enabled, nothing moves, and the user either clicks
/// again — firing the request twice — or gives up. The dashboard has no build
/// step and no component framework, so nothing but a check like this notices
/// when a handler loses its spinner again.
/// </summary>
public class DashboardBusyStateTests
{
    private static string Html => DashboardSource.Html;
    private static string Script => DashboardSource.Script;
    private static string FunctionBody(string declaration) => DashboardSource.FunctionBody(declaration);
    private static string HandlerSource(string declaration) => DashboardSource.HandlerSource(declaration);

    /// <summary>
    /// The helper every busy button relies on. Each of these lines is load-bearing:
    /// without the guard a double click still fires two requests, without the
    /// disable the button invites one, and without the restore in <c>finally</c> a
    /// failed call leaves the button spinning forever.
    /// </summary>
    [Fact]
    public void TheBusyHelperGuardsDisablesAndAlwaysRestores()
    {
        var body = FunctionBody("async function runBusy(");

        Assert.Contains("dataset.busy", body, StringComparison.Ordinal);
        Assert.Contains("button.disabled=true", body, StringComparison.Ordinal);
        Assert.Contains("aria-busy", body, StringComparison.Ordinal);
        Assert.Contains("class=\"spin\"", body, StringComparison.Ordinal);

        // The restore has to be unconditional, not just on the success path.
        var restore = body[body.IndexOf("finally", StringComparison.Ordinal)..];
        Assert.Contains("button.disabled=false", restore, StringComparison.Ordinal);
        Assert.Contains("button.innerHTML=original", restore, StringComparison.Ordinal);
    }

    /// <summary>
    /// Signing in is the first thing anyone does and the slowest single call in
    /// the app — the password hash is deliberately expensive, and the dashboard
    /// then loads before the screen changes.
    /// </summary>
    [Fact]
    public void TheSignInFormShowsABusyStateWhileItSubmits()
    {
        var handler = FunctionBody("$(\"#lock-form\").addEventListener(\"submit\"");

        Assert.Contains("runBusy(", handler, StringComparison.Ordinal);
        Assert.Contains("lk.creating", handler, StringComparison.Ordinal);
        Assert.Contains("lk.signingIn", handler, StringComparison.Ordinal);
    }

    /// <summary>
    /// Starting the GitHub device flow is a round trip to github.com, and the
    /// approval that follows is polled for as long as it takes. Both stretches
    /// need to say they are working.
    /// </summary>
    [Fact]
    public void TheGitHubDeviceFlowReportsProgressWhileItWaits()
    {
        var handler = FunctionBody("$(\"#btn-gh-signin\").onclick=");
        Assert.Contains("runBusy(", handler, StringComparison.Ordinal);
        Assert.Contains("se.contacting", handler, StringComparison.Ordinal);

        // The waiting row is the only feedback while GitHub is polled, so it has
        // to exist in the markup and be driven from the poll loop.
        Assert.Contains("id=\"gh-device-wait\"", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"gh-device-wait-text\"", Html, StringComparison.Ordinal);

        var poll = FunctionBody("async function devicePollOnce(");
        Assert.Contains("setDeviceWait(", poll, StringComparison.Ordinal);
        Assert.Contains("se.finishing", poll, StringComparison.Ordinal);
    }

    /// <summary>
    /// A poll that resolves after the user cancelled belongs to a flow nobody is
    /// watching; acting on it re-opened the code box, or signed in against a
    /// handle the user had abandoned.
    /// </summary>
    [Fact]
    public void AnAbandonedDevicePollIsIgnoredWhenItResolves()
    {
        var poll = FunctionBody("async function devicePollOnce(");

        Assert.Contains("const handle=deviceHandle", poll, StringComparison.Ordinal);
        Assert.Contains("if(deviceHandle!==handle)return", poll, StringComparison.Ordinal);
    }

    /// <summary>
    /// With the daemon down, five pages printed docker's raw stderr. The pattern
    /// has to keep matching what the server now sends
    /// (<see cref="DockerDaemonError.Unreachable"/>) as well as the daemon's own
    /// phrasings, which still arrive from remote hosts.
    /// </summary>
    [Fact]
    public void TheDockerUnreachablePatternMatchesWhatTheServerSends()
    {
        var pattern = Regex.Match(Script, @"const DOCKER_DOWN=/(.+?)/i;").Groups[1].Value;
        Assert.NotEmpty(pattern);

        var matcher = new Regex(pattern, RegexOptions.IgnoreCase);
        Assert.Matches(matcher, DockerDaemonError.Unreachable);

        // Every message the server can send for an unreachable daemon.
        string[] stderrs =
        [
            "Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?",
            "Got permission denied while trying to connect to the Docker daemon socket at unix:///var/run/docker.sock",
            "/bin/sh: 1: docker: command not found",
        ];
        foreach (var standardError in stderrs)
        {
            var described = DockerDaemonError.Describe(standardError);
            Assert.NotNull(described);
            Assert.Matches(matcher, described);
        }

        // And the raw daemon strings, for a remote host answering for itself.
        Assert.Matches(matcher, "Cannot connect to the Docker daemon at unix:///var/run/docker.sock.");
        Assert.Matches(matcher, "failed to connect to the docker API at npipe:////./pipe/dockerDesktopLinuxEngine");

        // An unrelated docker failure must not be swallowed by the card.
        Assert.DoesNotMatch(matcher, "Error response from daemon: No such container: web");
    }

    /// <summary>
    /// The card is only useful if the reader can act without leaving: it names
    /// the cause and offers the retry that a raw error never did.
    /// </summary>
    [Fact]
    public void TheDockerUnreachableCardOffersARetry()
    {
        var card = FunctionBody("function dockerDownCard(");
        Assert.Contains("data-docker-retry", card, StringComparison.Ordinal);
        Assert.Contains("dk.downTitle", card, StringComparison.Ordinal);

        var hint = FunctionBody("function dockerDownHint(");
        foreach (var key in new[] { "dk.downPerm", "dk.downMissing", "dk.downStopped" })
        {
            Assert.Contains(key, hint, StringComparison.Ordinal);
        }

        // The retry has to run through the busy helper too — it is itself a
        // server call, and a dead-looking retry button is the original bug.
        Assert.Contains("runBusy(button,t(\"dk.retrying\")", Script, StringComparison.Ordinal);
    }

    /// <summary>
    /// table() escapes its own empty message. A caller that escapes first shows
    /// the reader <c>&amp;#39;</c> in the middle of a failure message.
    /// </summary>
    [Fact]
    public void NoCallerDoubleEscapesTheEmptyTableMessage()
    {
        Assert.DoesNotContain("[],[],esc(ex.message)", Script, StringComparison.Ordinal);
    }

    /// <summary>
    /// A confirm button carrying a busyLabel holds its dialog open and reports on
    /// itself. Closing first and working afterwards is what left a prune or a
    /// restore running behind an unchanged page.
    /// </summary>
    [Fact]
    public void AModalConfirmCanReportItsOwnProgress()
    {
        var body = FunctionBody("function setModalButtons(");

        Assert.Contains("b.busyLabel", body, StringComparison.Ordinal);
        Assert.Contains("runBusy(btn,b.busyLabel", body, StringComparison.Ordinal);
        // Cancel must go inert too, or it closes the dialog mid-flight.
        Assert.Contains("siblings.forEach(el=>el.disabled=true)", body, StringComparison.Ordinal);
        Assert.Contains("siblings.forEach(el=>el.disabled=false)", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The calls that take long enough for a user to doubt them: two slow
    /// password hashes, a full re-hash of the audit chain, a GitHub round trip,
    /// and the two docker operations measured in minutes.
    /// </summary>
    [Theory]
    [InlineData("$(\"#password-form\").addEventListener(\"submit\"", "se.pwChanging")]
    [InlineData("$(\"#btn-au-verify\").onclick=", "au.verifying")]
    [InlineData("$(\"#settings-form\").addEventListener(\"submit\"", "se.connecting")]
    [InlineData("$(\"#btn-prune\").onclick=", "im.pruning")]
    public void EverySlowActionNamesWhatItIsDoing(string declaration, string busyKey)
    {
        var handler = HandlerSource(declaration);

        Assert.Contains(busyKey, handler, StringComparison.Ordinal);
        Assert.True(
            handler.Contains("runBusy(", StringComparison.Ordinal)
            || handler.Contains("busyLabel:", StringComparison.Ordinal),
            $"'{declaration}' makes a slow call without a busy state.");
    }

    /// <summary>
    /// The auto-refresh swallows its errors so one failing endpoint cannot toast
    /// every five seconds. That silence also covered the server going away
    /// entirely, leaving minute-old numbers on screen looking live — the worst
    /// version of "has this frozen?". Losing contact is a state, not a toast.
    /// </summary>
    [Fact]
    public void LosingTheServerIsShownUntilItComesBack()
    {
        Assert.Contains("id=\"offline-banner\"", Html, StringComparison.Ordinal);

        // Driven from api() itself: only fetch knows the difference between a
        // server that answered badly and one that did not answer at all.
        var api = FunctionBody("async function api(");
        Assert.Contains("setOffline(true)", api, StringComparison.Ordinal);
        Assert.Contains("setOffline(false)", api, StringComparison.Ordinal);
        Assert.Contains("unreachable.offline=true", api, StringComparison.Ordinal);

        var render = FunctionBody("function renderOffline(");
        Assert.Contains("err.offlineBanner", render, StringComparison.Ordinal);
        Assert.Contains("class=\"spin\"", render, StringComparison.Ordinal);

        // The banner says when contact was lost, so the staleness is legible.
        var setter = FunctionBody("function setOffline(");
        Assert.Contains("offlineSince=lost?new Date():null", setter, StringComparison.Ordinal);
    }

    /// <summary>
    /// The row-action buttons are a few characters wide, so they take a spinner
    /// in place of their label. An empty busyLabel has to mean that, and not fall
    /// through to the default "Working…" that would reflow the whole cell.
    /// </summary>
    [Fact]
    public void AnEmptyBusyLabelMeansSpinnerOnly()
    {
        var body = FunctionBody("async function runBusy(");

        Assert.Contains("busyLabel===\"\"", body, StringComparison.Ordinal);
        // ?? not ||, or an empty label would take the default anyway.
        Assert.Contains("busyLabel??t(\"c.working\")", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Three calls the operator waits on that used to grey their button out with
    /// the label unchanged — which is what a control that has stopped working
    /// looks like.
    /// </summary>
    [Theory]
    // Stopping or restarting a container: seconds of real docker work.
    [InlineData("$(\"#containers-table\").addEventListener(\"click\"", "runBusy(b,\"\"")]
    // Running a backup now: dumps a whole database before it answers.
    [InlineData("$(\"#backups-list\").addEventListener(\"click\"", "bk.running")]
    // Testing a remote host: an SSH connect, or its timeout, in silence.
    [InlineData("$(\"#envs-table\").addEventListener(\"click\"", "eh.testing")]
    public void TheSlowRowActionsReportWhileTheyRun(string declaration, string expected)
    {
        var handler = HandlerSource(declaration);

        Assert.Contains("runBusy(", handler, StringComparison.Ordinal);
        Assert.Contains(expected, handler, StringComparison.Ordinal);
    }

    /// <summary>
    /// A bulk action is one docker call per selected container, so restarting a
    /// dozen runs for a while with the whole toolbar live. A second click fired
    /// the entire batch again, against containers the first was still working on.
    /// </summary>
    [Fact]
    public void ABulkContainerActionLocksTheWholeToolbar()
    {
        var handler = HandlerSource("$(\"#ct-bulk\").addEventListener(\"click\"");

        Assert.Contains("runBusy(b,busyLabel,run)", handler, StringComparison.Ordinal);
        Assert.Contains("others.forEach(el=>el.disabled=true)", handler, StringComparison.Ordinal);
        Assert.Contains("others.forEach(el=>el.disabled=false)", handler, StringComparison.Ordinal);

        // kill and remove go through a confirm, which reports on itself instead.
        Assert.Contains("danger:true,busyLabel", handler, StringComparison.Ordinal);
    }

    /// <summary>
    /// The restore is reached through a delegated click handler rather than its
    /// own function, so it is checked against the script directly.
    /// </summary>
    [Fact]
    public void RestoringABackupHoldsItsDialogUntilItFinishes()
    {
        Assert.Contains("busyLabel:t(\"bk.restoring\")", Script, StringComparison.Ordinal);
        // The close has to follow the call, not precede it.
        var restore = Script[Script.IndexOf("busyLabel:t(\"bk.restoring\")", StringComparison.Ordinal)..];
        var call = restore.IndexOf("/api/backups/restore", StringComparison.Ordinal);
        var close = restore.IndexOf("closeModal()", StringComparison.Ordinal);
        Assert.True(call < close, "the restore dialog closes before the restore is done.");
    }
}
