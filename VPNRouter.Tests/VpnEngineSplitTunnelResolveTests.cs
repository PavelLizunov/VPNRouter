#nullable enable

using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class VpnEngineSplitTunnelResolveTests
{
    private sealed class StubProcessScanner : IProcessScanner
    {
        public ScanResult ScanForProfile(Profile profile) =>
            new() { ProcessNames = new List<string>(), ScannedAt = DateTime.Now };
    }

    private sealed class StubFirewallManager : IFirewallManager
    {
        public void CreateBlockRules(IEnumerable<string> processNames, bool isFullTunnel = true) { }
        public void EnableBlockRules() { }
        public void DisableBlockRules() { }
        public void DeleteAllRules() { }
        public void Dispose() { }
    }

    private sealed class StubProcessMonitor : IProcessMonitor
    {
        public event EventHandler<ProcessEventArgs>? ProcessStarted;
        public event EventHandler<ProcessEventArgs>? ProcessStopped;
        public void Start() { _ = ProcessStarted; }
        public void Stop() { _ = ProcessStopped; }
        public void Dispose() { }
    }

#pragma warning disable CS0618
    private static VpnEngine BuildEngine(FakeSplitTunnelDriver driver) =>
        new VpnEngine(
            scanner: new StubProcessScanner(),
            firewallFactory: () => new StubFirewallManager(),
            monitorFactory: () => new StubProcessMonitor(),
            logger: null,
            splitDriver: driver);
#pragma warning restore CS0618

    private static AppSettings SplitExcludeSettings(params string[] excluded) =>
        new()
        {
            App = new AppConfig
            {
                RoutingMode = "split",
                RoutingAppsMode = "exclude",
                RoutingAppsExclude = new List<string>(excluded),
            },
            Tun = new TunSettings { Ipv4Address = "172.19.0.2/30" },
        };

    [Fact]
    public async Task Engage_ExcludeResolvesEmpty_DoesNotEngageAndDisengagesPrior()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "TryEngageSplitDriverAsync's engage gate + ProcessImagePath resolvers are Windows-only.");

        var driver = new FakeSplitTunnelDriver();
        var engine = BuildEngine(driver);

        await driver.EngageAsync(new SplitTunnelEngageRequest(new List<string> { @"C:\old.exe" }, "172.19.0.2", null), TestContext.Current.CancellationToken);
        Assert.True(driver.IsEngaged);
        int engagesBefore = driver.EngageCount;

        var settings = SplitExcludeSettings("zzz-nonexistent-vpnrouter-test.exe");
        await engine.TryEngageSplitDriverAsync(settings, TestContext.Current.CancellationToken);

        Assert.Equal(engagesBefore, driver.EngageCount);
        Assert.Equal(1, driver.DisengageCount);
        Assert.False(driver.IsEngaged);
    }

    [Fact]
    public async Task Engage_ExcludeResolvesToPath_EngagesWithResolvedPaths()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "TryEngageSplitDriverAsync's engage gate + ProcessImagePath resolvers are Windows-only.");

        var driver = new FakeSplitTunnelDriver();
        var engine = BuildEngine(driver);

        var settings = SplitExcludeSettings("cmd.exe");
        await engine.TryEngageSplitDriverAsync(settings, TestContext.Current.CancellationToken);

        Assert.Equal(1, driver.EngageCount);
        Assert.Equal(0, driver.DisengageCount);
        Assert.NotNull(driver.LastRequest);
        Assert.Single(driver.LastRequest!.ExcludedDosPaths);
        Assert.EndsWith("cmd.exe", driver.LastRequest.ExcludedDosPaths[0], StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(driver.LastRequest.ExcludedDosPaths[0]));
        Assert.Equal("172.19.0.2/30", driver.LastRequest.TunnelIpv4);
        Assert.Null(driver.LastRequest.TunnelIpv6);
    }

    [Fact]
    public async Task RestartTrueSplit_ReengagesWithoutForeignDriverTakeover()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "TryEngageSplitDriverAsync's engage gate + ProcessImagePath resolvers are Windows-only.");

        var driver = new FakeSplitTunnelDriver();
        var engine = BuildEngine(driver);

        await engine.RestartTrueSplitAsync(SplitExcludeSettings("cmd.exe"), TestContext.Current.CancellationToken);

        Assert.Equal(1, driver.EngageCount);
        Assert.NotNull(driver.LastRequest);
        Assert.Single(driver.LastRequest!.ExcludedDosPaths);
    }

    [Fact]
    public async Task Engage_DriverMissing_ReportsMissingAndDoesNotEngage()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "TryEngageSplitDriverAsync's engage gate is Windows-only.");

        var driver = new FakeSplitTunnelDriver { IsAvailable = false };
        var engine = BuildEngine(driver);
        var states = new List<TrueSplitState>();
        engine.TrueSplitStateChanged += (state, _) => states.Add(state);

        await engine.TryEngageSplitDriverAsync(SplitExcludeSettings("cmd.exe"), TestContext.Current.CancellationToken);

        Assert.Equal(TrueSplitState.DriverMissing, engine.CurrentTrueSplitState);
        Assert.Contains(TrueSplitState.DriverMissing, states);
        Assert.Equal(0, driver.EngageCount);
    }

    [Fact]
    public async Task Engage_FailedDriverEngage_ReportsFallback()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "TryEngageSplitDriverAsync's engage gate + ProcessImagePath resolvers are Windows-only.");

        var driver = new FakeSplitTunnelDriver
        {
            EngageResult = false,
            LastFailureReason = "True-split driver device \\\\.\\MULLVADSPLITTUNNEL is busy (CreateFile err=5).",
        };
        var engine = BuildEngine(driver);
        var states = new List<TrueSplitState>();
        var reasons = new List<string>();
        engine.TrueSplitStateChanged += (state, reason) =>
        {
            states.Add(state);
            reasons.Add(reason);
        };

        await engine.TryEngageSplitDriverAsync(SplitExcludeSettings("cmd.exe"), TestContext.Current.CancellationToken);

        Assert.Equal(1, driver.EngageCount);
        Assert.Equal(TrueSplitState.Fallback, engine.CurrentTrueSplitState);
        Assert.Contains(TrueSplitState.Starting, states);
        Assert.Contains(TrueSplitState.Fallback, states);
        Assert.Contains(reasons, reason => reason.Contains("err=5", StringComparison.OrdinalIgnoreCase));
    }
}
