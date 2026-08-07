namespace PinqOps.Proxy;

/// <summary>What one observation of the proxy asks to be said, if anything.</summary>
public enum ProxyWatchdogNotice
{
    None,
    Down,
    Recovered,
}

/// <summary>The notice to send, and the state to carry into the next observation.</summary>
public readonly record struct ProxyWatchdogDecision(ProxyWatchdogNotice Notice, bool ReportedDown);

/// <summary>
/// Whether a proxy observation is worth telling anyone about.
///
/// <para>Pure, and separate from the worker that polls docker, because the bug this
/// prevents is not in the polling: it is in saying the same thing every minute for
/// an hour, or saying nothing at all because a flag was never cleared. That is a
/// four-case truth table, and a truth table belongs somewhere it can be read.</para>
/// </summary>
public static class ProxyWatchdog
{
    /// <summary>
    /// Edge-triggered: the notice fires on the change, not on the condition. A proxy
    /// that has been down for an hour has already been reported, and a recovery is
    /// only worth saying to someone who was told about the outage.
    /// </summary>
    public static ProxyWatchdogDecision Observe(bool proxyRunning, bool reportedDown) => (proxyRunning, reportedDown) switch
    {
        (false, false) => new ProxyWatchdogDecision(ProxyWatchdogNotice.Down, true),
        (false, true) => new ProxyWatchdogDecision(ProxyWatchdogNotice.None, true),
        (true, true) => new ProxyWatchdogDecision(ProxyWatchdogNotice.Recovered, false),
        (true, false) => new ProxyWatchdogDecision(ProxyWatchdogNotice.None, false),
    };

    /// <summary>
    /// The apps whose host port the proxy publishes, sorted so two reads of the same
    /// config produce the same message.
    ///
    /// <para>A disabled entry is not published — <see cref="ProxyPortSet.HostPorts"/>
    /// leaves it out of the container's <c>-p</c> flags — so the app is binding its
    /// own port again and a stopped proxy costs it nothing.</para>
    /// </summary>
    public static IReadOnlyList<string> EnrolledTargets(DomainConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return
        [
            .. config.Ports
                .Where(entry => entry.Enabled && entry.Target.Length > 0 && HostPort.IsValid(entry.HostPort))
                .Select(entry => entry.Target)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal),
        ];
    }
}
