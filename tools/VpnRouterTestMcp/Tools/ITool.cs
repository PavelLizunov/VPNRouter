using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace VpnRouterTestMcp.Tools;

public interface ITool
{
    string Name { get; }
    string Description { get; }
    string InputSchemaJson { get; }
    Task<ToolResult> InvokeAsync(JsonObject arguments);
}

/// <summary>
/// Result envelope returned by every tool. Content is a list of items
/// (each is text or an image — MCP spec supports multi-part content).
/// </summary>
public class ToolResult
{
    public List<ToolContent> Content { get; init; } = new();
    public bool IsError { get; init; }

    public static ToolResult Text(string text) => new()
    {
        Content = new List<ToolContent> { ToolContent.Text(text) }
    };

    public static ToolResult Error(string message) => new()
    {
        Content = new List<ToolContent> { ToolContent.Text(message) },
        IsError = true
    };

    public static ToolResult Image(string base64Png, string? caption = null)
    {
        var content = new List<ToolContent>();
        if (!string.IsNullOrEmpty(caption))
            content.Add(ToolContent.Text(caption));
        content.Add(ToolContent.Image(base64Png, "image/png"));
        return new ToolResult { Content = content };
    }
}

public class ToolContent
{
    public string Type { get; init; } = "text";
    public string? TextValue { get; init; }
    public string? Data { get; init; }
    public string? MimeType { get; init; }

    public static ToolContent Text(string text) => new() { Type = "text", TextValue = text };

    public static ToolContent Image(string base64Data, string mimeType) =>
        new() { Type = "image", Data = base64Data, MimeType = mimeType };

    public JsonNode ToJsonNode()
    {
        var obj = new JsonObject { ["type"] = Type };
        if (Type == "text" && TextValue != null)
            obj["text"] = TextValue;
        if (Type == "image")
        {
            obj["data"] = Data;
            obj["mimeType"] = MimeType;
        }
        return obj;
    }
}
