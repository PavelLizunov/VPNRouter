using VPNRouter.App.ViewModels.Internals;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Pure gating for the Tools tab + sub-tabs. Zapret + Telegram proxy are Windows-only.
/// The tab must show whenever ANY sub-tool is available and must NOT pre-select a hidden sub-tab.
/// </summary>
public class ToolTabAvailabilityTests
{
    [Theory]
    [InlineData(true, true, true)]     // Windows — Zapret + TgProxy
    [InlineData(true, false, true)]    // Zapret only
    [InlineData(false, true, true)]    // TgProxy only
    [InlineData(false, false, false)]  // nothing available -> tab hidden
    public void ToolsTabVisible_TrueWhenAnyToolAvailable(bool z, bool t, bool expected)
        => Assert.Equal(expected, ToolTabAvailability.ToolsTabVisible(z, t));

    [Theory]
    [InlineData(true, true, 0)]     // Windows -> Zapret (index 0)
    [InlineData(false, true, 1)]    // TgProxy is the first available
    [InlineData(false, false, 0)]   // none -> 0 (tab is hidden anyway)
    public void DefaultToolIndex_FirstAvailableSubTab(bool z, bool t, int expected)
        => Assert.Equal(expected, ToolTabAvailability.DefaultToolIndex(z, t));
}
