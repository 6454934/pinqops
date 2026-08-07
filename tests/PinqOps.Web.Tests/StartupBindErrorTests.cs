using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// A failed bind is the first thing that can go wrong on a new server, and it is
/// the one failure the operator sees before the dashboard exists to explain
/// anything. These are the shapes Kestrel raises.
/// </summary>
public class StartupBindErrorTests
{
    private const string Host = "0.0.0.0";
    private const string Port = "7467";

    /// <summary>Kestrel's own wrapping: an IOException carrying the real cause.</summary>
    private static IOException Wrapped(Exception cause) =>
        new($"Failed to bind to address http://{Host}:{Port}.", cause);

    [Fact]
    public void APortAlreadyTakenNamesThePortAndTheWayOut()
    {
        var described = StartupBindError.Describe(
            Wrapped(new AddressInUseException("address already in use")), Host, Port);

        Assert.NotNull(described);
        Assert.Contains(Port, described, StringComparison.Ordinal);
        Assert.Contains("--port", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// On some platforms the socket error arrives without the AddressInUse
    /// wrapper, so the code is checked as well as the type.
    /// </summary>
    [Fact]
    public void ARawAddressInUseSocketErrorIsRecognisedToo()
    {
        var described = StartupBindError.Describe(
            Wrapped(new SocketException((int)SocketError.AddressAlreadyInUse)), Host, Port);

        Assert.NotNull(described);
        Assert.Contains("already in use", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// Binding 80 or 443 without root is the obvious thing to try after reading
    /// about the reverse proxy, and "start it with sudo" is the whole answer.
    /// </summary>
    [Fact]
    public void APrivilegedPortSaysItNeedsRoot()
    {
        var described = StartupBindError.Describe(
            Wrapped(new SocketException((int)SocketError.AccessDenied)), Host, "443");

        Assert.NotNull(described);
        Assert.Contains("sudo", described, StringComparison.Ordinal);
        Assert.Contains("443", described, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAddressThisMachineDoesNotHaveSuggestsTheHostFlag()
    {
        var described = StartupBindError.Describe(
            Wrapped(new SocketException((int)SocketError.AddressNotAvailable)), "10.0.0.7", Port);

        Assert.NotNull(described);
        Assert.Contains("10.0.0.7", described, StringComparison.Ordinal);
        Assert.Contains("--host", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// Anything else has to keep its stack trace. Swallowing an unrelated startup
    /// failure behind a bind message would send the operator after a port that
    /// was never the problem.
    /// </summary>
    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void AnyOtherStartupFailureIsNotClaimed(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, "something else broke")!;

        Assert.Null(StartupBindError.Describe(exception, Host, Port));
    }

    /// <summary>A TLS certificate that will not load is not a bind failure.</summary>
    [Fact]
    public void ACertificateFailureIsNotClaimed()
    {
        var described = StartupBindError.Describe(
            Wrapped(new System.Security.Cryptography.CryptographicException("bad password")), Host, Port);

        Assert.Null(described);
    }
}
