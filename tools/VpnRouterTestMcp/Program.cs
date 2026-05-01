using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using VpnRouterTestMcp.Tools;

namespace VpnRouterTestMcp;

/// <summary>
/// VPNRouter test-only MCP server. Exposes tools for Claude Code to drive
/// the Avalonia GUI on Windows: screenshot, mouse click, type text, key press.
///
/// Protocol: JSON-RPC 2.0 over stdio (newline-delimited UTF-8).
/// See: https://spec.modelcontextprotocol.io/specification/basic/transports/
///
/// Logging goes to stderr (so it doesn't interfere with stdout JSON-RPC stream).
/// </summary>
public class Program
{
    public static async Task Main(string[] args)
    {
        // Force UTF-8 stdout (default Windows codepage breaks base64 PNG payloads).
        Console.OutputEncoding = Encoding.UTF8;

        // Disable buffering on stdout so MCP client sees responses immediately.
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = true
        };
        var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        var stderr = Console.Error;

        stderr.WriteLine($"[VpnRouterTestMcp] starting (pid={Environment.ProcessId}, cwd={Environment.CurrentDirectory})");

        var server = new McpServer(stderr);

        string? line;
        while ((line = await stdin.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var response = await server.HandleAsync(line);
                if (!string.IsNullOrEmpty(response))
                {
                    await stdout.WriteLineAsync(response);
                }
            }
            catch (Exception ex)
            {
                stderr.WriteLine($"[VpnRouterTestMcp] handler exception: {ex}");
                // Try to send back a JSON-RPC error if we have a request id
                try
                {
                    var req = JsonNode.Parse(line);
                    var id = req?["id"]?.DeepClone();
                    var errorResp = new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = id,
                        ["error"] = new JsonObject
                        {
                            ["code"] = -32603,
                            ["message"] = $"Internal error: {ex.GetType().Name}: {ex.Message}"
                        }
                    };
                    await stdout.WriteLineAsync(errorResp.ToJsonString());
                }
                catch { /* best effort */ }
            }
        }

        stderr.WriteLine("[VpnRouterTestMcp] stdin closed, exiting");
    }
}
