using System.Text.Json;

namespace PinqOps.Web;

/// <summary>
/// Reads the deployed app's live state out of <c>docker compose ps --format
/// json</c> output — the data behind the wizard's "your app is live" card.
/// </summary>
public static class ComposeAppStatus
{
    /// <summary>
    /// The state and first published host port of the app service. The template
    /// names the service <c>app</c>; when it is absent (hand-edited compose
    /// file) the first service stands in.
    /// </summary>
    public static (string? State, int? PublishedPort) FromServices(IReadOnlyList<JsonElement> services)
    {
        var service = PickAppService(services);
        if (service is not { } element)
        {
            return (null, null);
        }

        return (GetString(element, "State"), FirstPublishedPort(element));
    }

    private static JsonElement? PickAppService(IReadOnlyList<JsonElement> services)
    {
        JsonElement? first = null;
        foreach (var service in services)
        {
            if (service.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (string.Equals(GetString(service, "Service"), "app", StringComparison.OrdinalIgnoreCase))
            {
                return service;
            }

            first ??= service;
        }

        return first;
    }

    private static int? FirstPublishedPort(JsonElement service)
    {
        if (service.TryGetProperty("Publishers", out var publishers)
            && publishers.ValueKind == JsonValueKind.Array)
        {
            foreach (var publisher in publishers.EnumerateArray())
            {
                if (publisher.ValueKind == JsonValueKind.Object
                    && publisher.TryGetProperty("PublishedPort", out var published)
                    && published.ValueKind == JsonValueKind.Number
                    && published.TryGetInt32(out var port)
                    && port > 0)
                {
                    return port;
                }
            }
        }

        // Older compose versions report only a docker-ps style string:
        // "0.0.0.0:8080->80/tcp, :::8080->80/tcp".
        var ports = GetString(service, "Ports");
        var match = System.Text.RegularExpressions.Regex.Match(ports ?? "", @":(\d+)->");
        return match.Success && int.TryParse(match.Groups[1].Value, out var parsed) ? parsed : null;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
