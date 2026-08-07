using System.Globalization;

namespace PinqOps.Alerts;

/// <summary>
/// Parsers for the strings docker prints and the counters Linux exposes.
///
/// Every number here is parsed with <see cref="CultureInfo.InvariantCulture"/>,
/// explicitly. Docker always prints <c>12.34%</c> with a dot, and
/// <c>PinqOps.Core</c> — unlike <c>PinqOps.Web</c> — does not set
/// <c>InvariantGlobalization</c>, so on a Turkish or German host the ambient
/// culture would read <c>12.34</c> as <c>1234</c>: a hundredfold overstatement
/// that would fire every rule on the machine.
/// </summary>
public static class MetricParsing
{
    private const NumberStyles Style = NumberStyles.Float | NumberStyles.AllowThousands;

    /// <summary>
    /// A docker percentage such as <c>12.34%</c>. Returns null for the placeholders
    /// docker uses when it has nothing to report (<c>--</c>, <c>N/A</c>) and for
    /// anything unparseable, because "no reading" and "zero" must not be confused.
    /// </summary>
    public static double? Percent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim().TrimEnd('%').Trim();
        if (trimmed.Length == 0 || trimmed is "--" or "N/A" or "n/a")
        {
            return null;
        }

        return double.TryParse(trimmed, Style, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : null;
    }

    /// <summary>
    /// A docker size such as <c>1.5GiB</c> or <c>1.5 GB</c>, in bytes. Both unit
    /// families appear depending on the docker version and the column.
    /// </summary>
    public static double? Bytes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (trimmed is "--" or "N/A" or "n/a")
        {
            return null;
        }

        var digits = 0;
        while (digits < trimmed.Length
            && (char.IsAsciiDigit(trimmed[digits]) || trimmed[digits] == '.' || trimmed[digits] == '-'))
        {
            digits++;
        }

        if (digits == 0)
        {
            return null;
        }

        if (!double.TryParse(
                trimmed[..digits], Style, CultureInfo.InvariantCulture, out var number)
            || !double.IsFinite(number))
        {
            return null;
        }

        var unit = trimmed[digits..].Trim();
        var multiplier = unit.ToUpperInvariant() switch
        {
            "" or "B" => 1d,
            "KB" or "KIB" or "K" => 1024d,
            "MB" or "MIB" or "M" => 1024d * 1024,
            "GB" or "GIB" or "G" => 1024d * 1024 * 1024,
            "TB" or "TIB" or "T" => 1024d * 1024 * 1024 * 1024,
            "PB" or "PIB" or "P" => 1024d * 1024 * 1024 * 1024 * 1024,
            _ => -1d,
        };

        return multiplier < 0 ? null : number * multiplier;
    }

    /// <summary>
    /// Docker's <c>MemUsage</c> column, <c>1.5GiB / 4GiB</c>, as used and limit.
    /// </summary>
    public static (double? Used, double? Limit) MemUsage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null);
        }

        var parts = text.Split('/', 2);
        return parts.Length == 2
            ? (Bytes(parts[0]), Bytes(parts[1]))
            : (Bytes(parts[0]), null);
    }

    /// <summary>
    /// Memory as a percentage of the container's limit. Docker's own
    /// <c>MemPerc</c> is preferred; when the container has no memory limit docker
    /// prints <c>--</c> there, so fall back to used over limit.
    /// </summary>
    public static double? MemoryPercent(string? memPerc, string? memUsage)
    {
        if (Percent(memPerc) is { } reported)
        {
            return reported;
        }

        var (used, limit) = MemUsage(memUsage);
        return used is { } u && limit is { } l && l > 0 ? u / l * 100 : null;
    }

    /// <summary>
    /// Whether docker's <c>Status</c> column says the container is running. A
    /// container that is restarting is not up, whatever the transient wording.
    /// </summary>
    public static bool IsRunning(string? state, string? status)
    {
        if (!string.IsNullOrWhiteSpace(state))
        {
            return string.Equals(state.Trim(), "running", StringComparison.OrdinalIgnoreCase);
        }

        return status?.TrimStart().StartsWith("Up", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Whether the container's health check is failing. <c>(health: starting)</c>
    /// is deliberately not unhealthy — a container that has only just come up
    /// would otherwise page somebody on every single deploy.
    /// </summary>
    public static bool IsUnhealthy(string? status) =>
        status?.Contains("(unhealthy)", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Whether docker is restarting the container right now.</summary>
    public static bool IsRestarting(string? state, string? status)
    {
        if (!string.IsNullOrWhiteSpace(state)
            && string.Equals(state.Trim(), "restarting", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return status?.TrimStart().StartsWith("Restarting", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// The first name from docker's <c>Names</c> column, which is comma-joined
    /// when a container has aliases. <c>docker stats</c> reports only the first,
    /// so using it here keeps the two listings keyed the same way.
    /// </summary>
    public static string FirstName(string? names)
    {
        if (string.IsNullOrWhiteSpace(names))
        {
            return string.Empty;
        }

        var comma = names.IndexOf(',', StringComparison.Ordinal);
        return (comma < 0 ? names : names[..comma]).Trim();
    }
}

/// <summary>
/// A reading of the aggregate CPU counters from the first line of
/// <c>/proc/stat</c>. Meaningless alone — CPU usage is the difference between two
/// readings — which is why the arithmetic is separated from the file access and
/// tested on its own.
/// </summary>
public readonly record struct CpuTimes(double Idle, double Total)
{
    /// <summary>
    /// Parses <c>cpu  123 456 789 …</c>. Field 4 is idle and field 5 is iowait;
    /// both count as not-busy. Kernels differ in how many fields follow, so
    /// everything present is summed rather than a fixed count being assumed.
    /// </summary>
    public static CpuTimes? Parse(string? procStatLine)
    {
        if (string.IsNullOrWhiteSpace(procStatLine))
        {
            return null;
        }

        var parts = procStatLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5 || !parts[0].Equals("cpu", StringComparison.Ordinal))
        {
            return null;
        }

        double total = 0;
        double idle = 0;
        for (var index = 1; index < parts.Length; index++)
        {
            if (!double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var field))
            {
                return null;
            }

            total += field;
            if (index is 4 or 5)
            {
                idle += field;
            }
        }

        return new CpuTimes(idle, total);
    }

    /// <summary>
    /// Busy percentage between two readings, or null when there is nothing to
    /// compare — the first tick after start, or a counter that has not advanced.
    /// A fabricated 0% there would read as "the machine is idle".
    /// </summary>
    public static double? PercentBusy(CpuTimes? previous, CpuTimes? current)
    {
        if (previous is not { } before || current is not { } after)
        {
            return null;
        }

        var total = after.Total - before.Total;
        if (total <= 0)
        {
            return null;
        }

        var idle = after.Idle - before.Idle;
        return Math.Clamp((1 - (idle / total)) * 100, 0, 100);
    }
}
