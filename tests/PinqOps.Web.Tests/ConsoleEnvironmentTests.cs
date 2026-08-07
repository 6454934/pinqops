using System.Text.RegularExpressions;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// The console must open on the daemon the panel is showing.
///
/// <para>Every other control in the container detail panel names the selected
/// server: the listing that put the container on screen came from it, and the Run
/// button an inch away from the console sends its command to it. The console
/// socket was built by hand and named nothing, so it landed on this server's own
/// daemon — the operator read "prod" in the switcher, typed a command into a
/// prompt that was in fact a local container of the same name, and the audit line
/// agreed with the machine rather than with them. A dropped table is not
/// recoverable by noticing afterwards.</para>
///
/// <para>The page has no build step and no component framework, so nothing but a
/// check like this notices when the socket URL stops naming a host again.</para>
/// </summary>
public class ConsoleEnvironmentTests
{
    /// <summary>
    /// The console path goes through <c>withEnv</c>, the one place the page adds
    /// the selected server to a URL. Spelling the query string out at this call
    /// site would work today and drift tomorrow, which is how the socket came to
    /// be the only container call that named no host.
    /// </summary>
    [Fact]
    public void TheConsoleSocketNamesTheSelectedServer()
    {
        var body = DashboardSource.FunctionBody("function toggleShell(");

        var wrapped = body.IndexOf("withEnv(", StringComparison.Ordinal);
        var path = body.IndexOf("/api/ws/containers/", StringComparison.Ordinal);

        Assert.True(path >= 0, "toggleShell no longer builds the console path");
        Assert.True(wrapped >= 0, "toggleShell opens the console socket without naming a server");
        Assert.True(wrapped < path, "toggleShell builds the console path outside withEnv, so no server is named");
    }

    /// <summary>
    /// And <c>withEnv</c> answers for that path: it only appends the server to the
    /// route families it recognises, so a console routed through it while it still
    /// ignored the WebSocket route would read as fixed and behave as before.
    /// </summary>
    [Fact]
    public void TheEnvironmentHelperRecognisesTheConsoleRoute()
    {
        var pattern = PathFamilyPattern();

        Assert.Matches(pattern, "/api/ws/containers/postgres/console");
        Assert.Matches(pattern, "/api/docker/containers");
        Assert.DoesNotMatch(pattern, "/api/settings/time-zone");
    }

    /// <summary>
    /// The literal <c>withEnv</c> tests a path against before it appends anything.
    /// </summary>
    private static string PathFamilyPattern()
    {
        var body = DashboardSource.FunctionBody("function withEnv(");

        var close = body.IndexOf("/.test(path)", StringComparison.Ordinal);
        Assert.True(close > 0, "withEnv no longer tests the path against a pattern");

        var open = body.LastIndexOf("!/", close, StringComparison.Ordinal);
        Assert.True(open >= 0, "withEnv no longer skips the paths that carry no server");

        return body[(open + 2)..close];
    }
}
