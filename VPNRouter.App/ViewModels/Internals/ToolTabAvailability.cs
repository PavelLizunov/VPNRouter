namespace VPNRouter.App.ViewModels.Internals;

/// <summary>
/// Pure gating for the Tools tab and its sub-tabs. The Tools tab hosts Zapret
/// (Windows-only) and Telegram proxy (Windows-only).
/// </summary>
public static class ToolTabAvailability
{
    /// <summary>The Tools tab is visible when at least one sub-tool is available.</summary>
    public static bool ToolsTabVisible(bool zapret, bool tgProxy)
        => zapret || tgProxy;

    /// <summary>
    /// Index of the first AVAILABLE sub-tab (Zapret=0, TgProxy=1).
    /// Falls back to 0 when nothing is available (the tab is hidden anyway).
    /// </summary>
    public static int DefaultToolIndex(bool zapret, bool tgProxy)
        => zapret ? 0 : tgProxy ? 1 : 0;
}
