using VPNRouter.App.ViewModels.Internals;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Pure gating for the Tools tab + sub-tabs (Emergency-Channel-on-macOS/Linux
/// unhide, 2026-06-15). Zapret + Telegram proxy are Windows-only; the wgturn
/// Emergency Channel works on Windows/macOS/Linux. The tab must show whenever ANY
/// sub-tool is available and must NOT pre-select a hidden sub-tab.
/// </summary>
public class ToolTabAvailabilityTests
{
    [Theory]
    [InlineData(true, true, true, true)]     // Windows — Zapret + TgProxy + Emergency
    [InlineData(false, false, true, true)]   // macOS/Linux — only Emergency Channel
    [InlineData(true, false, false, true)]   // Zapret only
    [InlineData(false, false, false, false)] // nothing available -> tab hidden
    public void ToolsTabVisible_TrueWhenAnyToolAvailable(bool z, bool t, bool e, bool expected)
        => Assert.Equal(expected, ToolTabAvailability.ToolsTabVisible(z, t, e));

    [Theory]
    [InlineData(true, true, true, 0)]     // Windows -> Zapret (index 0)
    [InlineData(false, true, true, 1)]    // TgProxy is the first available
    [InlineData(false, false, true, 2)]   // macOS/Linux -> Emergency Channel (index 2)
    [InlineData(false, false, false, 0)]  // none -> 0 (tab is hidden anyway)
    public void DefaultToolIndex_FirstAvailableSubTab(bool z, bool t, bool e, int expected)
        => Assert.Equal(expected, ToolTabAvailability.DefaultToolIndex(z, t, e));
}
