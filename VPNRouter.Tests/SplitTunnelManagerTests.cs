#nullable enable
// ═══════════════════════════════════════════════════════════════════════════════
// W1.1-P2 — SplitTunnelDriverManager tests.
//   Brief: plans/w1.1-p2-driver-manager-brief.md
//
// Two tiers:
//   1. ClassifyServiceBinPath — the pure collision-guard decision (§3 #3). Runs on every
//      platform incl. Linux CI; this is the load-bearing coverage (it decides whether we
//      ever touch an existing kernel service — a wrong BailForeign→Adopt could hijack a real
//      Mullvad install's driver).
//   2. Manager smoke — construct / Dispose / fail-open-when-files-missing. The manager is
//      [SupportedOSPlatform("windows")], so these guard on OperatingSystem.IsWindows() (the
//      analyzer recognises the guard; Linux CI early-returns → still green). They verify the
//      lazy ctor touches nothing and the #1 fail-open path returns false without a throw —
//      no live driver, no admin, no SCM call.
// ═══════════════════════════════════════════════════════════════════════════════

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Services;
using Xunit;

using P = VPNRouter.Core.Services.SplitTunnelDriverProtocol;

namespace VPNRouter.Tests;

public class SplitTunnelManagerTests
{
    private const string Ours = @"C:\Program Files\VPNRouter\app\driver\mullvad-split-tunnel.sys";

    // ─── Collision guard (pure, cross-platform) ─────────────────────────────────

    [Fact]
    public void ClassifyServiceBinPath_ExactMatch_StartExisting()
        => Assert.Equal(P.ServiceCollisionAction.StartExisting,
            SplitTunnelPolicy.ClassifyServiceBinPath(Ours, Ours));

    [Fact]
    public void ClassifyServiceBinPath_ExactMatch_ToleratesQuotes_NtPrefix_Case()
    {
        // SCM often stores kernel binPaths as "\??\C:\..." and casing/quoting varies.
        const string existing = "\"\\??\\C:\\PROGRAM FILES\\VPNROUTER\\APP\\DRIVER\\MULLVAD-SPLIT-TUNNEL.SYS\"";
        Assert.Equal(P.ServiceCollisionAction.StartExisting,
            SplitTunnelPolicy.ClassifyServiceBinPath(existing, Ours));
    }

    [Fact]
    public void ClassifyServiceBinPath_OurInstallRelocated_AdoptMovedInstall()
    {
        // Same app, different install root — still unmistakably ours (VPNRouter segment) → self-heal.
        const string ours = @"D:\Apps\VPNRouter\app\driver\mullvad-split-tunnel.sys";
        const string existing = @"C:\Program Files\VPNRouter\app\driver\mullvad-split-tunnel.sys";
        Assert.Equal(P.ServiceCollisionAction.AdoptMovedInstall,
            SplitTunnelPolicy.ClassifyServiceBinPath(existing, ours));
    }

    [Fact]
    public void ClassifyServiceBinPath_RealMullvad_BailForeign()
    {
        // A genuine Mullvad install owns the same service name — never touch it (coexist).
        const string existing = @"C:\Program Files\Mullvad VPN\resources\mullvad-split-tunnel.sys";
        Assert.Equal(P.ServiceCollisionAction.BailForeign,
            SplitTunnelPolicy.ClassifyServiceBinPath(existing, Ours));
    }

    [Fact]
    public void ClassifyServiceBinPath_UnknownSquatter_BailForeign()
    {
        // Same filename, not our install path, no VPNRouter marker → don't adopt, fall back.
        const string existing = @"C:\Windows\Temp\evil\mullvad-split-tunnel.sys";
        Assert.Equal(P.ServiceCollisionAction.BailForeign,
            SplitTunnelPolicy.ClassifyServiceBinPath(existing, Ours));
    }

    [Fact]
    public void ClassifyServiceBinPath_SubstringButNotSegment_BailForeign()
    {
        // "NotVpnRouterApp" contains "vpnrouter" as a substring but NOT as a whole path segment
        // (\vpnrouter\) — must NOT be adopted (a raw Contains would have wrongly rewritten its binPath).
        const string existing = @"C:\Program Files\NotVpnRouterApp\driver\mullvad-split-tunnel.sys";
        Assert.Equal(P.ServiceCollisionAction.BailForeign,
            SplitTunnelPolicy.ClassifyServiceBinPath(existing, Ours));
    }

    [Fact]
    public void ClassifyServiceBinPath_VpnRouterSegmentButWrongTail_BailForeign()
    {
        // Has a real \vpnrouter\ segment but NOT our \driver\mullvad-split-tunnel.sys layout tail —
        // a squatter that must NOT be adopted (bug-hunt: keying on the segment alone would rewrite it).
        const string existing = @"C:\Program Files\VpnRouterClone\vpnrouter\weird\thing.sys";
        Assert.Equal(P.ServiceCollisionAction.BailForeign,
            SplitTunnelPolicy.ClassifyServiceBinPath(existing, Ours));
    }

    [Fact]
    public void ClassifyServiceBinPath_UnreadableConfig_BailForeign()
    {
        // Service exists but we couldn't read its binPath → treat as foreign (conservative).
        Assert.Equal(P.ServiceCollisionAction.BailForeign,
            SplitTunnelPolicy.ClassifyServiceBinPath("", Ours));
    }

    // ─── Manager smoke (Windows-only; Linux CI early-returns → green) ───────────

    [Fact]
    public void Manager_ConstructAndDispose_NoThrow_TouchesNothing()
    {
        if (!OperatingSystem.IsWindows()) return;   // [SupportedOSPlatform] guard (analyzer + runtime)

        using var mgr = new SplitTunnelDriverManager(driverDir: NonexistentDir(), ownTunName: "VPNRouter-TUN");
        Assert.False(mgr.IsEngaged);
        Assert.True(mgr.IsPumpHealthy);
        // Dispose (via using) must not throw even though nothing was ever engaged.
    }

    [Fact]
    public async Task EngageAsync_DriverFileMissing_ReturnsFalse_FailOpen_NoThrow()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var mgr = new SplitTunnelDriverManager(driverDir: NonexistentDir());
        var req = new SplitTunnelEngageRequest(
            new[] { @"C:\Windows\System32\curl.exe" }, TunnelIpv4: "172.19.0.2", TunnelIpv6: null);

        bool engaged = await mgr.EngageAsync(req, CancellationToken.None);

        Assert.False(engaged);          // fail-path #1 — .sys absent, returns before any syscall
        Assert.False(mgr.IsEngaged);    // fail-open: no state mutated, network untouched
        Assert.True(mgr.IsPumpHealthy); // pump (P3) never started — health flag holds its default
    }

    [Fact]
    public async Task DisengageAsync_WhenNeverEngaged_Idempotent_NoThrow()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var mgr = new SplitTunnelDriverManager(driverDir: NonexistentDir());
        await mgr.DisengageAsync(CancellationToken.None);
        await mgr.DisengageAsync(CancellationToken.None);   // second call is a no-op
        Assert.False(mgr.IsEngaged);
    }

    [Fact]
    public void CanRepairOwnStoppedServiceStartFailure_OnlyStoppedOurs()
    {
        Assert.True(SplitTunnelPolicy.CanRepairOwnStoppedServiceStartFailure(
            P.ServiceCollisionAction.StartExisting, startError: 1058, serviceState: 1));

        Assert.False(SplitTunnelPolicy.CanRepairOwnStoppedServiceStartFailure(
            P.ServiceCollisionAction.BailForeign, startError: 1058, serviceState: 1));
        Assert.False(SplitTunnelPolicy.CanRepairOwnStoppedServiceStartFailure(
            P.ServiceCollisionAction.StartExisting, startError: 1058, serviceState: 4));
        Assert.False(SplitTunnelPolicy.CanRepairOwnStoppedServiceStartFailure(
            P.ServiceCollisionAction.StartExisting, startError: 5, serviceState: 1));
        Assert.False(SplitTunnelPolicy.CanRepairOwnStoppedServiceStartFailure(
            P.ServiceCollisionAction.StartExisting, startError: 1072, serviceState: 1));
    }

    private static string NonexistentDir()
        => Path.Combine(Path.GetTempPath(), "vpnrouter-split-none-" + Guid.NewGuid().ToString("N"));
}
