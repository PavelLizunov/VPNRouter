using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace VpnRouterTestMcp.Tools;

[SupportedOSPlatform("windows")]
public class ListWindowsTool : ITool
{
    public string Name => "list_windows";

    public string Description => @"List all visible top-level windows with their titles, class names, and bounds.
Useful for finding the VPNRouter Avalonia window before clicking. Bounds are in screen coordinates.";

    public string InputSchemaJson => @"{
        ""type"": ""object"",
        ""properties"": {
            ""title_filter"": { ""type"": ""string"", ""description"": ""Optional case-insensitive substring to filter window titles. E.g. \""VPNRouter\"" or \""Chrome\""."" }
        }
    }";

    public Task<ToolResult> InvokeAsync(JsonObject arguments)
    {
        var filter = arguments["title_filter"]?.GetValue<string>();
        var windows = new List<string>();

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            int len = GetWindowTextLength(hWnd);
            if (len == 0) return true;

            var titleBuf = new StringBuilder(len + 1);
            GetWindowText(hWnd, titleBuf, titleBuf.Capacity);
            var title = titleBuf.ToString();

            if (filter != null && title.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                return true;

            var classBuf = new StringBuilder(256);
            GetClassName(hWnd, classBuf, classBuf.Capacity);
            var className = classBuf.ToString();

            if (GetWindowRect(hWnd, out var rect))
            {
                windows.Add($"hWnd=0x{hWnd:X} title='{title}' class={className} bounds=({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom}) size={rect.Right - rect.Left}x{rect.Bottom - rect.Top}");
            }
            return true;
        }, IntPtr.Zero);

        if (windows.Count == 0)
            return Task.FromResult(ToolResult.Text(filter != null
                ? $"No visible windows match filter '{filter}'."
                : "No visible windows found."));

        return Task.FromResult(ToolResult.Text(string.Join("\n", windows)));
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }
}

[SupportedOSPlatform("windows")]
public class FocusWindowTool : ITool
{
    public string Name => "focus_window";

    public string Description => @"Bring a window to the foreground by title substring (case-insensitive).
Returns the window's hWnd and bounds on success. Use list_windows first to find the right title.";

    public string InputSchemaJson => @"{
        ""type"": ""object"",
        ""required"": [""title""],
        ""properties"": {
            ""title"": { ""type"": ""string"", ""description"": ""Substring of the window title to match (case-insensitive). E.g. \""VPNRouter\""."" }
        }
    }";

    public Task<ToolResult> InvokeAsync(JsonObject arguments)
    {
        var title = arguments["title"]?.GetValue<string>()
            ?? throw new ArgumentException("title required");

        IntPtr foundHwnd = IntPtr.Zero;
        string foundTitle = "";

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            int len = GetWindowTextLength(hWnd);
            if (len == 0) return true;
            var buf = new StringBuilder(len + 1);
            GetWindowText(hWnd, buf, buf.Capacity);
            var t = buf.ToString();
            if (t.IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                foundHwnd = hWnd;
                foundTitle = t;
                return false; // stop enumeration
            }
            return true;
        }, IntPtr.Zero);

        if (foundHwnd == IntPtr.Zero)
            return Task.FromResult(ToolResult.Error($"No visible window matches '{title}'"));

        // Restore if minimized, then bring forward.
        if (IsIconic(foundHwnd))
            ShowWindow(foundHwnd, SW_RESTORE);
        SetForegroundWindow(foundHwnd);

        if (GetWindowRect(foundHwnd, out var rect))
        {
            return Task.FromResult(ToolResult.Text(
                $"Focused hWnd=0x{foundHwnd:X} title='{foundTitle}' bounds=({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom}) size={rect.Right - rect.Left}x{rect.Bottom - rect.Top}"));
        }
        return Task.FromResult(ToolResult.Text($"Focused hWnd=0x{foundHwnd:X} title='{foundTitle}' (bounds query failed)"));
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private const int SW_RESTORE = 9;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }
}
