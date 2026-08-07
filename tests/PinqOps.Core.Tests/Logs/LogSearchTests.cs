using PinqOps.Logs;
using Xunit;

namespace PinqOps.Tests.Logs;

/// <summary>
/// Searching collected logs. The scan runs newest first because the question people
/// bring to a log is almost always "what just happened".
/// </summary>
public class LogSearchTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Newest first, which is the order the caller reads them in.</summary>
    private static IReadOnlyList<LogLine> Lines() =>
    [
        new("web", Now, "GET /health 200"),
        new("web", Now.AddMinutes(-1), "GET /login 500 Internal Server Error"),
        new("db", Now.AddMinutes(-2), "checkpoint complete"),
        new("web", Now.AddHours(-2), "GET /old 200"),
    ];

    [Fact]
    public void WithNoQueryEverythingComesBackNewestFirst()
    {
        var result = LogSearch.Run(Lines(), new LogQuery());

        Assert.Equal(4, result.Lines.Count);
        Assert.Equal("GET /health 200", result.Lines[0].Text);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void ASubstringIsMatchedWithoutCaseMattering()
    {
        var result = LogSearch.Run(Lines(), new LogQuery(Query: "internal server"));

        Assert.Single(result.Lines);
        Assert.Contains("500", result.Lines[0].Text);
    }

    [Fact]
    public void OneContainerCanBeAskedAboutOnItsOwn()
    {
        var result = LogSearch.Run(Lines(), new LogQuery(Container: "db"));

        Assert.Single(result.Lines);
        Assert.Equal("db", result.Lines[0].Container);
    }

    [Fact]
    public void ARegularExpressionIsMatchedWhenAskedFor()
    {
        var result = LogSearch.Run(Lines(), new LogQuery(Query: @"GET /\w+ 5\d\d", Regex: true));

        Assert.Single(result.Lines);
    }

    /// <summary>
    /// The half-finished states of a regex being typed are mostly invalid, and each
    /// of them must produce "that is not a pattern yet" rather than a stack trace.
    /// </summary>
    [Theory]
    [InlineData("(unclosed")]
    [InlineData("[")]
    [InlineData("*")]
    public void AHalfTypedPatternIsAnAnswerRatherThanAnException(string pattern)
    {
        var result = LogSearch.Run(Lines(), new LogQuery(Query: pattern, Regex: true));

        Assert.NotNull(result.Problem);
        Assert.Empty(result.Lines);
    }

    [Fact]
    public void TheSameTextAsASubstringIsNotAPattern()
    {
        // Without Regex the query is taken literally, so a stray bracket finds
        // nothing rather than failing.
        var result = LogSearch.Run(Lines(), new LogQuery(Query: "(unclosed"));

        Assert.Null(result.Problem);
        Assert.Empty(result.Lines);
    }

    /// <summary>
    /// The scan is newest first, so the first line older than the cutoff means every
    /// remaining line is older still.
    /// </summary>
    [Fact]
    public void ASinceCutoffStopsTheScanRatherThanFilteringPastIt()
    {
        var result = LogSearch.Run(Lines(), new LogQuery(Since: Now.AddMinutes(-5)));

        Assert.Equal(3, result.Lines.Count);
        Assert.DoesNotContain(result.Lines, line => line.Text == "GET /old 200");
    }

    [Fact]
    public void TheLimitIsRespectedAndTheCutIsReported()
    {
        var result = LogSearch.Run(Lines(), new LogQuery(Limit: 2));

        Assert.Equal(2, result.Lines.Count);
        // Otherwise the last line shown reads as the last line there is.
        Assert.True(result.Truncated);
    }

    [Fact]
    public void ExactlyEnoughLinesIsNotATruncation()
    {
        var result = LogSearch.Run(Lines(), new LogQuery(Limit: 4));

        Assert.Equal(4, result.Lines.Count);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void AnAbsurdLimitIsClamped()
    {
        var many = Enumerable.Range(0, LogSearch.MaximumLimit + 500)
            .Select(index => new LogLine("web", Now.AddSeconds(-index), $"line {index}"));

        var result = LogSearch.Run(many, new LogQuery(Limit: int.MaxValue));

        Assert.Equal(LogSearch.MaximumLimit, result.Lines.Count);
    }

    [Fact]
    public void ALimitOfZeroStillReturnsALine() =>
        Assert.Single(LogSearch.Run(Lines(), new LogQuery(Limit: 0)).Lines);

    /// <summary>
    /// It stops as soon as it has enough, which on a log of a million lines is the
    /// difference between an answer and a timeout.
    /// </summary>
    [Fact]
    public void TheScanStopsRatherThanReadingEverything()
    {
        var read = 0;

        IEnumerable<LogLine> Counted()
        {
            foreach (var index in Enumerable.Range(0, 1_000_000))
            {
                read++;
                yield return new LogLine("web", Now.AddSeconds(-index), $"line {index}");
            }
        }

        LogSearch.Run(Counted(), new LogQuery(Limit: 10));

        Assert.True(read < 100, $"read {read} lines to return 10");
    }
}
