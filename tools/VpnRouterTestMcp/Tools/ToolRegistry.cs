using System.Collections.Generic;
using System.Linq;

namespace VpnRouterTestMcp.Tools;

/// <summary>
/// Registry of all tools exposed by the MCP server.
/// </summary>
public class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools;

    public ToolRegistry()
    {
        _tools = new Dictionary<string, ITool>
        {
            ["screenshot"] = new ScreenshotTool(),
            ["list_windows"] = new ListWindowsTool(),
            ["focus_window"] = new FocusWindowTool(),
            ["mouse_click"] = new MouseClickTool(),
            ["mouse_move"] = new MouseMoveTool(),
            ["type_text"] = new TypeTextTool(),
            ["press_key"] = new PressKeyTool(),
            ["wait"] = new WaitTool(),
        };
    }

    public IEnumerable<ITool> All => _tools.Values;

    public ITool? Get(string name) => _tools.TryGetValue(name, out var t) ? t : null;
}
