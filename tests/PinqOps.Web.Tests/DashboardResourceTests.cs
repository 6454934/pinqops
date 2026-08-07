using System.Text.RegularExpressions;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// The dashboard is one hand-written HTML file with no build step, so nothing
/// else would catch a translation added to one language and not the other, or a
/// <c>t("…")</c> call naming a key that does not exist. Both render as raw key
/// names in the UI rather than failing anywhere a developer would notice.
/// </summary>
public class DashboardResourceTests
{
    private static readonly string Html = LoadIndex();

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

    /// <summary>
    /// The keys defined in one language table, in order and <em>with</em> repeats,
    /// which is what makes a duplicate visible at all.
    /// </summary>
    private static List<string> KeysAt(int index)
    {
        var open = Html.IndexOf('{', index);
        var depth = 0;
        var end = open;
        while (true)
        {
            if (Html[end] == '{')
            {
                depth++;
            }
            else if (Html[end] == '}' && --depth == 0)
            {
                break;
            }

            end++;
        }

        return [.. Regex.Matches(Html[open..end], "\"([a-zA-Z0-9._]+)\"\\s*:").Select(m => m.Groups[1].Value)];
    }

    /// <summary>The keys defined in one language table, found by brace matching.</summary>
    private static HashSet<string> TableAt(int index)
    {
        var open = Html.IndexOf('{', index);
        var depth = 0;
        var end = open;
        while (true)
        {
            if (Html[end] == '{')
            {
                depth++;
            }
            else if (Html[end] == '}' && --depth == 0)
            {
                break;
            }

            end++;
        }

        return [.. Regex.Matches(Html[open..end], "\"([a-zA-Z0-9._]+)\"\\s*:").Select(m => m.Groups[1].Value)];
    }

    private static (HashSet<string> English, HashSet<string> Turkish) Tables()
    {
        var starts = Regex.Matches(Html, @"\n\s*(en|tr)\s*:\s*\{").Select(m => m.Index).ToList();
        Assert.Equal(2, starts.Count);
        return (TableAt(starts[0]), TableAt(starts[1]));
    }

    /// <summary>
    /// Every entry in the NAV array needs a matching <c>id="view-…"</c> section.
    /// A mismatch is invisible until someone clicks the nav item and lands on an
    /// empty page, because nothing here is compiled.
    /// </summary>
    [Fact]
    public void EveryNavViewHasASection()
    {
        var nav = Regex.Match(Html, @"const NAV\s*=\s*\[(.*?)\n?\];", RegexOptions.Singleline).Groups[1].Value;
        Assert.NotEmpty(nav);

        var views = Regex.Matches(nav, @"\[""([a-z]+)"",IC\.")
            .Select(match => match.Groups[1].Value)
            .ToList();
        Assert.NotEmpty(views);

        foreach (var view in views)
        {
            Assert.Contains($"id=\"view-{view}\"", Html, StringComparison.Ordinal);
            Assert.Contains($"\"nav.{view}\"", Html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BothLanguagesDefineTheSameKeys()
    {
        var (english, turkish) = Tables();

        Assert.Empty(english.Except(turkish).Order());
        Assert.Empty(turkish.Except(english).Order());
    }

    /// <summary>
    /// A table cell written as an object is read for <c>h</c> and <c>cls</c> and
    /// nothing else, so one built with any other key falls through to the object
    /// itself and the operator reads "[object Object]" where a value belongs. That
    /// happened once, with <c>t</c> — the obvious typo, since <c>t</c> is the
    /// translation function everywhere else in the file.
    ///
    /// <para>Narrow on purpose: it pins this shape, not every possible wrong key.
    /// Scoping a general check to the arguments of <c>table()</c> means matching
    /// braces through template literals that contain their own, and the loose
    /// version of it matches i18n variables, fetch options and modal buttons —
    /// dozens of false positives for one real defect.</para>
    /// </summary>
    [Fact]
    public void NoTableCellIsBuiltWithTheTranslationFunctionsName()
    {
        Assert.DoesNotContain("{t:", Html, StringComparison.Ordinal);
    }

    /// <summary>
    /// No key is defined twice in the same table.
    ///
    /// <para>A repeat is not an error in JavaScript — the later one simply wins —
    /// so the whole cost lands on whichever feature wrote the key first, which
    /// silently starts displaying the other one's words. Eight keys had gone that
    /// way, and the pair that mattered turned the Backups page's "remove schedule"
    /// confirmation into the wording for deleting a storage bucket, complete with
    /// a placeholder nothing filled in. The test above cannot see any of it: it
    /// compares the two tables as <em>sets</em>, and a key duplicated in both is
    /// present in both.</para>
    /// </summary>
    [Fact]
    public void NeitherLanguageDefinesAKeyTwice()
    {
        var starts = Regex.Matches(Html, @"\n\s*(en|tr)\s*:\s*\{").Select(match => match.Index).ToList();
        Assert.Equal(2, starts.Count);

        foreach (var (language, index) in new[] { ("en", starts[0]), ("tr", starts[1]) })
        {
            var keys = KeysAt(index);
            Assert.NotEmpty(keys);

            var repeated = keys
                .GroupBy(key => key, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => $"{group.Key} (x{group.Count()})")
                .Order(StringComparer.Ordinal)
                .ToList();

            Assert.True(
                repeated.Count == 0,
                $"the {language} table defines these keys more than once, so the later definition wins and "
                + $"whatever used the earlier one now shows the wrong words:{Environment.NewLine}  "
                + string.Join($"{Environment.NewLine}  ", repeated));
        }
    }

    [Fact]
    public void EveryTranslationKeyUsedIsDefined()
    {
        var (english, _) = Tables();
        var script = Regex.Match(Html, "<script>(.*)</script>", RegexOptions.Singleline).Groups[1].Value;

        // Only literal lookups can be checked; t("nav."+view) and friends build
        // their key at runtime and are excluded by the trailing-dot filter.
        var used = Regex.Matches(script, "t\\(\\s*\"([a-zA-Z0-9._]+)\"")
            .Select(match => match.Groups[1].Value)
            .Where(key => !key.EndsWith('.') && key.Contains('.'))
            .ToHashSet();

        Assert.Empty(used.Except(english).Order());
    }

    /// <summary>
    /// Everything marked <c>data-i18n</c> is replaced from the table at render
    /// time, so a key that does not exist shows the raw key to the user.
    /// </summary>
    [Fact]
    public void EveryMarkupTranslationKeyIsDefined()
    {
        var (english, _) = Tables();
        var used = Regex.Matches(Html, "data-i18n(?:-ph|-title)?=\"([a-zA-Z0-9._]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToHashSet();

        Assert.Empty(used.Except(english).Order());
    }

    /// <summary>
    /// The page's inline script is pinned by a CSP hash computed from the file at
    /// startup, so exactly one script block may exist — a second one could never
    /// execute, and the failure would only show at runtime in a browser.
    /// </summary>
    /// <summary>
    /// A server message shown to the operator goes through <c>trApi</c>, which is
    /// what turns it into Turkish.
    ///
    /// <para>Errors that arrive by being <em>thrown</em> already do:
    /// <c>api()</c> wraps them in <c>new Error(trApi(data.error))</c>. But a soft
    /// failure comes back in a <c>200</c> body as <c>{error: "…"}</c> — the bucket
    /// listing, a bucket browse, a finished image pull — and those were read
    /// straight out of the response and printed. A Turkish operator read
    /// "No object storage is configured." in English on the Buckets page, which is
    /// exactly what <see cref="ApiMessageTranslationTests"/> exists to prevent; it
    /// could not see this path because it follows thrown messages.</para>
    ///
    /// <para>Narrow on purpose: it pins the two shapes that actually print one —
    /// a toast and a table's empty-state — rather than every possible read of
    /// <c>.error</c>. A truthiness check like <c>if(r.error)</c> is not a
    /// rendering and is left alone.</para>
    /// </summary>
    [Fact]
    public void AServerMessageIsNeverPrintedWithoutBeingTranslated()
    {
        var script = Regex.Match(Html, "<script>(.*)</script>", RegexOptions.Singleline).Groups[1].Value;

        var raw = Regex.Matches(script, @"toast\(\s*[A-Za-z_$][\w$]*\.error\b")
            .Select(match => match.Value)
            .Concat(Regex.Matches(script, @",\s*[A-Za-z_$][\w$]*\.error\s*\)")
                .Select(match => match.Value))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            raw.Count == 0,
            "these print a server message without trApi(), so a Turkish operator reads it in English:"
            + $"{Environment.NewLine}  " + string.Join($"{Environment.NewLine}  ", raw));
    }

    [Fact]
    public void ThePageHasExactlyOneInlineScript()
    {
        Assert.Single(Regex.Matches(Html, "<script"));
        Assert.DoesNotContain("<script src=", Html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Module-level state is declared at column 0, and the ones declared
    /// <c>const</c> are containers that get mutated rather than replaced. Assigning
    /// to one throws <c>TypeError: Assignment to constant variable.</c> — but only
    /// on the line that does it, which is why nothing caught it when the per-app
    /// GitHub cache became a Map and four <c>ghCache=null</c> resets were left
    /// behind. Three of them sat after a successful POST (pasting a token, the
    /// device-flow approval, disconnecting), so the server did the work and the
    /// page then died on the next statement.
    ///
    /// <para>A parser would be more thorough, but the column-0 convention makes a
    /// plain scan exact here: it finds every real reassignment and nothing else,
    /// because inner scopes are always indented.</para>
    /// </summary>
    [Fact]
    public void NoModuleLevelConstIsAssignedTo()
    {
        var script = Regex.Match(Html, "<script>(.*)</script>", RegexOptions.Singleline).Groups[1].Value;
        var lines = script.Split('\n');

        var declared = lines
            .Select(line => Regex.Match(line, @"^const ([A-Za-z_$][\w$]*)="))
            .Where(match => match.Success)
            .Select(match => match.Groups[1].Value)
            .ToList();

        Assert.NotEmpty(declared);

        var assigned = new List<string>();
        foreach (var name in declared)
        {
            // "=" but not "==", "===" or the arrow of a default parameter, plus the
            // compound forms; a preceding word character or dot means it is some
            // other identifier (or a property) that merely ends with this name.
            var pattern = $@"(?<![\w$.]){Regex.Escape(name)}\s*(?:=(?![=>])|\+=|-=|\*=|/=|\|\|=|\?\?=)";
            for (var index = 0; index < lines.Length; index++)
            {
                if (lines[index].StartsWith($"const {name}=", StringComparison.Ordinal))
                {
                    continue; // the declaration itself
                }

                if (Regex.IsMatch(lines[index], pattern))
                {
                    assigned.Add($"{name} (script line {index + 1}): {lines[index].Trim()}");
                }
            }
        }

        Assert.True(
            assigned.Count == 0,
            "these assign to a module-level const, which throws in the browser the moment the line runs:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", assigned));
    }
}
