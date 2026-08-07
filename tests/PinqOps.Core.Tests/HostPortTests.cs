using System.Net;
using System.Net.Sockets;
using Xunit;

namespace PinqOps.Tests;

public class HostPortTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(8080, true)]
    [InlineData(65535, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(65536, false)]
    public void IsValid_BoundsThePortRange(int port, bool expected)
    {
        Assert.Equal(expected, HostPort.IsValid(port));
    }

    [Fact]
    public void IsAvailable_FalseWhileSomethingIsListening()
    {
        var occupied = StartListenerOnFreePort(out var listener);
        try
        {
            Assert.False(HostPort.IsAvailable(occupied));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void IsAvailable_TrueOnceTheListenerIsGone()
    {
        var port = StartListenerOnFreePort(out var listener);
        listener.Stop();

        Assert.True(HostPort.IsAvailable(port));
    }

    [Fact]
    public void IsAvailable_FalseForAnOutOfRangePort()
    {
        Assert.False(HostPort.IsAvailable(0));
        Assert.False(HostPort.IsAvailable(70000));
    }

    [Fact]
    public void FindAvailable_ReturnsThePreferredPortWhenItIsFree()
    {
        var port = StartListenerOnFreePort(out var listener);
        listener.Stop();

        Assert.Equal(port, HostPort.FindAvailable(port));
    }

    [Fact]
    public void FindAvailable_SkipsPastAnOccupiedPort()
    {
        var occupied = StartListenerOnFreePort(out var listener);
        try
        {
            var chosen = HostPort.FindAvailable(occupied);

            Assert.NotNull(chosen);
            Assert.NotEqual(occupied, chosen);
            Assert.InRange(chosen!.Value, occupied + 1, occupied + HostPort.ScanLimit);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void FindAvailable_RejectsAnOutOfRangePreference()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HostPort.FindAvailable(0));
    }

    /// <summary>Binds an OS-assigned free port and keeps it held by the caller.</summary>
    private static int StartListenerOnFreePort(out TcpListener listener)
    {
        listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
