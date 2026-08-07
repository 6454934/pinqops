namespace PinqOps.Alerts;

/// <summary>One container's readings at one instant.</summary>
public sealed record ContainerMetrics
{
    public required string Name { get; init; }

    /// <summary>CPU percent, or null when docker reported none.</summary>
    public double? Cpu { get; init; }

    /// <summary>Memory percent of the container's limit, or null when unknown.</summary>
    public double? Memory { get; init; }

    /// <summary>The container exists but is not running.</summary>
    public bool Down { get; init; }

    /// <summary>The container has a health check and it is failing.</summary>
    public bool Unhealthy { get; init; }

    /// <summary>Docker is restarting the container right now.</summary>
    public bool Restarting { get; init; }
}

/// <summary>
/// Everything the alert evaluator needs from one tick, flattened so that
/// <see cref="Value"/> is the single place a metric id turns into a number — and
/// therefore the single place "no reading" is decided.
/// </summary>
public sealed record MetricSample
{
    public required DateTimeOffset At { get; init; }

    /// <summary>Host CPU percent. Null on the first tick after start (no previous /proc/stat reading to compare against).</summary>
    public double? Cpu { get; init; }

    /// <summary>Host memory used, as a percentage of total.</summary>
    public double? Memory { get; init; }

    /// <summary>Host swap used, as a percentage of total. Null when there is no swap.</summary>
    public double? Swap { get; init; }

    /// <summary>Root filesystem used, as a percentage of its size.</summary>
    public double? Disk { get; init; }

    /// <summary>Load average over 1/5/15 minutes, divided by the CPU count.</summary>
    public double? Load1 { get; init; }

    public double? Load5 { get; init; }

    public double? Load15 { get; init; }

    public IReadOnlyList<ContainerMetrics> Containers { get; init; } = [];

    /// <summary>
    /// False when the docker daemon could not be reached this tick. Host metrics
    /// are read independently, so they stay valid — that separation is what keeps
    /// a docker outage from blinding every host rule at once.
    /// </summary>
    public bool DockerReachable { get; init; } = true;

    public IEnumerable<string> ContainerNames => Containers.Select(c => c.Name);

    /// <summary>
    /// The value a rule should be evaluated against, or null for "no reading" —
    /// which the evaluator turns into NoData once the rule's grace period is up.
    /// </summary>
    public double? Value(string metric, string series)
    {
        if (!AlertMetrics.IsContainerMetric(metric))
        {
            return metric switch
            {
                AlertMetrics.HostCpu => Cpu,
                AlertMetrics.HostMemory => Memory,
                AlertMetrics.HostSwap => Swap,
                AlertMetrics.HostDisk => Disk,
                AlertMetrics.HostLoad1 => Load1,
                AlertMetrics.HostLoad5 => Load5,
                AlertMetrics.HostLoad15 => Load15,
                _ => null,
            };
        }

        // A container that docker does not list is not "up", "down" or "healthy";
        // it is unknown. Reporting 0 here would quietly resolve a live alert.
        var container = Containers.FirstOrDefault(c =>
            string.Equals(c.Name, series, StringComparison.Ordinal));
        if (container is null)
        {
            return null;
        }

        return metric switch
        {
            AlertMetrics.ContainerCpu => container.Cpu,
            AlertMetrics.ContainerMemory => container.Memory,
            AlertMetrics.ContainerDown => container.Down ? 1 : 0,
            AlertMetrics.ContainerUnhealthy => container.Unhealthy ? 1 : 0,
            AlertMetrics.ContainerRestarting => container.Restarting ? 1 : 0,
            _ => null,
        };
    }
}
