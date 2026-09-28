using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;
public class AutostartHelperShapeTests
{
    [Fact]
    public void Disable_When_NotEnabled_DoesNotThrow()
    {
        if (VPNRouter.Core.Platform.AutostartHelper.IsEnabled()) return;
        var ex = Record.Exception(() => VPNRouter.Core.Platform.AutostartHelper.Disable());
        Assert.Null(ex);
    }

    [Fact]
    public void IsEnabled_DoesNotThrow_OnAnyPlatform()
    {
        var ex = Record.Exception(() => VPNRouter.Core.Platform.AutostartHelper.IsEnabled());
        Assert.Null(ex);
    }

    [Fact]
    public void EnsureCurrentPath_When_NotEnabled_ReturnsFalse()
    {
        if (VPNRouter.Core.Platform.AutostartHelper.IsEnabled()) return;
        var fakeExe = OperatingSystem.IsWindows()
            ? @"C:\Program Files\VPNRouter\VPNRouter.App.exe"
            : "/Applications/VPNRouter.app/Contents/MacOS/VPNRouter.App";
        Assert.False(VPNRouter.Core.Platform.AutostartHelper.EnsureCurrentPath(fakeExe));
    }

    [Fact]
    public void EnsureCurrentPath_With_Empty_Path_ReturnsFalse()
    {
        Assert.False(VPNRouter.Core.Platform.AutostartHelper.EnsureCurrentPath(""));
        Assert.False(VPNRouter.Core.Platform.AutostartHelper.EnsureCurrentPath("   "));
    }

    [Fact]
    public void Enable_With_Empty_Path_NoOp()
    {
        var wasEnabled = VPNRouter.Core.Platform.AutostartHelper.IsEnabled();
        VPNRouter.Core.Platform.AutostartHelper.Enable("");
        VPNRouter.Core.Platform.AutostartHelper.Enable("   ");
        Assert.Equal(wasEnabled, VPNRouter.Core.Platform.AutostartHelper.IsEnabled());
    }
}
