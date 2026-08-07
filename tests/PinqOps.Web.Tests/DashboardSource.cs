using System.Text.RegularExpressions;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// Reads the dashboard page and pulls named pieces out of its single inline
/// script. The page has no build step and no component framework, so these
/// checks are the only thing standing between a refactor and a behaviour that
/// silently stops happening in a browser.
/// </summary>
internal static class DashboardSource
{
    public static string Html { get; } = LoadIndex();

    public static string Script { get; } =
        Regex.Match(Html, "<script>(.*)</script>", RegexOptions.Singleline).Groups[1].Value;

    private static string LoadIndex()
    {
        // Walk up to the repository root; the test binary runs from bin/.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pinqops.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, "src", "PinqOps.Web", "wwwroot", "index.html"));
    }

    /// <summary>The body of a named function or handler, by brace matching.</summary>
    public static string FunctionBody(string declaration)
    {
        var start = Script.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{declaration}' is missing from the dashboard script.");
        return BlockAt(BodyBrace(start));
    }

    /// <summary>
    /// The brace that opens the body, skipping the parameter list. A default
    /// parameter puts a brace inside the signature — <c>api(path,opts={})</c> —
    /// and taking the first one found an empty <c>{}</c> instead of the function.
    /// </summary>
    private static int BodyBrace(int declarationStart)
    {
        var open = Script.IndexOf('(', declarationStart);
        if (open < 0)
        {
            return Script.IndexOf('{', declarationStart);
        }

        var depth = 0;
        for (var index = open; index < Script.Length; index++)
        {
            if (Script[index] == '(')
            {
                depth++;
            }
            else if (Script[index] == ')' && --depth == 0)
            {
                return Script.IndexOf('{', index);
            }
        }

        return Script.IndexOf('{', declarationStart);
    }

    /// <summary>
    /// A whole top-level handler, declaration included. Brace matching is no use
    /// for the arrow-expression ones — <c>onclick=()=&gt;openModal(…,[{…}])</c>
    /// opens its first brace inside an argument — and every handler in this file
    /// starts at column zero, so the next one marks the end of this one.
    /// </summary>
    public static string HandlerSource(string declaration)
    {
        var start = Script.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{declaration}' is missing from the dashboard script.");

        var next = Script.IndexOf("\n$(\"#", start + declaration.Length, StringComparison.Ordinal);
        return next < 0 ? Script[start..] : Script[start..next];
    }

    private static string BlockAt(int open)
    {
        var depth = 0;
        var end = open;
        while (true)
        {
            if (Script[end] == '{')
            {
                depth++;
            }
            else if (Script[end] == '}' && --depth == 0)
            {
                break;
            }

            end++;
        }

        return Script[open..(end + 1)];
    }
}
