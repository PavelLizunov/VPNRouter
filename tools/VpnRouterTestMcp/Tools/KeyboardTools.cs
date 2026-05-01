using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace VpnRouterTestMcp.Tools;

[SupportedOSPlatform("windows")]
public class TypeTextTool : ITool
{
    public string Name => "type_text";

    public string Description => @"Type a string into the currently-focused window using SendInput.
Each character is sent as a Unicode keystroke with a small delay between characters.
For special keys (Enter, Tab, Esc, F1-F12, Ctrl+X), use press_key instead.";

    public string InputSchemaJson => @"{
        ""type"": ""object"",
        ""required"": [""text""],
        ""properties"": {
            ""text"": { ""type"": ""string"", ""description"": ""Plain text to type. Newlines are NOT translated to Enter — use press_key for that."" },
            ""delay_ms"": { ""type"": ""integer"", ""default"": 10, ""description"": ""Inter-character delay in milliseconds."" }
        }
    }";

    public async Task<ToolResult> InvokeAsync(JsonObject arguments)
    {
        var text = arguments["text"]?.GetValue<string>() ?? throw new ArgumentException("text required");
        var delay = arguments["delay_ms"]?.GetValue<int>() ?? 10;

        foreach (var ch in text)
        {
            SendUnicodeChar(ch);
            await Task.Delay(delay);
        }

        return ToolResult.Text($"Typed {text.Length} character(s).");
    }

    private static void SendUnicodeChar(char ch)
    {
        var inputs = new[]
        {
            new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = ch,
                        dwFlags = KEYEVENTF_UNICODE,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            },
            new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = ch,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            }
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}

[SupportedOSPlatform("windows")]
public class PressKeyTool : ITool
{
    public string Name => "press_key";

    public string Description => @"Press a single keyboard key (or key combination with modifiers) using SendInput.
Examples: 'Enter', 'Tab', 'Escape', 'F5', 'ctrl+a', 'ctrl+shift+s', 'alt+F4'.
Modifiers: ctrl, shift, alt, win. Combine with '+'.";

    public string InputSchemaJson => @"{
        ""type"": ""object"",
        ""required"": [""key""],
        ""properties"": {
            ""key"": { ""type"": ""string"", ""description"": ""Key name or combo. Examples: 'Enter', 'Escape', 'Tab', 'F5', 'ctrl+c', 'alt+F4'."" }
        }
    }";

    private static readonly Dictionary<string, ushort> KeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = 0x0D,
        ["return"] = 0x0D,
        ["tab"] = 0x09,
        ["escape"] = 0x1B,
        ["esc"] = 0x1B,
        ["space"] = 0x20,
        ["backspace"] = 0x08,
        ["delete"] = 0x2E,
        ["del"] = 0x2E,
        ["home"] = 0x24,
        ["end"] = 0x23,
        ["pageup"] = 0x21,
        ["pagedown"] = 0x22,
        ["up"] = 0x26,
        ["down"] = 0x28,
        ["left"] = 0x25,
        ["right"] = 0x27,
        ["f1"] = 0x70, ["f2"] = 0x71, ["f3"] = 0x72, ["f4"] = 0x73,
        ["f5"] = 0x74, ["f6"] = 0x75, ["f7"] = 0x76, ["f8"] = 0x77,
        ["f9"] = 0x78, ["f10"] = 0x79, ["f11"] = 0x7A, ["f12"] = 0x7B,
        // Modifiers (used in combos)
        ["ctrl"] = 0x11,
        ["control"] = 0x11,
        ["shift"] = 0x10,
        ["alt"] = 0x12,
        ["win"] = 0x5B,
    };

    public Task<ToolResult> InvokeAsync(JsonObject arguments)
    {
        var keySpec = arguments["key"]?.GetValue<string>() ?? throw new ArgumentException("key required");

        var parts = keySpec.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var modifiers = new List<ushort>();
        ushort? mainKey = null;

        foreach (var part in parts)
        {
            if (KeyMap.TryGetValue(part, out var vk))
            {
                if (vk == 0x10 || vk == 0x11 || vk == 0x12 || vk == 0x5B)
                    modifiers.Add(vk);
                else
                    mainKey = vk;
            }
            else if (part.Length == 1)
            {
                // Single character — convert to VK using VkKeyScan
                var ch = part.ToUpperInvariant()[0];
                var scan = VkKeyScan(ch);
                mainKey = (ushort)(scan & 0xFF);
            }
            else
            {
                return Task.FromResult(ToolResult.Error($"Unknown key: '{part}'"));
            }
        }

        if (mainKey == null)
            return Task.FromResult(ToolResult.Error($"No main key in '{keySpec}' (only modifiers?)"));

        // Press modifiers down
        foreach (var mod in modifiers)
            SendKey(mod, false);
        // Press main key down + up
        SendKey(mainKey.Value, false);
        SendKey(mainKey.Value, true);
        // Release modifiers (reverse order)
        for (int i = modifiers.Count - 1; i >= 0; i--)
            SendKey(modifiers[i], true);

        return Task.FromResult(ToolResult.Text($"Pressed: {keySpec}"));
    }

    private static void SendKey(ushort vk, bool keyUp)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short VkKeyScan(char ch);

    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}

public class WaitTool : ITool
{
    public string Name => "wait";

    public string Description => "Sleep for the specified number of milliseconds. Useful between clicks for UI to settle.";

    public string InputSchemaJson => @"{
        ""type"": ""object"",
        ""required"": [""ms""],
        ""properties"": {
            ""ms"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 30000, ""description"": ""Sleep duration in milliseconds (1-30000)."" }
        }
    }";

    public async Task<ToolResult> InvokeAsync(JsonObject arguments)
    {
        var ms = arguments["ms"]?.GetValue<int>() ?? throw new ArgumentException("ms required");
        if (ms < 1) ms = 1;
        if (ms > 30000) ms = 30000;
        await Task.Delay(ms);
        return ToolResult.Text($"Waited {ms}ms.");
    }
}
