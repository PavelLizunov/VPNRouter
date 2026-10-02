using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless;
using VPNRouter.Tools.UiProbe;

namespace VPNRouter.Tools.UiMcp;

// Minimal MCP server over stdio: newline-delimited JSON-RPC 2.0. Tools only.
public sealed class McpServer
{
    private const string ServerName = "vpnrouter-ui-mcp";
    private const string ServerVersion = "0.1.0";
    private static readonly string[] SupportedProtocols = { "2025-06-18", "2025-03-26", "2024-11-05" };

    private readonly HeadlessUnitTestSession _ui;
    private readonly Stream _out;
    private readonly object _writeLock = new();

    public McpServer(HeadlessUnitTestSession ui, Stream output)
    {
        _ui = ui;
        _out = output;
    }

    public int Run(TextReader input)
    {
        string? line;
        while ((line = input.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? message;
            try
            {
                message = JsonNode.Parse(line);
            }
            catch (JsonException ex)
            {
                Send(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = null,
                    ["error"] = new JsonObject { ["code"] = -32700, ["message"] = "Parse error: " + ex.Message },
                });
                continue;
            }
            if (message is JsonObject obj) Handle(obj);
        }
        return 0;
    }

    private void Handle(JsonObject message)
    {
        var method = message["method"]?.GetValue<string>();
        var id = message["id"]?.DeepClone();
        var parameters = message["params"] as JsonObject;
        if (method == null) return;                      // a response to something we never asked
        if (id == null && method.StartsWith("notifications/", StringComparison.Ordinal)) return;

        try
        {
            JsonNode result = method switch
            {
                "initialize" => Initialize(parameters),
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = Tools.Definitions() },
                "tools/call" => CallTool(parameters),
                _ => throw new MethodNotFound(method),
            };
            if (id != null) Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
        }
        catch (MethodNotFound ex)
        {
            if (id != null)
                Send(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found: " + ex.Method },
                });
        }
        catch (Exception ex)
        {
            if (id != null)
                Send(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["error"] = new JsonObject { ["code"] = -32603, ["message"] = ex.GetType().Name + ": " + ex.Message },
                });
        }
    }

    private static JsonNode Initialize(JsonObject? parameters)
    {
        var requested = parameters?["protocolVersion"]?.GetValue<string>();
        var version = requested != null && SupportedProtocols.Contains(requested) ? requested : SupportedProtocols[^1];
        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = ServerVersion },
            ["instructions"] =
                "Renders and clicks through the real VPNRouter desktop UI headlessly. Start with ui_catalog. " +
                "ui_render shows one surface, ui_matrix compares themes/languages/sizes on one sheet, ui_tree lists controls, " +
                "ui_sweep clicks controls to look for crashes. Fonts here are not the Windows fonts: trust overflow and overlap, not pixels.",
        };
    }

    private JsonNode CallTool(JsonObject? parameters)
    {
        var name = parameters?["name"]?.GetValue<string>() ?? throw new ArgumentException("tools/call needs a name");
        var args = parameters["arguments"] as JsonObject ?? new JsonObject();
        try
        {
            var content = _ui.Dispatch(() => Tools.Run(name, args), CancellationToken.None);
            return new JsonObject { ["content"] = content, ["isError"] = false };
        }
        catch (Exception ex)
        {
            var text = ex.GetType().Name + ": " + ex.Message;
            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                ["isError"] = true,
            };
        }
    }

    private void Send(JsonObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        lock (_writeLock)
        {
            _out.Write(bytes, 0, bytes.Length);
            _out.Flush();
        }
    }

    private sealed class MethodNotFound : Exception
    {
        public MethodNotFound(string method) : base(method) => Method = method;
        public string Method { get; }
    }
}
