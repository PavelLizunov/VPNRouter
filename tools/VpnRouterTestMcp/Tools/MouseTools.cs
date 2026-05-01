using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace VpnRouterTestMcp.Tools;

[SupportedOSPlatform("windows")]
public class MouseClickTool : ITool
{
    public string Name => "mouse_click";

    public string Description => @"Click the mouse at absolute screen coordinates.
Default button is left, single click. Use 'count=2' for double-click.
Cursor moves first, then clicks. Returns the new cursor position.";

    public string InputSchemaJson => @"{
        ""type"": ""object"",
        ""required"": [""x"", ""y""],
        ""properties"": {
            ""x"": { ""type"": ""integer"", ""description"": ""Absolute screen x coordinate."" },
            ""y"": { ""type"": ""integer"", ""description"": ""Absolute screen y coordinate."" },
            ""button"": { ""type"": ""string"", ""enum"": [""left"", ""right"", ""middle""], ""default"": ""left"" },
            ""count"": { ""type"": ""integer"", ""default"": 1, ""description"": ""1 = single click, 2 = double-click."" }
        }
    }";

    public async Task<ToolResult> InvokeAsync(JsonObject arguments)
    {
        var x = arguments["x"]?.GetValue<int>() ?? throw new ArgumentException("x required");
        var y = arguments["y"]?.GetValue<int>() ?? throw new ArgumentException("y required");
        var button = arguments["button"]?.GetValue<string>() ?? "left";
        var count = arguments["count"]?.GetValue<int>() ?? 1;

        SetCursorPos(x, y);
        await Task.Delay(50);

        uint downFlag, upFlag;
        switch (button)
        {
            case "right":
                downFlag = MOUSEEVENTF_RIGHTDOWN;
                upFlag = MOUSEEVENTF_RIGHTUP;
                break;
            case "middle":
                downFlag = MOUSEEVENTF_MIDDLEDOWN;
                upFlag = MOUSEEVENTF_MIDDLEUP;
                break;
            default:
                downFlag = MOUSEEVENTF_LEFTDOWN;
                upFlag = MOUSEEVENTF_LEFTUP;
                break;
        }

        for (int i = 0; i < count; i++)
        {
            mouse_event(downFlag, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(20);
            mouse_event(upFlag, 0, 0, 0, UIntPtr.Zero);
            if (i < count - 1) await Task.Delay(50);
        }

        GetCursorPos(out var pt);
        return ToolResult.Text($"Clicked {button} x{count} at ({x},{y}). Cursor now at ({pt.X},{pt.Y}).");
    }

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
}

[SupportedOSPlatform("windows")]
public class MouseMoveTool : ITool
{
    public string Name => "mouse_move";

    public string Description => "Move the mouse cursor to absolute screen coordinates without clicking. Returns new cursor position.";

    public string InputSchemaJson => @"{
        ""type"": ""object"",
        ""required"": [""x"", ""y""],
        ""properties"": {
            ""x"": { ""type"": ""integer"" },
            ""y"": { ""type"": ""integer"" }
        }
    }";

    public Task<ToolResult> InvokeAsync(JsonObject arguments)
    {
        var x = arguments["x"]?.GetValue<int>() ?? throw new ArgumentException("x required");
        var y = arguments["y"]?.GetValue<int>() ?? throw new ArgumentException("y required");
        SetCursorPos(x, y);
        GetCursorPos(out var pt);
        return Task.FromResult(ToolResult.Text($"Cursor moved to ({pt.X},{pt.Y})."));
    }

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
}
