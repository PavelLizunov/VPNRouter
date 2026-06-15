namespace VPNRouter.App.ViewModels.Internals;

/// <summary>
/// Pure gating for the Tools tab and its sub-tabs. The Tools tab hosts Zapret
/// (Windows-only), Telegram proxy (Windows-only) and the wgturn Emergency Channel
/// (Windows / macOS / Linux — the <c>wgturn-cli</c> binary is fetched on-demand
/// per platform by <c>WgturnUpdater</c>).
///
/// <para>Before 2026-06-15 the WHOLE Tools tab was gated on Zapret availability
/// (Windows-only), which also hid the Emergency Channel on macOS/Linux even though
/// it works there. This helper makes the tab visible when ANY sub-tool is available
/// and picks a default sub-tab that is actually visible, so a hidden tool (e.g. the
/// Windows-only Zapret page on macOS) is never pre-selected. Extracted as a pure
/// helper because the real availability comes from <c>OperatingSystem.*</c>, which
/// isn't injectable — this keeps the branching unit-testable on any host.</para>
/// </summary>
public static class ToolTabAvailability
{
    /// <summary>The Tools tab is visible when at least one sub-tool is available.</summary>
    public static bool ToolsTabVisible(bool zapret, bool tgProxy, bool emergencyChannel)
        => zapret || tgProxy || emergencyChannel;

    /// <summary>
    /// Index of the first AVAILABLE sub-tab (Zapret=0, TgProxy=1, EmergencyChannel=2)
    /// so macOS/Linux default to the Emergency Channel instead of the hidden Zapret
    /// page. Falls back to 0 when nothing is available (the tab is hidden anyway).
    /// </summary>
    public static int DefaultToolIndex(bool zapret, bool tgProxy, bool emergencyChannel)
        => zapret ? 0 : tgProxy ? 1 : emergencyChannel ? 2 : 0;
}
