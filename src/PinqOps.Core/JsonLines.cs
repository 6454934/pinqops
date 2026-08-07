using System.Text.Json;

namespace PinqOps;

/// <summary>
/// Docker's <c>--format json</c> output is NDJSON in some versions and a
/// single array in others; accept both.
/// </summary>
public static class JsonLines
{
    public static List<JsonElement> Parse(string output)
    {
        var items = new List<JsonElement>();
        var trimmed = output.Trim();
        if (trimmed.Length == 0)
        {
            return items;
        }

        if (trimmed.StartsWith('['))
        {
            // A truncated or partial array must not crash best-effort callers
            // (image retention, health checks); return whatever parsed.
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                items.AddRange(document.RootElement.EnumerateArray().Select(element => element.Clone()));
            }
            catch (JsonException)
            {
            }

            return items;
        }

        foreach (var line in trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{'))
            {
                continue;
            }

            // Skip a single malformed line (e.g. an interleaved docker warning)
            // rather than discarding every well-formed line alongside it.
            try
            {
                using var document = JsonDocument.Parse(line);
                items.Add(document.RootElement.Clone());
            }
            catch (JsonException)
            {
            }
        }

        return items;
    }
}
