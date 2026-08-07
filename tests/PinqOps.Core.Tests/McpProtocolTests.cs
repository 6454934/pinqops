using System.Net;
using System.Text;
using System.Text.Json;
using PinqOps.Mcp;
using Xunit;

namespace PinqOps.Tests;

public class McpProtocolTests
{
    [Fact]
    public async Task HandleAsync_Initialize_ReturnsServerInfo()
    {
        using var http = new HttpClient(new StubHandler());
        using var request = JsonDocument.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");

        var response = await McpProtocol.HandleAsync(request.RootElement, http);

        Assert.NotNull(response);
        using var document = JsonDocument.Parse(response!);
        Assert.Equal("pinqops", document.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Equal(McpProtocol.ProtocolVersion, document.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task HandleAsync_ToolsList_IncludesExpectedTools()
    {
        using var http = new HttpClient(new StubHandler());
        using var request = JsonDocument.Parse(
            """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""");

        var response = await McpProtocol.HandleAsync(request.RootElement, http);

        Assert.NotNull(response);
        using var document = JsonDocument.Parse(response!);
        var names = document.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToArray();
        Assert.Contains("list_apps", names);
        Assert.Contains("trigger_deploy", names);
        Assert.Contains("container_logs", names);
    }

    [Fact]
    public async Task HandleAsync_ListApps_CallsSettings()
    {
        var handler = new StubHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://example/") };
        using var request = JsonDocument.Parse(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_apps","arguments":{}}}""");

        var response = await McpProtocol.HandleAsync(request.RootElement, http);

        Assert.NotNull(response);
        Assert.Contains(handler.Paths, path => path.Contains("api/settings", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(response!);
        Assert.False(document.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri?.PathAndQuery ?? "");
            var body = """{"apps":[]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
