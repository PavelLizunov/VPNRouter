using System;
using System.IO;
using System.Linq;
using Xunit;

namespace VPNRouter.Tests;

public sealed class PerformanceThrottleContractTests
{
    [Fact]
    public void AndroidVpnRouterService_ImplementsScreenStateStatsThrottling()
    {
        var java = LoadSource("VPNRouter.Android", "VpnRouterService.java");

        // Verify screenStateReceiver registration and handling
        Assert.Contains("private android.content.BroadcastReceiver screenStateReceiver", java);
        Assert.Contains("private volatile boolean isScreenOn", java);
        Assert.Contains("filter.addAction(Intent.ACTION_SCREEN_ON);", java);
        Assert.Contains("filter.addAction(Intent.ACTION_SCREEN_OFF);", java);
        Assert.Contains("Intent.ACTION_SCREEN_OFF.equals(action)", java);
        Assert.Contains("stopStatsPoller();", java);
        Assert.Contains("Intent.ACTION_SCREEN_ON.equals(action)", java);
        Assert.Contains("startStatsPoller();", java);

        // Verify unregistration in onDestroy
        Assert.Contains("releaseScreenStateReceiver();", java);
    }

    [Fact]
    public void AndroidMainActivity_ThrottlesStatsBroadcastsWhenPaused()
    {
        var mainActivity = LoadSource("VPNRouter.Android", "MainActivity.cs");
        var vpnLifecycle = LoadSource("VPNRouter.Android", "AndroidApp.VpnLifecycle.cs");

        // Verify IsActivityPaused lifecycle tracking
        Assert.Contains("public static bool IsActivityPaused => _isActivityPaused;", mainActivity);
        Assert.Contains("_isActivityPaused = true;", mainActivity);
        Assert.Contains("_isActivityPaused = false;", mainActivity);

        // Verify ActionStats skips UI dispatch when paused
        Assert.Contains("if (_isActivityPaused) break;", mainActivity);

        // Verify diagnostics timer tick checks paused state
        Assert.Contains("if (MainActivity.IsActivityPaused) return;", vpnLifecycle);
    }

    [Fact]
    public void DesktopRuntimeStatus_ThrottlesPollingWhenWindowMinimizedOrHidden()
    {
        var runtimeStatus = LoadSource("VPNRouter.App", "ViewModels", "MainWindowViewModel.RuntimeStatus.cs");

        Assert.Contains("GetMainWindow()", runtimeStatus);
        Assert.Contains("window.WindowState == WindowState.Minimized", runtimeStatus);
        Assert.Contains("!window.IsVisible", runtimeStatus);
    }

    private static string LoadSource(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && directory != null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(
                new[] { directory.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException(
            $"Could not locate repository source: {Path.Combine(relativeParts)}");
    }
}
