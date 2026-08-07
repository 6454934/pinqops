namespace PinqOps.Traffic;

/// <summary>What one route did over the window.</summary>
public sealed record RouteSummary(string Route, int Requests, int Errors, double P95Seconds);

/// <summary>What one country sent. Only ever produced when the requests carried one.</summary>
public sealed record CountrySummary(string Country, int Requests);

/// <summary>One domain's traffic over the window.</summary>
/// <param name="HasCountries">
/// False when nothing carried a country header, so the page can leave the column out
/// rather than show an empty one that reads as "no visitors".
/// </param>
public sealed record DomainTraffic(
    string Host,
    int Requests,
    int Errors,
    long Bytes,
    double P95Seconds,
    IReadOnlyList<RouteSummary> TopRoutes,
    IReadOnlyList<CountrySummary> Countries,
    bool HasCountries);

/// <summary>
/// Rolls access-log entries up into per-domain numbers.
///
/// <para><b>What this is not.</b> It is a summary of what the proxy saw, computed
/// from a file on this server. It is not analytics in the tracking sense: there are
/// no cookies, no identifiers, no per-visitor anything, and no request ever leaves
/// the machine. What it can say about geography is exactly what a CDN in front of it
/// already put in a header.</para>
/// </summary>
public static class TrafficRollup
{
    public const int TopRouteCount = 10;

    public const int TopCountryCount = 10;

    /// <summary>A status this counts as an error: anything the server or the route refused.</summary>
    public static bool IsError(int status) => status >= 400;

    /// <summary>Summarises every domain in <paramref name="entries"/>, busiest first.</summary>
    public static IReadOnlyList<DomainTraffic> Summarise(IEnumerable<AccessEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return
        [
            .. entries
                .GroupBy(entry => entry.Host, StringComparer.OrdinalIgnoreCase)
                .Select(SummariseOne)
                .OrderByDescending(domain => domain.Requests)
                .ThenBy(domain => domain.Host, StringComparer.Ordinal),
        ];
    }

    private static DomainTraffic SummariseOne(IGrouping<string, AccessEntry> domain)
    {
        var entries = domain.ToList();
        var countries = entries
            .Where(entry => entry.Country is { Length: 2 })
            .GroupBy(entry => entry.Country!, StringComparer.Ordinal)
            .Select(group => new CountrySummary(group.Key, group.Count()))
            .OrderByDescending(country => country.Requests)
            .ThenBy(country => country.Country, StringComparer.Ordinal)
            .Take(TopCountryCount)
            .ToList();

        var routes = entries
            .GroupBy(entry => entry.Route, StringComparer.Ordinal)
            .Select(group => new RouteSummary(
                group.Key,
                group.Count(),
                group.Count(entry => IsError(entry.Status)),
                Percentile95([.. group.Select(entry => entry.DurationSeconds)])))
            .OrderByDescending(route => route.Requests)
            .ThenBy(route => route.Route, StringComparer.Ordinal)
            .Take(TopRouteCount)
            .ToList();

        return new DomainTraffic(
            domain.Key,
            entries.Count,
            entries.Count(entry => IsError(entry.Status)),
            entries.Sum(entry => entry.Bytes),
            Percentile95([.. entries.Select(entry => entry.DurationSeconds)]),
            routes,
            countries,
            // Distinguished from "nobody visited": an empty column because the
            // header is absent means something different from an empty column
            // because there was no traffic.
            countries.Count > 0);
    }

    /// <summary>
    /// The 95th percentile of a set of durations.
    ///
    /// <para><b>Not the mean.</b> A mean response time is dominated by the many fast
    /// requests and says nothing about the experience of the few slow ones — which
    /// is the only part anybody complains about. The nearest-rank method is used
    /// rather than an interpolating one: it always returns a duration that actually
    /// happened, which is easier to go and find in the log.</para>
    /// </summary>
    public static double Percentile95(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.Order().ToList();
        var rank = (int)Math.Ceiling(0.95 * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }
}
