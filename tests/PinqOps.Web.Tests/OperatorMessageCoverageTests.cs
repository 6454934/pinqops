using System.Text.RegularExpressions;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// Messages an operator reads that the main translation tripwire cannot see.
///
/// <para><c>ApiMessageTranslationTests</c> works by scanning exception literals
/// thrown from the web project. That is most of them, and it is why most of them
/// are covered — but it leaves two shapes out, and both had gone untranslated:
/// a message written straight into a response body rather than thrown, and one
/// that originates in the shared logic and is only surfaced by a web endpoint.
/// Listed here explicitly, because a scanner that finds them is a scanner that
/// would have to understand the whole call graph.</para>
/// </summary>
public class OperatorMessageCoverageTests
{
    /// <summary>
    /// Each message exactly as the operator receives it, with any holes filled.
    /// </summary>
    public static TheoryData<string> Messages() =>
    [
        // ApiAuthorization writes this into the 403 body rather than throwing it,
        // so nothing scans for it — and it is the most common refusal in the whole
        // product.
        "Your role or token does not grant the scope this action requires.",

        // Notifiers.ValidateHttpUrl, reached from the alert channels form. Core,
        // so out of the scanner's reach.
        "'ftp://example.com/hook' is not a valid http(s) URL.",
    ];

    [Theory]
    [MemberData(nameof(Messages))]
    public void ATurkishReaderGetsTurkish(string message)
    {
        var matched = Patterns().Any(pattern => pattern.IsMatch(message));

        Assert.True(
            matched,
            $"no API_TR pattern matches a message the operator can read:{Environment.NewLine}  {message}");
    }

    /// <summary>
    /// A validation message must not carry the parameter name .NET appends when an
    /// <see cref="ArgumentException"/> is given one. It is written for whoever
    /// called the method, and this one is read by whoever typed the URL — who is
    /// told, after the sentence, that the parameter was called "url".
    /// </summary>
    [Fact]
    public void AValidationMessageCarriesNoParameterName()
    {
        var failure = Assert.Throws<ArgumentException>(
            () => PinqOps.Notifications.WebhookNotifier.ValidateHttpUrl("ftp://example.com/hook"));

        Assert.DoesNotContain("Parameter", failure.Message, StringComparison.Ordinal);
        Assert.EndsWith("is not a valid http(s) URL.", failure.Message, StringComparison.Ordinal);
    }

    private static IReadOnlyList<Regex> Patterns()
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
                // A construct .NET spells differently; skipped, as elsewhere.
            }
        }

        Assert.NotEmpty(patterns);
        return patterns;
    }
}
