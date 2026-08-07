using System.Globalization;
using System.Text.RegularExpressions;

namespace PinqOps.Deploy;

/// <summary>
/// Asks the application for a page after its containers are up, and refuses to call
/// the deploy a success until it answers.
///
/// <para><b>What it adds over the compose health check.</b> Docker knows whether a
/// process is running and, when the image declares a HEALTHCHECK, whether that
/// script is happy. Most images declare none, and a process that started and then
/// failed to bind its port is "running" by every measure docker has. One HTTP
/// request is the difference between started and serving.</para>
///
/// <para><b>Fail-closed.</b> A probe that cannot be run has not passed. Resolving no
/// address, an unparseable inspect, a fallback that will not start — each fails the
/// deploy. The alternative is a gate that quietly stops gating, which is worse than
/// no gate: it reports the same green either way.</para>
///
/// <para><b>Two transports, and why.</b> The direct one is an HTTP request from this
/// process to the container's address on its docker network. That is unreachable on
/// Docker Desktop and anywhere else the bridge is not routed to the host, and the
/// symptom is a connection refused that looks exactly like a broken application. On
/// the first socket-level failure the probe switches to asking from inside the
/// network instead, and stays there for the rest of the run.</para>
/// </summary>
public sealed partial class ReadinessProbe
{
    /// <summary>Small and universally available; only busybox <c>wget</c> is used.</summary>
    public const string FallbackImage = "alpine";

    private readonly IProcessRunner _processRunner;
    private readonly HttpMessageHandler? _handler;
    private readonly Action<string>? _log;

    public ReadinessProbe(IProcessRunner processRunner, HttpMessageHandler? handler = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        _processRunner = processRunner;
        _handler = handler;
        _log = log;
    }

    /// <summary>
    /// Returns null when the application answered <see cref="ReadinessSettings.ConsecutiveSuccesses"/>
    /// times in a row within the timeout; otherwise why it did not.
    /// </summary>
    public async Task<string?> WaitForReadyAsync(
        ReadinessSettings settings,
        string containerName,
        int containerPort,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);

        var options = settings.Normalized();
        if (!HostPort.IsValid(containerPort))
        {
            return $"readiness probe: {containerPort} is not a port the application could be listening on.";
        }

        var address = await ResolveAddressAsync(containerName, cancellationToken).ConfigureAwait(false);
        if (address is null)
        {
            // Fail-closed: no address is not "probably fine", it is a container that
            // is on no network anything can reach.
            return $"readiness probe: could not find an address for '{containerName}' on any docker network.";
        }

        // An IPv6 literal has to be bracketed or the colons read as the port
        // separator and the URL will not parse at all.
        static string UrlFor(ContainerAddress address, int containerPort, ReadinessSettings options)
        {
            var host = address.IpAddress.Contains(':', StringComparison.Ordinal)
                ? $"[{address.IpAddress}]"
                : address.IpAddress;
            return $"http://{host}:{containerPort.ToString(CultureInfo.InvariantCulture)}{options.Path}";
        }

        var url = UrlFor(address, containerPort, options);
        _log?.Invoke($"readiness probe: {url} (via {address.Network}, expecting {options.ExpectedStatusFrom}-{options.ExpectedStatusTo})");

        using var client = new HttpClient(_handler ?? new SocketsHttpHandler(), disposeHandler: _handler is null)
        {
            Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds),
        };

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(options.TimeoutSeconds);
        var successes = 0;
        var fromInsideTheNetwork = false;
        var lastFailure = "no attempt completed";

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var attempt = fromInsideTheNetwork
                ? await AskFromInsideAsync(address.Network, url, options, cancellationToken).ConfigureAwait(false)
                : await AskDirectlyAsync(client, url, cancellationToken).ConfigureAwait(false);

            if (attempt.Unreachable && !fromInsideTheNetwork)
            {
                // Not a failed attempt — a transport that cannot work here. Retrying
                // it until the deadline would report "the app never answered" about
                // an app nobody ever asked.
                fromInsideTheNetwork = true;
                _log?.Invoke(
                    $"readiness probe: this host cannot reach {address.IpAddress} directly ({attempt.Failure}); "
                    + $"asking from inside {address.Network} instead");
                continue;
            }

            if (attempt.Status is { } status && status >= options.ExpectedStatusFrom && status <= options.ExpectedStatusTo)
            {
                if (++successes >= options.ConsecutiveSuccesses)
                {
                    _log?.Invoke($"readiness probe passed: {status} × {successes}");
                    return null;
                }

                lastFailure = $"answered {status}, waiting for {options.ConsecutiveSuccesses} in a row";
            }
            else
            {
                // Reset rather than decrement: the point of the run is to catch a
                // process that answers once and then stops, and a run that survives
                // a failure in the middle is not a run.
                successes = 0;
                lastFailure = attempt.Status is { } bad
                    ? $"answered {bad}, expected {options.ExpectedStatusFrom}-{options.ExpectedStatusTo}"
                    : attempt.Failure;

                // A socket-level failure may not be the app at all: a container
                // that crash-looped or was recreated comes back with a NEW address,
                // and probing the old one until the deadline reports "never became
                // ready" about a container nobody asked. Follow it.
                if (attempt.Status is null
                    && await ResolveAddressAsync(containerName, cancellationToken).ConfigureAwait(false) is { } moved
                    && (moved.IpAddress != address.IpAddress || moved.Network != address.Network))
                {
                    address = moved;
                    url = UrlFor(address, containerPort, options);
                    _log?.Invoke($"readiness probe: '{containerName}' moved; now asking {url} (via {address.Network})");
                }
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return $"readiness probe: {url} did not become ready within {options.TimeoutSeconds}s — {lastFailure}";
            }

            _log?.Invoke($"waiting for readiness: {lastFailure}");
            await Task.Delay(TimeSpan.FromSeconds(options.IntervalSeconds), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One attempt: a status code, or why there was none.</summary>
    private sealed record Attempt(int? Status, string Failure, bool Unreachable = false);

    private async Task<ContainerAddress?> ResolveAddressAsync(string containerName, CancellationToken cancellationToken)
    {
        var result = await _processRunner
            .RunAsync("docker", DockerComposeCommandBuilder.InspectContainerNetworks(containerName), null, cancellationToken)
            .ConfigureAwait(false);

        return result.Succeeded ? ContainerNetworkAddress.Best(result.StandardOutput) : null;
    }

    private static async Task<Attempt> AskDirectlyAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            return new Attempt((int)response.StatusCode, string.Empty);
        }
        catch (HttpRequestException exception)
        {
            // A refused connection is what an application that has not finished
            // starting looks like, and also what an unroutable docker bridge looks
            // like. Only the second is worth changing transport for, and the two are
            // not distinguishable from here — so the switch happens once, and the
            // fallback then reports the application's real answer either way.
            return new Attempt(null, Describe(exception), Unreachable: true);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The client's own timeout, not the caller's cancellation. An app too
            // slow to answer is a failed attempt, never a transport problem.
            return new Attempt(null, "the request timed out");
        }
    }

    /// <summary>
    /// The same request, made by a throwaway container on the app's own network. Its
    /// exit code is not the answer — busybox <c>wget</c> exits non-zero for any
    /// status outside 2xx, and a 302 is a perfectly good sign of life — so the status
    /// line it prints is what gets parsed.
    /// </summary>
    private async Task<Attempt> AskFromInsideAsync(
        string network, string url, ReadinessSettings options, CancellationToken cancellationToken)
    {
        var result = await _processRunner
            .RunAsync(
                "docker",
                [
                    "run", "--rm", "--network", network, FallbackImage,
                    "wget", "-S", "-O", "/dev/null", "-T", options.RequestTimeoutSeconds.ToString(CultureInfo.InvariantCulture), url,
                ],
                null,
                cancellationToken)
            .ConfigureAwait(false);

        // wget writes the response headers to stderr; a redirect chain writes more
        // than one, and the last is the one that answered.
        var matches = StatusLinePattern().Matches(result.StandardError + '\n' + result.StandardOutput);
        if (matches.Count > 0
            && int.TryParse(matches[^1].Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var status))
        {
            return new Attempt(status, string.Empty);
        }

        var detail = result.StandardError.Trim();
        return new Attempt(
            null,
            detail.Length > 0 ? detail : $"the probe container exited {result.ExitCode} without a response");
    }

    private static string Describe(HttpRequestException exception) =>
        exception.InnerException?.Message is { Length: > 0 } inner ? inner : exception.Message;

    [GeneratedRegex(@"HTTP/\d(?:\.\d)?\s+(\d{3})")]
    private static partial Regex StatusLinePattern();
}
