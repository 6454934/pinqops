using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// The dashboard reports a failed request by printing the server's own message,
/// and the server writes them in English. A Turkish user therefore reads Turkish
/// everywhere until something goes wrong, at which point the answer arrives in
/// another language — which is exactly when they most need to read it.
///
/// The bridge is the API_TR table in the page: patterns matched against the
/// incoming message. Nothing made a new server message join that list, so it
/// drifted silently — eighty of them by the time anyone looked. This test is
/// what makes the list keep up: add an operator-facing message, and it fails
/// until that message can be read in both languages.
/// </summary>
public class ApiMessageTranslationTests
{
    /// <summary>
    /// Messages that never reach the dashboard, so a translation would be dead
    /// weight. Each is an internal invariant — a broken build or a malformed
    /// catalog entry is a bug report, not something an operator acts on.
    /// </summary>
    private static readonly string[] NotOperatorFacing =
    [
        "Embedded dashboard page",
        "Malformed credential placeholder in catalog entry",
        // Thrown while routes are being mapped, so the process does not finish
        // starting and no request ever sees them. They exist to turn a typo in a
        // route's resource gate into a startup failure rather than a route that
        // quietly governs nothing — which is a bug report, not something an
        // operator acts on.
        "is not a known resource kind",
        "is not a valid access level",
    ];

    /// <summary>
    /// What an interpolated hole holds in practice. A pattern written for a port
    /// says <c>(\d+)</c> and one written for a name says <c>(.+)</c>; the test
    /// cannot know which, so a message counts as covered when any of these
    /// shapes matches — which is the same as the pattern describing it.
    /// </summary>
    private static readonly string[] HoleShapes = ["7", "web", "/opt/pinqops/app/docker-compose.yml"];

    /// <summary>
    /// The patterns in the page's API_TR table, as .NET regexes. They are written
    /// for JavaScript, but the constructs used — anchors, groups, classes,
    /// escapes — mean the same thing in both engines.
    /// </summary>
    private static IReadOnlyList<Regex> TranslationPatterns()
    {
        var script = DashboardSource.Script;
        var start = script.IndexOf("const API_TR=[", StringComparison.Ordinal);
        Assert.True(start >= 0, "the API_TR translation table is missing from the dashboard.");

        var table = script[start..script.IndexOf("\n];", start, StringComparison.Ordinal)];
        var patterns = new List<Regex>();
        foreach (Match entry in Regex.Matches(table, @"\[\s*/(.+?)/\s*,"))
        {
            try
            {
                patterns.Add(new Regex(entry.Groups[1].Value, RegexOptions.None, TimeSpan.FromSeconds(1)));
            }
            catch (ArgumentException)
            {
                // A construct .NET spells differently. Skipping keeps this test
                // about coverage rather than about regex dialects.
            }
        }

        Assert.NotEmpty(patterns);
        return patterns;
    }

    /// <summary>
    /// Every message the web project can hand to the exception types
    /// <see cref="ApiExceptionFilter"/> turns into an <c>{ error }</c> response.
    /// </summary>
    public static TheoryData<string, bool, string> ServerMessages()
    {
        const string Thrown =
            @"new (?:ArgumentException|InvalidOperationException|KeyNotFoundException"
            + @"|UnauthorizedAccessException|GitHubApiException)\(\s*(\$?)""((?:[^""\\]|\\.)*)""";

        var web = Path.Combine(RepositoryRoot(), "src", "PinqOps.Web");
        var data = new TheoryData<string, bool, string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(web, "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file), Thrown))
            {
                var literal = match.Groups[2].Value.Replace("\\\"", "\"", StringComparison.Ordinal);
                if (!Regex.IsMatch(literal, "[a-z]{4,}") || !seen.Add(literal))
                {
                    continue;
                }

                data.Add(literal, match.Groups[1].Value.Length > 0, Path.GetFileName(file));
            }
        }

        Assert.NotEmpty(data);
        return data;
    }

    [Theory]
    [MemberData(nameof(ServerMessages))]
    public void EveryOperatorFacingMessageCanBeReadInTurkish(string literal, bool interpolated, string file)
    {
        if (NotOperatorFacing.Any(skip => literal.Contains(skip, StringComparison.Ordinal)))
        {
            return;
        }

        var patterns = TranslationPatterns();
        var covered = Candidates(literal, interpolated)
            .Any(candidate => patterns.Any(pattern => pattern.IsMatch(candidate)));

        Assert.True(
            covered,
            $"{file} sends a message with no Turkish translation, so a Turkish user reads it in "
            + $"English. Add a pattern to API_TR in index.html for:{Environment.NewLine}  "
            + Render(literal, "..."));
    }

    /// <summary>
    /// Having a translation is not the same as rendering one. A replacement written
    /// as a plain string carries the captured values as <c>$1</c>/<c>$2</c>, so it
    /// has to go back through the regex; handed to the caller as it stands, a
    /// Turkish reader gets a literal "$1" exactly where the container name, the port
    /// or the count belongs. The coverage test above passes either way, which is how
    /// 39 messages shipped like that.
    /// </summary>
    [Fact]
    public void AStringTranslationIsExpanded()
    {
        Assert.Contains(
            "msg.replace(re,rep)",
            DashboardSource.Script,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// And the placeholders have to name groups their own pattern captures — a
    /// <c>$2</c> against a one-group pattern expands to nothing at all.
    /// </summary>
    [Fact]
    public void EveryPlaceholderNamesAGroupItsPatternCaptures()
    {
        var checked_ = 0;
        foreach (var (pattern, replacement) in StringTranslations())
        {
            var groups = pattern.GetGroupNumbers().Max();
            foreach (Match placeholder in Regex.Matches(replacement, @"\$(\d)"))
            {
                var wanted = int.Parse(placeholder.Groups[1].Value, CultureInfo.InvariantCulture);
                checked_++;
                Assert.True(
                    wanted <= groups,
                    $"the translation \"{replacement}\" uses ${wanted}, but /{pattern}/ captures {groups} group(s).");
            }
        }

        // The table is meant to be full of these; zero would mean the parser stopped
        // finding them and the assertion above stopped being about anything.
        Assert.True(checked_ > 0, "no $N placeholders were found in API_TR — has the table's shape changed?");
    }

    /// <summary>
    /// The API_TR entries whose replacement is a plain string, as (pattern,
    /// replacement) pairs. Entries whose replacement is a function build their own
    /// text from the match and need none of this.
    /// </summary>
    private static IReadOnlyList<(Regex Pattern, string Replacement)> StringTranslations()
    {
        var script = DashboardSource.Script;
        var start = script.IndexOf("const API_TR=[", StringComparison.Ordinal);
        Assert.True(start >= 0, "the API_TR translation table is missing from the dashboard.");

        var table = script[start..script.IndexOf("\n];", start, StringComparison.Ordinal)];
        var pairs = new List<(Regex, string)>();
        foreach (Match entry in Regex.Matches(table, """\[\s*/(.+?)/\s*,\s*"((?:[^"\\]|\\.)*)"\s*\],"""))
        {
            try
            {
                pairs.Add((
                    new Regex(entry.Groups[1].Value, RegexOptions.None, TimeSpan.FromSeconds(1)),
                    entry.Groups[2].Value));
            }
            catch (ArgumentException)
            {
                // A construct .NET spells differently, as above.
            }
        }

        return pairs;
    }

    /// <summary>The message as the page could receive it, once per hole shape.</summary>
    private static IEnumerable<string> Candidates(string literal, bool interpolated) =>
        interpolated ? HoleShapes.Select(shape => Render(literal, shape)) : [literal];

    /// <summary>
    /// Fills a C# interpolated string's holes with <paramref name="shape"/>.
    /// Done in one pass so <c>{{</c> and <c>}}</c> — an escaped literal brace,
    /// which really does reach the user as <c>${IMAGE_TAG}</c> — are never
    /// mistaken for an interpolation.
    /// </summary>
    private static string Render(string literal, string shape) =>
        Regex.Replace(literal, @"\{\{|\}\}|\{[^{}]*\}", match => match.Value switch
        {
            "{{" => "{",
            "}}" => "}",
            _ => shape,
        });

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pinqops.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
