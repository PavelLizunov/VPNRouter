using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using VpnRouterTestMcp.Tools;

namespace VpnRouterTestMcp;

/// <summary>
/// MCP server: dispatches JSON-RPC 2.0 requests to handlers.
/// Supported methods:
///   - initialize → return server capabilities
///   - notifications/initialized → no response (client lifecycle hint)
///   - tools/list → enumerate available tools
///   - tools/call → invoke a tool
///   - ping → empty result (heartbeat)
/// </summary>
public class McpServer
{
    private readonly TextWriter _logger;
    private readonly ToolRegistry _tools;
    private bool _initialized;

    public McpServer(TextWriter logger)
    {
        _logger = logger;
        _tools = new ToolRegistry();
    }

    public async Task<string?> HandleAsync(string requestLine)
    {
        var req = JsonNode.Parse(requestLine);
        if (req == null) return null;

        var method = req["method"]?.GetValue<string>();
        var id = req["id"];

        _logger.WriteLine($"[mcp] method={method} id={id?.ToJsonString() ?? "null"}");

        // Notifications (no id) → no response
        if (id == null)
        {
            switch (method)
            {
                case "notifications/initialized":
                    _initialized = true;
                    return null;
                case "notifications/cancelled":
                    return null;
                default:
                    _logger.WriteLine($"[mcp] unhandled notification: {method}");
                    return null;
            }
        }

        // Requests (have id) → must respond
        try
        {
            JsonNode? result = method switch
            {
                "initialize" => HandleInitialize(req),
                "ping" => new JsonObject(),
                "tools/list" => HandleToolsList(),
                "tools/call" => await HandleToolsCallAsync(req),
                _ => throw new InvalidOperationException($"Unknown method: {method}")
            };

            var resp = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id.DeepClone(),
                ["result"] = result
            };
            return resp.ToJsonString();
        }
        catch (Exception ex)
        {
            _logger.WriteLine($"[mcp] error in {method}: {ex.Message}");
            var resp = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id.DeepClone(),
                ["error"] = new JsonObject
                {
                    ["code"] = -32603,
                    ["message"] = ex.Message
                }
            };
            return resp.ToJsonString();
        }
    }

    private JsonObject HandleInitialize(JsonNode req)
    {
        // Echo back protocol version + advertise tools capability.
        // Per MCP spec: server should echo client's protocolVersion if supported.
        var clientProtocol = req["params"]?["protocolVersion"]?.GetValue<string>() ?? "2024-11-05";
        return new JsonObject
        {
            ["protocolVersion"] = clientProtocol,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject()
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "vpnrouter-test-mcp",
                ["version"] = "0.1.0"
            }
        };
    }

    private JsonObject HandleToolsList()
    {
        var tools = new JsonArray();
        foreach (var tool in _tools.All)
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = JsonNode.Parse(tool.InputSchemaJson)
            });
        }
        return new JsonObject
        {
            ["tools"] = tools
        };
    }

    private async Task<JsonObject> HandleToolsCallAsync(JsonNode req)
    {
        var toolName = req["params"]?["name"]?.GetValue<string>()
            ?? throw new ArgumentException("tools/call requires 'name'");
        var arguments = req["params"]?["arguments"] as JsonObject ?? new JsonObject();

        var tool = _tools.Get(toolName)
            ?? throw new InvalidOperationException($"Unknown tool: {toolName}");

        var result = await tool.InvokeAsync(arguments);

        // Wrap tool result in MCP content envelope.
        // result.Content can be text or image; both go into a content array.
        var content = new JsonArray();
        foreach (var item in result.Content)
        {
            content.Add(item.ToJsonNode());
        }

        return new JsonObject
        {
            ["content"] = content,
            ["isError"] = result.IsError
        };
    }
}
