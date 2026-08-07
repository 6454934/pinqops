using System.Runtime.InteropServices;
using PinqOps.Alerts;

namespace PinqOps.Web;

/// <summary>Host-level facts for the System panel: uptime, load, memory, swap, disk, CPU.</summary>
public sealed class SystemInfoService
{
    /// <summary>
    /// The most recent CPU percentage the alert sampler computed, if it has run.
    /// CPU usage is a delta between two readings, so a single caller cannot
    /// produce one on demand — and two callers keeping their own "previous"
    /// reading would race and both get it wrong. The sampler owns the pair of
    /// readings; this is where it publishes the answer.
    /// </summary>
    public double? CpuPercent { get; set; }

    public object GetInfo()
    {
        var memory = ReadMemInfo();
        var (diskTotal, diskFree) = ReadRootDisk();

        return new
        {
            hostname = Environment.MachineName,
            os = ReadOsPrettyName() ?? RuntimeInformation.OSDescription,
            kernel = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.OSArchitecture.ToString(),
            uptimeSeconds = ReadUptimeSeconds(),
            loadAverage = ReadLoadAverage(),
            memTotalKb = memory.TotalKb,
            memAvailableKb = memory.AvailableKb,
            swapTotalKb = memory.SwapTotalKb,
            swapFreeKb = memory.SwapFreeKb,
            cpuPercent = CpuPercent,
            cpuCount = Environment.ProcessorCount,
            diskTotalBytes = diskTotal,
            diskFreeBytes = diskFree,
            serverTimeUtc = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// The host half of one metric sample. <paramref name="previousCpu"/> is the
    /// caller's last reading; the new one is returned so it can be kept for the
    /// next tick.
    /// </summary>
    public (MetricSample Sample, CpuTimes? Cpu) Snapshot(DateTimeOffset at, CpuTimes? previousCpu)
    {
        var memory = ReadMemInfo();
        var (diskTotal, diskFree) = ReadRootDisk();
        var load = ReadLoadAverage();
        var cores = Math.Max(Environment.ProcessorCount, 1);

        var currentCpu = ReadCpuTimes();
        var cpuPercent = CpuTimes.PercentBusy(previousCpu, currentCpu);
        CpuPercent = cpuPercent;

        return (
            new MetricSample
            {
                At = at,
                Cpu = cpuPercent,
                Memory = Percentage(memory.TotalKb - memory.AvailableKb, memory.TotalKb),
                Swap = Percentage(memory.SwapTotalKb - memory.SwapFreeKb, memory.SwapTotalKb),
                Disk = Percentage(diskTotal - diskFree, diskTotal),
                // Divided by the core count so a threshold means the same thing on
                // a 2-core VPS and a 64-core box.
                Load1 = load is { Length: > 0 } ? load[0] / cores : null,
                Load5 = load is { Length: > 1 } ? load[1] / cores : null,
                Load15 = load is { Length: > 2 } ? load[2] / cores : null,
            },
            currentCpu);
    }

    private static double? Percentage(double? used, double? total) =>
        used is { } u && total is { } t && t > 0 ? Math.Clamp(u / t * 100, 0, 100) : null;

    /// <summary>The aggregate CPU counters, or null when /proc/stat is unreadable.</summary>
    private static CpuTimes? ReadCpuTimes()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/stat"))
            {
                return CpuTimes.Parse(line);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static double? ReadUptimeSeconds()
    {
        try
        {
            var first = File.ReadAllText("/proc/uptime").Split(' ')[0];
            return double.TryParse(first, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
                ? seconds
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static double[]? ReadLoadAverage()
    {
        try
        {
            var parts = File.ReadAllText("/proc/loadavg").Split(' ');
            return parts.Length >= 3
                ?
                [
                    double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                    double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                    double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
                ]
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    private readonly record struct MemInfo(long? TotalKb, long? AvailableKb, long? SwapTotalKb, long? SwapFreeKb);

    private static MemInfo ReadMemInfo()
    {
        try
        {
            long? total = null, available = null, swapTotal = null, swapFree = null;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                {
                    total = ParseKb(line);
                }
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                {
                    available = ParseKb(line);
                }
                else if (line.StartsWith("SwapTotal:", StringComparison.Ordinal))
                {
                    swapTotal = ParseKb(line);
                }
                else if (line.StartsWith("SwapFree:", StringComparison.Ordinal))
                {
                    swapFree = ParseKb(line);
                }

                if (total is not null && available is not null && swapTotal is not null && swapFree is not null)
                {
                    break;
                }
            }

            return new MemInfo(total, available, swapTotal, swapFree);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return default;
        }

        static long? ParseKb(string line)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && long.TryParse(parts[1], out var kb) ? kb : null;
        }
    }

    /// <summary>Free bytes on the root filesystem, or null when unknown.</summary>
    public long? RootFreeBytes() => ReadRootDisk().Free;

    private static (long? Total, long? Free) ReadRootDisk()
    {
        try
        {
            var root = new DriveInfo("/");
            return (root.TotalSize, root.AvailableFreeSpace);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, null);
        }
    }

    private static string? ReadOsPrettyName()
    {
        try
        {
            foreach (var line in File.ReadLines("/etc/os-release"))
            {
                if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
                {
                    return line["PRETTY_NAME=".Length..].Trim('"');
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }
}
