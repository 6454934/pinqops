using System.Text.Json;
using PinqOps.Mcp;

namespace PinqOps.Cli;

/// <summary>
/// Local stdio MCP bridge for agents that cannot reach the dashboard URL
/// directly. Prefer the remote <c>/mcp</c> endpoint on pinqops-ui when the
/// dashboard is reachable — Cursor/Claude can use just a URL + bearer token.
/// </summary>
public static class McpServer
{
    public static async Task<int> RunAsync()
    {
        var baseUrl = Environment.GetEnvironmentVariable("PINQOPS_URL");
        var token = Environment.GetEnvironmentVariable("PINQOPS_TOKEN");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine(
                "pinqops mcp: set PINQOPS_URL and PINQOPS_TOKEN, or point Cursor at "
                + "http(s)://<dashboard>/mcp with Authorization: Bearer pot_… (no local binary needed).");
            return 1;
        }

        var insecure = Environment.GetEnvironmentVariable("PINQOPS_INSECURE") is "1" or "true";
        if (insecure)
        {
            Console.Error.WriteLine(
                "pinqops mcp: WARNING — PINQOPS_INSECURE is set, so the server's TLS certificate is not verified. "
                + "The API token is exposed to anyone who can intercept this connection.");
        }

        using var http = McpProtocol.CreateClient(baseUrl, token, insecure);
        Console.Error.WriteLine($"pinqops mcp: serving {McpProtocol.ToolNames.Count} tools against {baseUrl}");

        string? line;
        while ((line = await Console.In.ReadLineAsync()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonDocument request;
            try
            {
                request = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (request)
            {
                if (request.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var response = await McpProtocol.HandleAsync(request.RootElement, http);
                if (response is not null)
                {
                    Console.WriteLine(response);
                    await Console.Out.FlushAsync();
                }
            }
        }

        return 0;
    }
}
