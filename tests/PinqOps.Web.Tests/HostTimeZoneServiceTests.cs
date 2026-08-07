using PinqOps;
using PinqOps.Web.Tests.Fakes;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// Setting the server's time zone.
///
/// <para>Everything scheduled here is scheduled in the server's own zone — "3 a.m."
/// means three in the morning where the box is. .NET resolves that zone once per
/// process and caches it, so changing it on the host moved the clock for
/// <c>docker logs</c> and <c>journalctl</c> and for the reading the Settings page
/// shows back, but not for the scheduler: the page said one zone and jobs went on
/// firing in another. The change then landed at the next restart, moving a nightly
/// job by the offset with nobody having edited it and nothing in the log.</para>
/// </summary>
public class HostTimeZoneServiceTests
{
    private static FakeProcessRunner Runner(bool setSucceeds) => new((_, arguments) =>
        arguments.Contains("set-timezone") && !setSucceeds
            ? new ProcessResult(1, string.Empty, "Failed to set time zone: Interactive authentication required.")
            : new ProcessResult(0, "Europe/Istanbul\n", string.Empty));

    /// <summary>
    /// The process has to be told to forget the zone it resolved at startup, or the
    /// scheduler keeps the old one until something restarts it.
    /// </summary>
    [Fact]
    public async Task SettingTheZoneMakesThisProcessForgetTheOldOne()
    {
        var forgotten = 0;
        var service = new HostTimeZoneService(Runner(setSucceeds: true), () => forgotten++);

        await service.SetAsync("Europe/Istanbul");

        Assert.Equal(1, forgotten);
    }

    /// <summary>
    /// And a refused change does not, because nothing on the host moved — clearing
    /// the cache there would re-read the same zone and claim work that did not happen.
    /// </summary>
    [Fact]
    public async Task AZoneThatCouldNotBeSetLeavesTheProcessAsItWas()
    {
        var forgotten = 0;
        var service = new HostTimeZoneService(Runner(setSucceeds: false), () => forgotten++);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetAsync("Europe/Istanbul"));

        Assert.Equal(0, forgotten);
    }
}
