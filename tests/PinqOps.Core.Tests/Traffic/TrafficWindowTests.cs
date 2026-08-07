using System.Diagnostics;
using PinqOps.Traffic;
using Xunit;

namespace PinqOps.Tests.Traffic;

/// <summary>
/// The window a traffic summary is built from.
///
/// <para>The cap exists because a busy proxy writes faster than anyone reads. It
/// only does its job if reaching it is cheap — a cap that costs a copy of the whole
/// window per line past it is not a bound, it is the reason the read is slow.</para>
/// </summary>
public class TrafficWindowTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private static AccessEntry Entry(int index) =>
        new(Start.AddSeconds(index), "shop.example.com", "/", 200, 100, 0.01, null);

    private static IEnumerable<AccessEntry> Entries(int count)
    {
        for (var index = 0; index < count; index++)
        {
            yield return Entry(index);
        }
    }

    [Fact]
    public void EverythingInsideTheWindowIsKeptWhenItFits() =>
        Assert.Equal(10, TrafficWindow.MostRecent(Entries(10), Start, maximum: 100).Count);

    [Fact]
    public void AnythingBeforeTheMomentAskedAboutIsLeftOut() =>
        Assert.Equal(4, TrafficWindow.MostRecent(Entries(10), Start.AddSeconds(6), maximum: 100).Count);

    /// <summary>Over the cap it is the most recent that are kept, not the first read.</summary>
    [Fact]
    public void PastTheCapItIsTheNewestThatSurvive()
    {
        var kept = TrafficWindow.MostRecent(Entries(100), Start, maximum: 10);

        Assert.Equal(10, kept.Count);
        Assert.Equal(Entry(90), kept[0]);
        Assert.Equal(Entry(99), kept[^1]);
    }

    [Fact]
    public void TheWindowIsOldestFirst()
    {
        var kept = TrafficWindow.MostRecent(Entries(5), Start, maximum: 100);

        Assert.Equal(kept.OrderBy(entry => entry.At), kept);
    }

    /// <summary>
    /// The cost, which is the point. Dropping the oldest entry has to be free: at
    /// one copy of the window per line past the cap, a proxy that served a few
    /// million requests inside the window spends minutes here — on the dashboard's
    /// own request thread, which makes the Traffic page a way to hang the server.
    ///
    /// <para>The bound is enormously generous on purpose. Dropping from the front of
    /// a queue puts this in the low milliseconds; shifting a list puts it at 8
    /// seconds for these numbers and 32 for a million lines, measured. Nothing sits
    /// between the two, so a loaded machine cannot turn one answer into the other.</para>
    /// </summary>
    [Fact]
    public void ReachingTheCapCostsNothingPerLine()
    {
        // The cap the traffic page actually uses, because the cost is per line past
        // it multiplied by its size — a smaller one here would measure a smaller
        // problem than the one that exists.
        const int Maximum = 200_000;
        const int Lines = 600_000;

        var clock = Stopwatch.StartNew();
        var kept = TrafficWindow.MostRecent(Entries(Lines), Start, Maximum);
        clock.Stop();

        Assert.Equal(Maximum, kept.Count);
        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(5),
            $"windowing {Lines} entries down to {Maximum} took {clock.Elapsed.TotalSeconds:F1}s, "
            + "which means the oldest is being dropped by shifting the whole window");
    }
}
