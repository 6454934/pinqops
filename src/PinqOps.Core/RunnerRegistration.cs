using System.Text.Json;

namespace PinqOps;

/// <summary>
/// Reads the actions-runner <c>.runner</c> registration file: which repository
/// the local runner is registered to and under what agent name. Shared by the
/// dashboard (repo-aware readiness checks) and the CLI (pre-install cleanup),
/// so both sides agree on one definition of "registered".
/// </summary>
public static class RunnerRegistration
{
    /// <summary>
    /// The agent name and repository URL from <c>{dir}/.runner</c>, or nulls
    /// when nothing is registered there or the file is unreadable.
    /// </summary>
    public static (string? AgentName, string? Url) Read(string runnerDirectory)
    {
        var runnerFile = Path.Combine(runnerDirectory, ".runner");
        if (!File.Exists(runnerFile))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(runnerFile));
            return (
                GetString(document.RootElement, "agentName"),
                GetString(document.RootElement, "gitHubUrl") ?? GetString(document.RootElement, "serverUrl"));
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    public static string? ReadUrl(string runnerDirectory) => Read(runnerDirectory).Url;

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
