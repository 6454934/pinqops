using System.Text.RegularExpressions;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// The dashboard is one page with one inline script, so a single syntax error in it
/// is not a broken feature — it is a blank product. The browser refuses the whole
/// script, every handler is undefined, and nothing on any page works.
///
/// <para>That shipped. Two string literals were opened with a double quote and left
/// to run onto the next line, which JavaScript does not allow, and the page went out
/// dead: <c>Uncaught SyntaxError: Invalid or unexpected token</c> and an empty
/// dashboard. Nothing in the suite reads the script as code — the checks over this
/// file look for keys, handlers and links — so nothing noticed.</para>
///
/// <para>This is the cheap half of the answer: the shape that actually got through.
/// A quote opened at the end of a line, after an assignment, a concatenation or an
/// argument, is never something anybody writes on purpose, and it is exactly how both
/// of them looked. Parsing the script properly needs a JavaScript engine the test
/// project does not have; this needs nothing and would have caught both.</para>
/// </summary>
public class DashboardScriptSyntaxTests
{
    /// <summary>
    /// A quote that opens at the end of a line, right after the operator that was
    /// meant to be given a string.
    /// </summary>
    private static readonly Regex QuoteLeftOpenAtEndOfLine = new(
        """(?<lead>[=+(,\[:]|\+=)[ \t]*"[ \t]*\r?$""",
        RegexOptions.Compiled | RegexOptions.Multiline);

    [Fact]
    public void NoStringLiteralIsLeftOpenAtTheEndOfALine()
    {
        var script = DashboardSource.Html;

        var offenders = QuoteLeftOpenAtEndOfLine.Matches(script)
            .Select(match => script.Take(match.Index).Count(character => character == '\n') + 1)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"a string literal is opened and never closed on line(s) {string.Join(", ", offenders)} — "
            + "the whole inline script fails to parse, so the page renders nothing at all");
    }
}
