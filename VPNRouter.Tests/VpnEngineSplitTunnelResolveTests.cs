// Reviewer P1 fix #2 (2026-07-05) — wiring tests for VpnEngine.TryEngageSplitDriverAsync's
// resolve loop + empty-set guard.
//
// Two behaviours pinned (both Windows-only, like the sibling lifecycle suite — the engage gate
// is SplitTunnelPolicy.ShouldEngage(OperatingSystem.IsWindows(), ...) and the resolvers are
// [SupportedOSPlatform("windows")]):
//
//   (i)  exclude set resolves to ZERO on-disk paths  → EngageAsync is NOT called, and a
//        previously-engaged driver is DisengageAsync'd (an ENGAGED driver with zero configured
//        paths splits nothing yet lights the badge — so we must not claim engaged).
//   (ii) exclude set resolves to a real path (cmd.exe, always on PATH) → EngageAsync IS called,
//        with the resolved path in the request. This also live-covers ProcessImagePath's
//        ResolveRunningPath → ResolveNameToPath (where.exe) fallback chain.
//
// Why TryEngageSplitDriverAsync directly (not a full StartAsync ColdStart): it's internal exactly
// so the driver wiring can be tested without a ProgramData-touching StartAsync (see its doc-comment).
// The FakeSplitTunnelDriver records Engage/Disengage counts + the last request.
//
// ResolveNameToPath is NOT unit-tested in isolation: ProcessImagePath is a static class with no
// injectable process-runner seam, so a dedicated test would have to spawn the real where.exe. Case
// (ii) live-covers it (cmd.exe resolves via where.exe on every Windows box); a standalone spawn test
// would add nothing but flakiness.

#nullable enable

using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

/// <summary>
/// Wiring tests for <see cref="VpnEngine.TryEngageSplitDriverAsync"/> — the excluded-name → path
/// resolve loop and the "0 paths resolved → don't engage" guard (reviewer P1 fix #2).
/// Companion to <see cref="VpnEngineSplitTunnelLifecycleTests"/> (full ColdStart lifecycle).
/// </summary>
public sealed class VpnEngineSplitTunnelResolveTests
{
    private sealed class StubProcessScanner : IProcessScanner
    {
        public ScanResult ScanForProfile(Profile profile) =>
            new() { ProcessNames = new List<string>(), ScannedAt = DateTime.Now };
    }

    private sealed class StubFirewallManager : IFirewallManager
    {
        public void CreateBlockRules(IEnumerable<string> processNames) { }
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

    /// <summary>Settings that pass <see cref="SplitTunnelPolicy.ShouldEngage"/> (split + exclude +
    /// non-empty list). Only the excluded-name list differs between the two tests.</summary>
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
        // The gate (ShouldEngage) is Windows-only and the resolvers are [SupportedOSPlatform("windows")].
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "TryEngageSplitDriverAsync's engage gate + ProcessImagePath resolvers are Windows-only.");

        var driver = new FakeSplitTunnelDriver();
        var engine = BuildEngine(driver);

        // Simulate a prior successful engage so we can assert the empty-set path disengages it.
        await driver.EngageAsync(new SplitTunnelEngageRequest(new List<string> { @"C:\old.exe" }, "172.19.0.2", null), default);
        Assert.True(driver.IsEngaged);
        int engagesBefore = driver.EngageCount;   // 1

        // An excluded name that is neither running nor on PATH → resolves to nothing.
        var settings = SplitExcludeSettings("zzz-nonexistent-vpnrouter-test.exe");
        await engine.TryEngageSplitDriverAsync(settings, default);

        // EngageAsync must NOT have been called again (still 1 from the prior manual engage).
        Assert.Equal(engagesBefore, driver.EngageCount);
        // The previously-engaged driver was disengaged (don't claim engaged with zero configured paths).
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

        // cmd.exe is in System32 and always on PATH → ResolveNameToPath(where.exe) resolves it even
        // though it isn't running. Exercises the ResolveRunningPath → ResolveNameToPath fallback.
        var settings = SplitExcludeSettings("cmd.exe");
        await engine.TryEngageSplitDriverAsync(settings, default);

        Assert.Equal(1, driver.EngageCount);
        Assert.Equal(0, driver.DisengageCount);
        Assert.NotNull(driver.LastRequest);
        Assert.Single(driver.LastRequest!.ExcludedDosPaths);
        Assert.EndsWith("cmd.exe", driver.LastRequest.ExcludedDosPaths[0], StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(driver.LastRequest.ExcludedDosPaths[0]));
        // TUN v4 flowed into the request; v6 is intentionally null (TunSettings has no v6).
        Assert.Equal("172.19.0.2/30", driver.LastRequest.TunnelIpv4);
        Assert.Null(driver.LastRequest.TunnelIpv6);
    }
}
