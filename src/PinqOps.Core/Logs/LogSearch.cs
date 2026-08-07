using System.Text.RegularExpressions;

namespace PinqOps.Logs;

/// <summary>One collected line.</summary>
public sealed record LogLine(string Container, DateTimeOffset At, string Text);

/// <summary>What to look for.</summary>
/// <param name="Query">Substring, or a regular expression when <paramref name="Regex"/> is set.</param>
/// <param name="Since">Only lines at or after this instant.</param>
/// <param name="Limit">How many to return, newest first.</param>
public sealed record LogQuery(
    string? Container = null,
    string? Query = null,
    bool Regex = false,
    DateTimeOffset? Since = null,
    int Limit = LogSearch.DefaultLimit);

/// <summary>What a search found, and whether it stopped early.</summary>
public sealed record LogSearchResult(IReadOnlyList<LogLine> Lines, bool Truncated, string? Problem);

/// <summary>
/// Searching the collected logs.
///
/// <para><b>Newest first, and it stops when it has enough.</b> The question people
/// bring to a log is almost always "what just happened", so the scan runs backwards
/// and returns as soon as the limit is met — on a log of a million lines that is the
/// difference between an answer and a timeout.</para>
///
/// <para><b>A bad regular expression is an answer, not an exception.</b> Somebody
/// types one into a search box; the half-finished states of a regex being typed are
/// mostly invalid, and each of them must produce "that is not a pattern yet" rather
/// than a stack trace.</para>
/// </summary>
public static class LogSearch
{
    public const int DefaultLimit = 200;

    public const int MaximumLimit = 2_000;

    /// <summary>
    /// How long a pattern may run before it is abandoned. A regular expression can
    /// backtrack for minutes on a line long enough, and a search box is exactly
    /// where one gets typed by accident.
    /// </summary>
    public static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Filters <paramref name="newestFirst"/> — which the caller has already read in
    /// reverse — down to what the query asks for.
    /// </summary>
    public static LogSearchResult Run(IEnumerable<LogLine> newestFirst, LogQuery query)
    {
        ArgumentNullException.ThrowIfNull(newestFirst);
        ArgumentNullException.ThrowIfNull(query);

        var limit = Math.Clamp(query.Limit, 1, MaximumLimit);

        Regex? pattern = null;
        if (query.Regex && query.Query is { Length: > 0 } expression)
        {
            try
            {
                pattern = new Regex(expression, RegexOptions.IgnoreCase, PatternTimeout);
            }
            catch (ArgumentException exception)
            {
                return new LogSearchResult([], false, $"That is not a pattern: {exception.Message}");
            }
        }

        var found = new List<LogLine>(limit);
        var truncated = false;

        foreach (var line in newestFirst)
        {
            if (found.Count >= limit)
            {
                // There was more to find; the page says so rather than implying the
                // last line shown is the last line there is.
                truncated = true;
                break;
            }

            if (query.Container is { Length: > 0 } container
                && !string.Equals(line.Container, container, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (query.Since is { } since && line.At < since)
            {
                // The scan is newest first, so everything past here is older still.
                break;
            }

            if (!Matches(line.Text, query, pattern, out var problem))
            {
                if (problem is not null)
                {
                    return new LogSearchResult(found, truncated, problem);
                }

                continue;
            }

            found.Add(line);
        }

        return new LogSearchResult(found, truncated, null);
    }

    private static bool Matches(string text, LogQuery query, Regex? pattern, out string? problem)
    {
        problem = null;

        if (query.Query is not { Length: > 0 } needle)
        {
            return true;
        }

        if (pattern is null)
        {
            return text.Contains(needle, StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            // Reported rather than skipped: a pattern that times out on one line will
            // time out on the next thousand, and silently returning fewer results
            // would look like the log not containing what it contains.
            problem = "That pattern took too long to match. Try a simpler one.";
            return false;
        }
    }
}
