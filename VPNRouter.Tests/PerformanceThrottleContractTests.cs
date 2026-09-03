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

    [Fact]
    public void DesktopStartup_ServiceViewModel_EagerRefreshDisabledByDefault()
    {
        var serviceVm = LoadSource("VPNRouter.App", "ViewModels", "ServiceViewModel.cs");
        var bootstrap = LoadSource("VPNRouter.App", "ViewModels", "MainWindowViewModel.AutostartBootstrap.cs");

        // Verify ServiceViewModel constructor defaults eagerRefresh to false
        Assert.Contains("public ServiceViewModel(ILogger logger, bool eagerRefresh = false)", serviceVm);
        Assert.Contains("if (eagerRefresh)", serviceVm);

        // Verify BootstrapAutostart does not invoke ServiceVm.Refresh on the UI thread
        Assert.DoesNotContain("InvokeAsync(() => ServiceVm.Refresh())", bootstrap);
        Assert.Contains("ServiceVm.Refresh()", bootstrap);
    }

    [Fact]
    public void DesktopDnsFlusher_NativeDnsFlush_WiresDllImport()
    {
        var dnsFlusher = LoadSource("VPNRouter.Core", "Services", "DnsFlusher.cs");

        Assert.Contains("DnsFlushResolverCache", dnsFlusher);
        Assert.Contains("dnsapi.dll", dnsFlusher);
    }

    [Fact]
    public void DesktopFirewallManager_CleanupOrphanedRules_UsesSinglePassFindRulesByPrefixes()
    {
        var fw = LoadSource("VPNRouter.Core", "Services", "FirewallManager.cs");

        Assert.Contains("var orphaned = FindRulesByPrefixes(AllPrefixes);", fw);
        Assert.Contains("FindRulesByPrefixes(IEnumerable<string> prefixes)", fw);
    }

    [Fact]
    public void DesktopApp_WiresAppAutomationDriverLifecycle()
    {
        var program = LoadSource("VPNRouter.App", "Program.cs");
        var appAxaml = LoadSource("VPNRouter.App", "App.axaml.cs");

        Assert.Contains("AppAutomationDriver.ParseArgs(args);", program);
        Assert.Contains("AppAutomationDriver.StartIfConfigured(mainWindow, _viewModel);", appAxaml);
        Assert.Contains("AppAutomationDriver.Stop();", appAxaml);
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
