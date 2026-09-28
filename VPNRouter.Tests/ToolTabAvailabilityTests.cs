using VPNRouter.App.ViewModels.Internals;
using Xunit;

namespace VPNRouter.Tests;

public class ToolTabAvailabilityTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void ToolsTabVisible_TrueWhenAnyToolAvailable(bool z, bool t, bool expected)
        => Assert.Equal(expected, ToolTabAvailability.ToolsTabVisible(z, t));

    [Theory]
    [InlineData(true, true, 0)]
    [InlineData(false, true, 1)]
    [InlineData(false, false, 0)]
    public void DefaultToolIndex_FirstAvailableSubTab(bool z, bool t, int expected)
        => Assert.Equal(expected, ToolTabAvailability.DefaultToolIndex(z, t));
}
