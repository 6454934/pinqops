using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PinqOps.Mcp;

/// <summary>
/// Shared MCP JSON-RPC handling used by the local <c>pinqops mcp</c> stdio
/// bridge and by the dashboard's remote <c>/mcp</c> Streamable HTTP endpoint.
/// Tool calls are plain dashboard REST requests made with the caller's token.
/// </summary>
public static class McpProtocol
{
    public const string ProtocolVersion = "2024-11-05";

    private sealed record Tool(string Name, string Description, object InputSchema);

    private static readonly Tool[] Tools =
    [
        new("list_apps", "List the app repositories this pinqops server manages.", NoArgs()),
        new("deploy_status", "Current deploy state and live container status of an app.", AppArg()),
        new("deploy_history", "Recent deploy history of an app (tags, results, timestamps).", AppArg()),
        new("trigger_deploy", "Start a build & deploy of an app (workflow_dispatch). Requires a deploy-scope token.", AppArg()),
        new("rollback", "Roll an app back to a previous image tag. Requires a deploy-scope token.", RollbackArgs()),
        new("app_metrics", "Live CPU/memory of running containers (docker stats).", NoArgs()),
        new("container_logs", "Recent logs of a container by id or name.", LogsArgs()),
    ];

    public static IReadOnlyList<string> ToolNames { get; } = Tools.Select(tool => tool.Name).ToArray();

    /// <summary>
    /// Handles one JSON-RPC message. Returns the response JSON, or null for
    /// notifications (no <c>id</c>).
    /// </summary>
    public static async Task<string?> HandleAsync(JsonElement message, HttpClient http)
    {
        var method = message.TryGetProperty("method", out var methodElement) ? methodElement.GetString() : null;
        var hasId = message.TryGetProperty("id", out var id);
        if (!hasId)
        {
            return null;
        }

        return method switch
        {
            "initialize" => Ok(id, new
            {
                protocolVersion = ProtocolVersion,
                capabilities = new { tools = new { } },
                serverInfo = new { name = "pinqops", version = PinqOpsVersion.Current },
            }),
            "ping" => Ok(id, new { }),
            "tools/list" => Ok(id, new
            {
                tools = Tools.Select(tool => new
                {
                    name = tool.Name,
                    description = tool.Description,
                    inputSchema = tool.InputSchema,
                }),
            }),
            "tools/call" => await CallToolAsync(id, message, http).ConfigureAwait(false),
            _ => Error(id, -32601, $"Unknown method '{method}'"),
        };
    }

    public static HttpClient CreateClient(string baseUrl, string token, bool insecure = false)
    {
        var handler = new HttpClientHandler { CheckCertificateRevocationList = true };
        if (insecure)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private static async Task<string> CallToolAsync(JsonElement id, JsonElement message, HttpClient http)
    {
        if (!message.TryGetProperty("params", out var parameters)
            || !parameters.TryGetProperty("name", out var nameElement))
        {
            return Error(id, -32602, "Missing tool name");
        }

        var name = nameElement.GetString();
        var args = parameters.TryGetProperty("arguments", out var arguments) ? arguments : default;
        string? App() =>
            args.ValueKind == JsonValueKind.Object && args.TryGetProperty("app", out var value)
                ? value.GetString()
                : null;
        string Query() => App() is { Length: > 0 } app
            ? $"?appId={Uri.EscapeDataString(app)}"
            : string.Empty;

        try
        {
            var text = name switch
            {
                "list_apps" => await GetAsync(http, "api/settings").ConfigureAwait(false),
                "deploy_status" => await GetAsync(http, $"api/deploy/state{Query()}").ConfigureAwait(false),
                "deploy_history" => await GetAsync(http, $"api/deploy/history{Query()}").ConfigureAwait(false),
                "app_metrics" => await GetAsync(http, "api/docker/stats").ConfigureAwait(false),
                "trigger_deploy" => await PostAsync(http, $"api/setup/trigger-deploy{Query()}", null)
                    .ConfigureAwait(false),
                "rollback" => await PostAsync(
                        http,
                        $"api/deploy/rollback{Query()}",
                        new { tag = args.TryGetProperty("tag", out var tag) ? tag.GetString() : null })
                    .ConfigureAwait(false),
                "container_logs" => await GetAsync(
                        http,
                        $"api/docker/containers/{Uri.EscapeDataString(args.GetProperty("container").GetString() ?? "")}/logs")
                    .ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unknown tool '{name}'"),
            };
            return ToolResult(id, text, isError: false);
        }
        catch (Exception exception)
        {
            return ToolResult(id, exception.Message, isError: true);
        }
    }

    private static async Task<string> GetAsync(HttpClient http, string path)
    {
        using var response = await http.GetAsync(path).ConfigureAwait(false);
        return await ReadAsync(response).ConfigureAwait(false);
    }

    private static async Task<string> PostAsync(HttpClient http, string path, object? body)
    {
        using var content = new StringContent(
            body is null ? "{}" : JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json");
        using var response = await http.PostAsync(path, content).ConfigureAwait(false);
        return await ReadAsync(response).ConfigureAwait(false);
    }

    private static async Task<string> ReadAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {body}");
        }

        return body;
    }

    private static string Ok(JsonElement id, object result) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id = IdValue(id), result });

    private static string Error(JsonElement id, int code, string messageText) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id = IdValue(id), error = new { code, message = messageText } });

    private static string ToolResult(JsonElement id, string text, bool isError) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = IdValue(id),
            result = new { content = new[] { new { type = "text", text } }, isError },
        });

    private static object? IdValue(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.Number => id.TryGetInt64(out var number) ? number : id.GetDouble(),
        JsonValueKind.String => id.GetString(),
        _ => null,
    };

    private static object NoArgs() => new { type = "object", properties = new { } };

    private static object AppArg() => new
    {
        type = "object",
        properties = new { app = new { type = "string", description = "App id (optional; defaults to the only/first app)." } },
    };

    private static object RollbackArgs() => new
    {
        type = "object",
        required = new[] { "tag" },
        properties = new
        {
            app = new { type = "string", description = "App id (optional)." },
            tag = new { type = "string", description = "Image tag to roll back to (from deploy_history)." },
        },
    };

    private static object LogsArgs() => new
    {
        type = "object",
        required = new[] { "container" },
        properties = new { container = new { type = "string", description = "Container id or name." } },
    };
}
