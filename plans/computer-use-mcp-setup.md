# VPNRouter Test MCP server

In-tree minimal MCP (Model Context Protocol) server that gives Claude
Code direct mouse / keyboard / screenshot access to the Windows
desktop, so it can drive the Avalonia GUI for repro / smoke tests
without asking the user to do it manually.

**Status (2026-05-01)**: implemented in `tools/VpnRouterTestMcp/`,
wired in `.mcp.json` at project root, Windows-only.

## What it exposes

After the MCP server is loaded by Claude Code, Claude has these
additional tools (prefix `mcp__vpnrouter-test__`):

| Tool | Purpose |
|---|---|
| `screenshot(x?,y?,width?,height?)` | Capture primary screen (or region) → returns base64 PNG that Claude can view directly. |
| `list_windows(title_filter?)` | Enumerate visible top-level windows with title / class / bounds. |
| `focus_window(title)` | Bring window matching title substring to foreground. |
| `mouse_click(x, y, button?, count?)` | Click at absolute screen coords. button=left/right/middle, count=1/2 for double-click. |
| `mouse_move(x, y)` | Move cursor without clicking. |
| `type_text(text, delay_ms?)` | Type a Unicode string into the focused window. |
| `press_key(key)` | Press special key or combo. Examples: `Enter`, `Tab`, `ctrl+c`, `alt+F4`. |
| `wait(ms)` | Sleep N milliseconds. |

All tools run in-process with the MCP server, no external dependencies
beyond `System.Drawing.Common` (NuGet).

## Build & enable

One-time:

```bash
cd C:/Project/VPNRouter
dotnet build tools/VpnRouterTestMcp -c Release
```

This produces `tools/VpnRouterTestMcp/bin/Release/net8.0-windows/VpnRouterTestMcp.dll`.

The `.mcp.json` at project root references this DLL via:

```json
{
  "mcpServers": {
    "vpnrouter-test": {
      "command": "dotnet",
      "args": [
        "./tools/VpnRouterTestMcp/bin/Release/net8.0-windows/VpnRouterTestMcp.dll"
      ]
    }
  }
}
```

After build, **restart Claude Code** in this project directory for the
MCP server to be discovered. After restart, `mcp__vpnrouter-test__*`
tools appear in Claude's available tools list.

## Smoke test (manual)

```powershell
# Sanity check that the MCP server replies to JSON-RPC on stdio:
@"
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
{"jsonrpc":"2.0","id":2,"method":"tools/list"}
"@ | dotnet "C:\Project\VPNRouter\tools\VpnRouterTestMcp\bin\Release\net8.0-windows\VpnRouterTestMcp.dll"
```

Expected: 3 JSON-RPC responses on stdout. `initialize` returns
`serverInfo.name = "vpnrouter-test-mcp"`. `tools/list` shows 8 tools.

## Typical session usage

```
> User: "проверь Bug 1 в Servers tab"
> Claude:
>   mcp__vpnrouter-test__list_windows({"title_filter": "VPNRouter"})
>     → hWnd=0xABC123 title='VPNRouter v2.30.2-r2' bounds=(100,80)-(620,720)
>   mcp__vpnrouter-test__focus_window({"title": "VPNRouter"})
>   mcp__vpnrouter-test__screenshot({"x": 100, "y": 80, "width": 520, "height": 640})
>     → [image: VPNRouter open on Servers tab, "Серверы" sub-tab highlighted]
>   ...verifies bug 1 fix is working visually...
>   mcp__vpnrouter-test__mouse_click({"x": 250, "y": 200})  ; click "Подписки" tab
>   mcp__vpnrouter-test__wait({"ms": 500})
>   mcp__vpnrouter-test__screenshot({...})
>     → [image: Subscriptions tab now visible, green dot on connected server]
```

## Safety / scope

- The MCP server has full mouse/keyboard control of the Windows session
  while running. Run only inside the dev VM (VirtualBox guest), never
  on host machine.
- It runs only when Claude Code launches it (subprocess). Disconnects
  when Claude Code exits.
- To temporarily disable, comment-out the entry in `.mcp.json` or rename
  the file to `.mcp.json.disabled` and restart Claude Code.
- Logs go to stderr of the subprocess — visible in Claude Code MCP
  server log (typically under `~/.claude/logs/mcp/`).

## Troubleshooting

- **"Tool not found: mcp__vpnrouter-test__screenshot"**: Claude Code
  hasn't loaded the server. Restart Claude Code after running
  `dotnet build`. Check that `.mcp.json` is at project root, not in a
  subdir.
- **"DLL not found"**: build hasn't been run, or build output path
  changed. Re-run `dotnet build tools/VpnRouterTestMcp -c Release` and
  verify the DLL exists at the path in `.mcp.json`.
- **Server starts but tools fail**: check stderr log for the MCP server
  subprocess (path varies by Claude Code version, often
  `~/.claude/logs/mcp/vpnrouter-test/server.log` or similar).
- **"This call site is reachable on all platforms"**: project is
  Windows-only by design. CA1416 warning is suppressed in csproj.
- **Avalonia window doesn't respond to clicks**: Avalonia uses its own
  input pipeline. SendInput-driven mouse_click works — Avalonia treats
  the cursor at coordinates the same as a real user click. If specific
  controls don't react, ensure the window is foreground first
  (`focus_window`).

## Future work

- UI Automation (UIA) integration: introspect the Avalonia AutomationId
  tree to click controls by AutomationId rather than absolute coords
  (more robust to window moves / resizes).
- Element-relative coords: given a window hWnd, accept coords relative
  to client area instead of screen coords.
- Multi-monitor support: currently only primary screen is captured.
- Headless mode for CI: combine with Avalonia.Headless to drive the
  app without a visible window.

## Cross-refs

- `.mcp.json` — server registration
- `tools/VpnRouterTestMcp/Program.cs` — entry point + stdio loop
- `tools/VpnRouterTestMcp/McpServer.cs` — JSON-RPC dispatcher
- `tools/VpnRouterTestMcp/Tools/` — individual tool implementations
