namespace PinqOps.Traffic;

/// <summary>
/// Narrows a stream of access-log entries down to the window a summary is built
/// from: the most recent <c>maximum</c> of the ones at or after a moment.
///
/// <para><b>Why a cap at all.</b> A busy proxy writes faster than anyone reads, and
/// the alternative to a cap is a page that gets slower every day until it times
/// out.</para>
///
/// <para><b>Why it is its own function.</b> It is the part of the summary that has a
/// cost rather than a result, and a cost is not something a test can check while it
/// is spelled out inside a method that also opens a file.</para>
/// </summary>
public static class TrafficWindow
{
    /// <summary>
    /// The last <paramref name="maximum"/> entries at or after
    /// <paramref name="since"/>, oldest first.
    ///
    /// <para>Held in a queue, so dropping the oldest costs nothing. A list looks
    /// like it says the same thing and does not: removing its first element shifts
    /// every other one, so once the cap is reached each further line costs a copy of
    /// the whole window — which turns the cap from a bound into the thing that makes
    /// the read quadratic.</para>
    /// </summary>
    public static IReadOnlyList<AccessEntry> MostRecent(
        IEnumerable<AccessEntry> entries, DateTimeOffset since, int maximum)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);

        var window = new Queue<AccessEntry>();
        foreach (var entry in entries)
        {
            if (entry.At < since)
            {
                continue;
            }

            window.Enqueue(entry);
            if (window.Count > maximum)
            {
                window.Dequeue();
            }
        }

        return [.. window];
    }
}
