using System.Text.RegularExpressions;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// A fresh server is empty everywhere, so the empty state is the first thing a
/// new operator reads on most pages. "No containers." is accurate and useless:
/// it describes the situation and says nothing about how to leave it.
/// </summary>
public class DashboardEmptyStateTests
{
    /// <summary>
    /// The pages a first-time operator lands on with nothing set up yet. Each
    /// message has to point somewhere — the wording is free to change, the
    /// pointer is not.
    /// </summary>
    [Theory]
    // The install catalog is "the app catalog" now that "Apps" names the list of
    // published apps; the pointer has to name the page it actually means.
    [InlineData("ct.none", new[] { "GitHub", "catalog" })]
    [InlineData("im.none", new[] { "deploy", "app" })]
    [InlineData("st.noVolumes", new[] { "Apps", "compose" })]
    [InlineData("bk.none", new[] { "above" })]
    [InlineData("al.noRules", new[] { "above" })]
    [InlineData("pv.none", new[] { "pull request" })]
    // These three were still the bare shape this class was written to replace —
    // "No stacks yet.", "No jobs yet.", "No buckets yet." — because nothing here
    // named them.
    [InlineData("sk.none", new[] { "New stack", "compose" })]
    [InlineData("jb.none", new[] { "above", "schedule" })]
    [InlineData("jb.noRuns", new[] { "schedule" })]
    [InlineData("bk.noBuckets", new[] { "above", "offsite" })]
    public void AnEmptyPageSaysWhatToDoNext(string key, string[] pointers)
    {
        var english = English(key);

        Assert.NotNull(english);
        foreach (var pointer in pointers)
        {
            Assert.Contains(pointer, english, StringComparison.OrdinalIgnoreCase);
        }

        // A bare "No X." is the shape this replaced: one short sentence and no
        // second clause telling the reader where to go.
        Assert.True(english.Length > 30, $"'{key}' is too terse to be a useful empty state: \"{english}\"");
    }

    /// <summary>
    /// Turkish carries the same guidance. Length is the proxy: a translation
    /// that reverted to "Konteyner yok." leaves half the users where they were.
    /// </summary>
    [Theory]
    [InlineData("ct.none")]
    [InlineData("im.none")]
    [InlineData("st.noVolumes")]
    [InlineData("bk.none")]
    [InlineData("sk.none")]
    [InlineData("jb.none")]
    [InlineData("jb.noRuns")]
    [InlineData("bk.noBuckets")]
    public void TheTurkishEmptyStateIsGuidanceToo(string key)
    {
        var turkish = Turkish(key);

        Assert.NotNull(turkish);
        Assert.True(turkish.Length > 30, $"'{key}' lost its Turkish guidance: \"{turkish}\"");
    }

    private static string? English(string key) => Lookup(key, EnglishTable);

    private static string? Turkish(string key) => Lookup(key, TurkishTable);

    private static string? Lookup(string key, string table)
    {
        var match = Regex.Match(table, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string EnglishTable => Tables.English;

    private static string TurkishTable => Tables.Turkish;

    /// <summary>
    /// The two translation tables, split at the <c>tr:{</c> that opens the
    /// second. Located the same way <see cref="DashboardResourceTests"/> does it,
    /// since the file's own whitespace is not part of any contract.
    /// </summary>
    private static (string English, string Turkish) Tables { get; } = SplitTables();

    private static (string English, string Turkish) SplitTables()
    {
        var script = DashboardSource.Script;
        var starts = Regex.Matches(script, @"\n\s*(en|tr)\s*:\s*\{").Select(match => match.Index).ToList();
        Assert.Equal(2, starts.Count);

        return (script[starts[0]..starts[1]], script[starts[1]..]);
    }
}
