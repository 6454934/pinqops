using System.Text.Json;

namespace PinqOps;

/// <summary>
/// Verifies that the compose project actually came up after <c>up -d</c>:
/// polls <c>compose ps</c> until every service is running (and healthy, when it
/// defines a HEALTHCHECK) or the timeout elapses. Services without a
/// HEALTHCHECK only need to be running.
/// </summary>
public sealed class ComposeHealthChecker
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly IProcessRunner _processRunner;
    private readonly Action<string>? _log;
    private readonly TimeSpan _pollInterval;

    public ComposeHealthChecker(IProcessRunner processRunner, Action<string>? log = null, TimeSpan? pollInterval = null)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _log = log;
        _pollInterval = pollInterval ?? PollInterval;
    }

    /// <summary>
    /// Returns null when all services settle healthy within
    /// <paramref name="timeout"/>; otherwise a human-readable failure reason.
    /// </summary>
    /// <param name="project">
    /// The compose project to ask about, when it is not the one the file declares —
    /// which is how a coloured deploy checks the copy it just started rather than
    /// the one still serving traffic. Null is every other caller.
    /// </param>
    /// <param name="environmentFile">The environment that project was started with.</param>
    public async Task<string?> WaitForHealthyAsync(
        string composeFilePath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        string? project = null,
        string? environmentFile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeFilePath);

        var deadline = DateTimeOffset.UtcNow + timeout;
        string lastState = "no containers reported";
        var workingDirectory = PinqOpsStatePaths.ComposeWorkingDirectory(composeFilePath);
        var ps = project is { Length: > 0 } && environmentFile is { Length: > 0 }
            ? DockerComposeCommandBuilder.PsColor(composeFilePath, project, environmentFile)
            : DockerComposeCommandBuilder.Ps(composeFilePath);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await _processRunner
                .RunAsync("docker", ps, workingDirectory, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return $"compose ps failed (exit {result.ExitCode}): {result.StandardError.TrimEnd()}";
            }

            var services = JsonLines.Parse(result.StandardOutput);
            var verdict = Evaluate(services);
            if (verdict.AllHealthy)
            {
                _log?.Invoke("health check passed: all services running" + (verdict.CheckedHealth ? " and healthy" : string.Empty));
                return null;
            }

            if (verdict.FatalReason is not null)
            {
                return verdict.FatalReason;
            }

            lastState = verdict.PendingReason ?? lastState;
            if (DateTimeOffset.UtcNow >= deadline)
            {
                return $"health check timed out after {timeout.TotalSeconds:0}s: {lastState}";
            }

            _log?.Invoke($"waiting for services: {lastState}");
            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Verdict Evaluate(IReadOnlyList<JsonElement> services)
    {
        if (services.Count == 0)
        {
            return new Verdict(false, false, null, "no containers reported by compose ps");
        }

        var checkedHealth = false;
        foreach (var service in services)
        {
            var name = GetString(service, "Name") ?? GetString(service, "Service") ?? "unknown";
            var state = (GetString(service, "State") ?? string.Empty).ToLowerInvariant();
            var health = (GetString(service, "Health") ?? string.Empty).ToLowerInvariant();

            if (state is "exited" or "dead")
            {
                return new Verdict(false, checkedHealth, $"service '{name}' is {state}", null);
            }

            if (health == "unhealthy")
            {
                return new Verdict(false, checkedHealth, $"service '{name}' is unhealthy", null);
            }

            if (state != "running")
            {
                return new Verdict(false, checkedHealth, null, $"service '{name}' is {state}");
            }

            if (health.Length > 0 && health != "healthy")
            {
                checkedHealth = true;
                return new Verdict(false, true, null, $"service '{name}' health is {health}");
            }

            if (health == "healthy")
            {
                checkedHealth = true;
            }
        }

        return new Verdict(true, checkedHealth, null, null);
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record Verdict(bool AllHealthy, bool CheckedHealth, string? FatalReason, string? PendingReason);
}
