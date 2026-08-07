using System.Net;
using PinqOps.Deploy;
using PinqOps.Tests.Fakes;
using Xunit;

namespace PinqOps.Tests.Deploy;

/// <summary>
/// The gate that distinguishes "the process started" from "the application is
/// serving". Every failure here has to fail the deploy, including the ones that are
/// the probe's own fault.
/// </summary>
public class ReadinessProbeTests
{
    private const string Container = "acme-app-1";
    private const string Networks = """{"pinqops-app-acme":{"IPAddress":"172.20.0.3"}}""";

    /// <summary>Answers `docker inspect` and nothing else; the HTTP side is scripted separately.</summary>
    private static FakeProcessRunner InspectRunner(string networks = Networks, int exitCode = 0) =>
        new((_, arguments) => arguments.Contains("inspect")
            ? new ProcessResult(exitCode, networks, exitCode == 0 ? string.Empty : "No such object")
            : new ProcessResult(0, string.Empty, string.Empty));

    private static ScriptedHttpMessageHandler Answering(params HttpStatusCode[] statuses) =>
        new((attempt, _) => new HttpResponseMessage(statuses[Math.Min(attempt, statuses.Length - 1)]));

    /// <summary>Fast enough to keep the suite quick; the clamps are tested separately.</summary>
    private static ReadinessSettings Settings(
        int consecutiveSuccesses = 1, int timeoutSeconds = 2, string path = "/") => new()
    {
        Enabled = true,
        Path = path,
        IntervalSeconds = 1,
        TimeoutSeconds = timeoutSeconds,
        RequestTimeoutSeconds = 1,
        ConsecutiveSuccesses = consecutiveSuccesses,
    };

    [Fact]
    public async Task AnApplicationThatAnswersIsReady()
    {
        var handler = Answering(HttpStatusCode.OK);
        var probe = new ReadinessProbe(InspectRunner(), handler);

        Assert.Null(await probe.WaitForReadyAsync(Settings(), Container, 8080));
        Assert.Equal("http://172.20.0.3:8080/", handler.Requests[0].ToString());
    }

    [Fact]
    public async Task ThePathIsTheOneThatWasConfigured()
    {
        var handler = Answering(HttpStatusCode.OK);

        await new ReadinessProbe(InspectRunner(), handler)
            .WaitForReadyAsync(Settings(path: "/healthz"), Container, 3000);

        Assert.Equal("http://172.20.0.3:3000/healthz", handler.Requests[0].ToString());
    }

    /// <summary>
    /// A redirect to a login page is a sign of life: the question is whether the
    /// application is answering, not whether this path is public.
    /// </summary>
    [Fact]
    public async Task ARedirectCountsAsAnswering() =>
        Assert.Null(await new ReadinessProbe(InspectRunner(), Answering(HttpStatusCode.Found))
            .WaitForReadyAsync(Settings(), Container, 8080));

    [Fact]
    public async Task AServerErrorIsNotReady()
    {
        var failure = await new ReadinessProbe(InspectRunner(), Answering(HttpStatusCode.InternalServerError))
            .WaitForReadyAsync(Settings(), Container, 8080);

        Assert.NotNull(failure);
        Assert.Contains("500", failure);
        Assert.Contains("expected 200-399", failure);
    }

    /// <summary>
    /// The reason ConsecutiveSuccesses defaults to two: a process that binds its
    /// port before it finishes loading answers once and then stops. One success
    /// would call the deploy green in exactly that window.
    /// </summary>
    [Fact]
    public async Task OneAnswerFollowedBySilenceIsNotReady()
    {
        var handler = new ScriptedHttpMessageHandler((attempt, _) =>
            new HttpResponseMessage(attempt == 0 ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable));

        var failure = await new ReadinessProbe(InspectRunner(), handler)
            .WaitForReadyAsync(Settings(consecutiveSuccesses: 2), Container, 8080);

        Assert.NotNull(failure);
        Assert.Contains("503", failure);
    }

    [Fact]
    public async Task TwoAnswersInARowAreReady()
    {
        var handler = Answering(HttpStatusCode.OK);

        Assert.Null(await new ReadinessProbe(InspectRunner(), handler)
            .WaitForReadyAsync(Settings(consecutiveSuccesses: 2, timeoutSeconds: 10), Container, 8080));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task AnApplicationThatStartsSlowlyIsStillReady()
    {
        var handler = new ScriptedHttpMessageHandler((attempt, _) =>
            new HttpResponseMessage(attempt < 2 ? HttpStatusCode.BadGateway : HttpStatusCode.OK));

        Assert.Null(await new ReadinessProbe(InspectRunner(), handler)
            .WaitForReadyAsync(Settings(timeoutSeconds: 10), Container, 8080));
    }

    [Fact]
    public async Task TheTimeoutIsReportedWithWhatTheApplicationLastSaid()
    {
        var failure = await new ReadinessProbe(InspectRunner(), Answering(HttpStatusCode.BadGateway))
            .WaitForReadyAsync(Settings(timeoutSeconds: 1), Container, 8080);

        Assert.NotNull(failure);
        Assert.Contains("did not become ready within 1s", failure);
        Assert.Contains("502", failure);
    }

    [Fact]
    public async Task AContainerWithNoAddressFailsRatherThanPasses()
    {
        // Fail-closed. A probe that cannot be run has not passed.
        var failure = await new ReadinessProbe(InspectRunner("null"), Answering(HttpStatusCode.OK))
            .WaitForReadyAsync(Settings(), Container, 8080);

        Assert.NotNull(failure);
        Assert.Contains("could not find an address", failure);
    }

    [Fact]
    public async Task AnInspectThatFailsFailsTheProbe()
    {
        var failure = await new ReadinessProbe(InspectRunner(string.Empty, exitCode: 1), Answering(HttpStatusCode.OK))
            .WaitForReadyAsync(Settings(), Container, 8080);

        Assert.NotNull(failure);
        Assert.Contains("could not find an address", failure);
    }

    [Fact]
    public async Task AnImpossiblePortIsRefusedBeforeAnythingIsAsked()
    {
        var runner = InspectRunner();

        var failure = await new ReadinessProbe(runner, Answering(HttpStatusCode.OK))
            .WaitForReadyAsync(Settings(), Container, 0);

        Assert.NotNull(failure);
        Assert.Empty(runner.Invocations);
    }

    private static ScriptedHttpMessageHandler Unreachable() =>
        new((_, _) => throw new HttpRequestException(
            "Connection refused", new System.Net.Sockets.SocketException(10061)));

    /// <summary>
    /// On Docker Desktop and anywhere else the bridge is not routed to the host, a
    /// direct request cannot work at all — and the symptom looks exactly like a
    /// broken application. The probe asks from inside the network instead.
    /// </summary>
    [Fact]
    public async Task AHostThatCannotReachTheBridgeAsksFromInsideTheNetwork()
    {
        var runner = new FakeProcessRunner((_, arguments) =>
            arguments.Contains("inspect")
                ? new ProcessResult(0, Networks, string.Empty)
                : new ProcessResult(0, string.Empty, "  HTTP/1.1 200 OK\n  Content-Type: text/html\n"));

        Assert.Null(await new ReadinessProbe(runner, Unreachable()).WaitForReadyAsync(Settings(), Container, 8080));

        var run = Assert.Single(runner.Invocations, invocation => invocation.Arguments.Contains("run"));
        Assert.Equal(
            ["run", "--rm", "--network", "pinqops-app-acme", "alpine",
             "wget", "-S", "-O", "/dev/null", "-T", "1", "http://172.20.0.3:8080/"],
            run.Arguments);
    }

    /// <summary>
    /// busybox wget exits non-zero for anything outside 2xx, so its exit code is not
    /// the answer — a 302 is a perfectly good sign of life, and a 500 has to be
    /// reported as a 500 rather than as "the probe container failed".
    /// </summary>
    [Fact]
    public async Task TheStatusLineIsWhatCountsFromInsideNotTheExitCode()
    {
        var runner = new FakeProcessRunner((_, arguments) =>
            arguments.Contains("inspect")
                ? new ProcessResult(0, Networks, string.Empty)
                : new ProcessResult(1, string.Empty, "wget: server returned error: HTTP/1.1 503 Service Unavailable\n"));

        var failure = await new ReadinessProbe(runner, Unreachable())
            .WaitForReadyAsync(Settings(timeoutSeconds: 1), Container, 8080);

        Assert.NotNull(failure);
        Assert.Contains("503", failure);
    }

    [Fact]
    public async Task AProbeContainerThatSaysNothingIsAFailedAttemptNotAPass()
    {
        var runner = new FakeProcessRunner((_, arguments) =>
            arguments.Contains("inspect")
                ? new ProcessResult(0, Networks, string.Empty)
                : new ProcessResult(125, string.Empty, "docker: network pinqops-app-acme not found"));

        var failure = await new ReadinessProbe(runner, Unreachable())
            .WaitForReadyAsync(Settings(timeoutSeconds: 1), Container, 8080);

        Assert.NotNull(failure);
        Assert.Contains("network pinqops-app-acme not found", failure);
    }

    /// <summary>
    /// The switch happens once. Going back to the direct transport between attempts
    /// would pay the timeout again on every single one.
    /// </summary>
    [Fact]
    public async Task TheDirectTransportIsNotRetriedOnceItHasBeenRuledOut()
    {
        var handler = Unreachable();
        var runner = new FakeProcessRunner((_, arguments) =>
            arguments.Contains("inspect")
                ? new ProcessResult(0, Networks, string.Empty)
                : new ProcessResult(0, string.Empty, "HTTP/1.1 500 Internal Server Error\n"));

        await new ReadinessProbe(runner, handler).WaitForReadyAsync(Settings(timeoutSeconds: 1), Container, 8080);

        Assert.Single(handler.Requests);
        Assert.True(runner.Invocations.Count(invocation => invocation.Arguments.Contains("run")) > 1);
    }

    [Fact]
    public async Task TheProbeUrlIsLoggedSoAFailureCanBeReproducedByHand()
    {
        var log = new List<string>();

        await new ReadinessProbe(InspectRunner(), Answering(HttpStatusCode.OK), log.Add)
            .WaitForReadyAsync(Settings(path: "/healthz"), Container, 8080);

        Assert.Contains(log, line => line.Contains("http://172.20.0.3:8080/healthz"));
    }

    [Fact]
    public async Task AnIpv6AddressIsBracketedSoTheUrlParses()
    {
        var handler = Answering(HttpStatusCode.OK);

        await new ReadinessProbe(InspectRunner("""{"pinqops-apps":{"IPAddress":"fd00::2"}}"""), handler)
            .WaitForReadyAsync(Settings(), Container, 8080);

        Assert.Equal("http://[fd00::2]:8080/", handler.Requests[0].ToString());
    }
}
