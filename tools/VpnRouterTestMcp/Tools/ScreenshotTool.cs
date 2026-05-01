using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace VpnRouterTestMcp.Tools;

[SupportedOSPlatform("windows")]
public class ScreenshotTool : ITool
{
    public string Name => "screenshot";

    public string Description => @"Capture a screenshot of the entire primary screen (or a region) and return as base64 PNG.
The result is an image content block that the model can view directly.
Use to inspect the current state of the desktop / VPNRouter window.";

    public string InputSchemaJson => @"{
        ""type"": ""object"",
        ""properties"": {
            ""x"": { ""type"": ""integer"", ""description"": ""Optional region top-left x. Default: 0."" },
            ""y"": { ""type"": ""integer"", ""description"": ""Optional region top-left y. Default: 0."" },
            ""width"": { ""type"": ""integer"", ""description"": ""Optional region width. Default: full primary screen width."" },
            ""height"": { ""type"": ""integer"", ""description"": ""Optional region height. Default: full primary screen height."" }
        }
    }";

    public Task<ToolResult> InvokeAsync(JsonObject arguments)
    {
        // Get primary screen size via P/Invoke
        int screenWidth = GetSystemMetrics(SM_CXSCREEN);
        int screenHeight = GetSystemMetrics(SM_CYSCREEN);

        int x = arguments["x"]?.GetValue<int>() ?? 0;
        int y = arguments["y"]?.GetValue<int>() ?? 0;
        int w = arguments["width"]?.GetValue<int>() ?? screenWidth;
        int h = arguments["height"]?.GetValue<int>() ?? screenHeight;

        // Clamp to screen bounds
        if (x < 0) x = 0;
        if (y < 0) y = 0;
        if (x + w > screenWidth) w = screenWidth - x;
        if (y + h > screenHeight) h = screenHeight - y;
        if (w <= 0 || h <= 0)
            return Task.FromResult(ToolResult.Error("Invalid screenshot region after clamping"));

        using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        }

        // Compress to PNG
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        var base64 = Convert.ToBase64String(ms.ToArray());

        var caption = $"Screenshot {w}x{h} at ({x},{y}). Primary screen={screenWidth}x{screenHeight}.";
        return Task.FromResult(ToolResult.Image(base64, caption));
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
}
