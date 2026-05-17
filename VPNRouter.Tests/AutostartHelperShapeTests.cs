using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
/// <summary>v2.29.0-r2: shape and idempotency tests for the new
/// cross-platform <see cref="VPNRouter.Core.Platform.AutostartHelper"/>.
/// We can't unit-test the actual file/registry side effects from here
/// (CI machines don't have stable HKCU\Run state, and writing to
/// ~/Library/LaunchAgents on a Linux runner would 404 on the parent
/// dir), but we CAN exercise the public API surface and assert that:
/// - Disable() is safe to call when not enabled (no exception).
/// - IsEnabled() / Disable() / EnsureCurrentPath() never throw on the
///   current platform.
/// - EnsureCurrentPath() returns false when no entry exists.</summary>
public class AutostartHelperShapeTests
{
    [Fact]
    public void Disable_When_NotEnabled_DoesNotThrow()
    {
        // Don't actually toggle (test must be safe to run on dev machine
        // — we don't want to nuke the user's real autostart setting). Just
        // call IsEnabled() and skip the test if it's currently true.
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
        // Should silently no-op on empty / whitespace path; no autostart
        // entry should appear after the call.
        var wasEnabled = VPNRouter.Core.Platform.AutostartHelper.IsEnabled();
        VPNRouter.Core.Platform.AutostartHelper.Enable("");
        VPNRouter.Core.Platform.AutostartHelper.Enable("   ");
        Assert.Equal(wasEnabled, VPNRouter.Core.Platform.AutostartHelper.IsEnabled());
    }
}
