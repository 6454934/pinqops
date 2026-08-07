namespace PinqOps.Proxy;

/// <summary>
/// Derives the set of host ports the proxy container publishes.
///
/// <para>The <c>-p</c> flags of a container are fixed when it is created, so
/// changing the set means recreating the container. That makes "what should be
/// published" a question with exactly one right answer, and this is the one place
/// that answers it: the installer builds its flags from here, and the status check
/// compares the running container's bindings against here to notice drift. A second
/// place that decided this would eventually disagree, and a Caddyfile with a
/// <c>:8080</c> block in front of a container with no <c>-p 8080</c> is a route
/// that exists on paper and refuses every connection.</para>
/// </summary>
public static class ProxyPortSet
{
    public const int HttpPort = 80;

    public const int HttpsPort = 443;

    /// <summary>
    /// Every host port the proxy should publish: its own HTTP and HTTPS listeners,
    /// plus one per enabled port entry. Sorted and de-duplicated, so two configs
    /// describing the same set compare equal however they were written.
    /// </summary>
    public static IReadOnlyList<int> HostPorts(DomainConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var ports = new SortedSet<int> { HttpPort, HttpsPort };
        foreach (var entry in config.Ports)
        {
            // The same rules the generator applies, so the published set and the
            // rendered file cannot disagree about which entries count.
            if (entry.Enabled && HostPort.IsValid(entry.HostPort) && entry.HostPort is not (HttpPort or HttpsPort))
            {
                ports.Add(entry.HostPort);
            }
        }

        return [.. ports];
    }

    /// <summary>
    /// Those ports as <c>docker run</c> arguments. HTTPS is published on UDP as
    /// well, which is what carries HTTP/3.
    /// </summary>
    public static IReadOnlyList<string> PublishArguments(DomainConfig config)
    {
        var arguments = new List<string>();
        foreach (var port in HostPorts(config))
        {
            arguments.Add("-p");
            arguments.Add($"{port}:{port}");
            if (port == HttpsPort)
            {
                arguments.Add("-p");
                arguments.Add($"{port}:{port}/udp");
            }
        }

        return arguments;
    }

    /// <summary>
    /// Whether a running container's published ports are the ones this config asks
    /// for. <paramref name="published"/> is what <c>docker inspect</c> reported; a
    /// mismatch means the container has to be recreated before its Caddyfile can be
    /// believed.
    /// </summary>
    public static bool Matches(DomainConfig config, IEnumerable<int> published)
    {
        ArgumentNullException.ThrowIfNull(published);
        return HostPorts(config).SequenceEqual(new SortedSet<int>(published));
    }
}
