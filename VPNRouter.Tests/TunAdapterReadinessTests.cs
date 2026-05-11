using System;
using System.Reflection;
using System.Threading.Tasks;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// v2.31.9-r4 regression suite for the TUN-race bug surfaced by
/// brat-2026-05-05. The user logged a FATAL "configure tun interface:
/// The device is not ready for use" 16 seconds after Apply triggered
/// a restart of sing-box. Root cause: pre-r4 only
/// <see cref="VpnEngine.StartAsync"/> called
/// <see cref="TunAdapterDiagnostics.EnsureAdapterEnabledOrAbsent"/> —
/// the auto-restart paths (Apply hot-reload-fallback, HealthMonitor
/// crash recovery) bypassed the pre-enable, so a wintun adapter left
/// in admin=disabled state by a prior r5 cleanup remained disabled
/// when the new sing-box tried to claim it.
///
/// <para>These tests pin the post-r4 contract: the readiness check
/// lives at the single launch chokepoint and never throws on
/// non-Windows / missing netsh / weird adapter state.</para>
/// </summary>
public sealed class TunAdapterReadinessTests
{
    [Fact]
    public void EnsureAdapterEnabledOrAbsent_NonWindows_NoOp()
    {
        // On Linux/macOS the call should silently no-op, not throw.
        // This pins the OperatingSystem.IsWindows() guard at the top of
        // the method.
        var ex = Record.Exception(() =>
            TunAdapterDiagnostics.EnsureAdapterEnabledOrAbsent(
                logger: null, interfaceName: "VPNRouter-TUN", context: "test.non-windows"));
        Assert.Null(ex);
    }

    [Fact]
    public void EnsureAdapterEnabledOrAbsent_EmptyInterfaceName_NoOp()
    {
        var ex = Record.Exception(() =>
            TunAdapterDiagnostics.EnsureAdapterEnabledOrAbsent(
                logger: null, interfaceName: "", context: "test.empty"));
        Assert.Null(ex);
    }

    [Fact]
    public void EnsureAdapterEnabledOrAbsent_NullInterfaceName_NoOp()
    {
        var ex = Record.Exception(() =>
            TunAdapterDiagnostics.EnsureAdapterEnabledOrAbsent(
                logger: null, interfaceName: null!, context: "test.null"));
        Assert.Null(ex);
    }

    [Fact]
    public void EnsureAdapterEnabledOrAbsent_NonExistentAdapter_NoThrow()
    {
        // On Windows: this exercises netsh against an adapter that does
        // not exist. netsh exits 1 with "not found" — our code treats
        // that as success ("nothing to clean"). On non-Windows this is
        // the same no-op as the guard test above.
        var ex = Record.Exception(() =>
            TunAdapterDiagnostics.EnsureAdapterEnabledOrAbsent(
                logger: null,
                interfaceName: "VPNRouter-Test-DoesNotExist-" + Guid.NewGuid().ToString("N"),
                context: "test.nonexistent"));
        Assert.Null(ex);
    }

    [Fact]
    public void DisableOrphanedAdapter_NonExistentAdapter_NoThrow()
    {
        // Same idempotency contract on the disable side. After r5 (this
        // method's first appearance) we relied on the "exit 1 not found"
        // path being non-fatal so HealthMonitor restart sequences never
        // fail because of orphan-cleanup hiccups.
        var ex = Record.Exception(() =>
            TunAdapterDiagnostics.DisableOrphanedAdapter(
                logger: null,
                interfaceName: "VPNRouter-Test-DoesNotExist-" + Guid.NewGuid().ToString("N"),
                context: "test.nonexistent"));
        Assert.Null(ex);
    }

    // ─── Bug-r9-H regression suite ────────────────────────────────────
    // Pre-start cleanup of stale wintun adapters left behind by a previous
    // sing-box CRASH (graceful Stop is already covered by
    // DisableOrphanedAdapter on the way out). The parser is the testable
    // surface — full PreStartCleanupAsync involves netsh + PowerShell I/O
    // which we exercise only via the non-Windows no-op path here.

    [Fact]
    public async Task PreStartCleanupAsync_NonWindows_ReturnsZeroNoOp()
    {
        // On Linux/macOS this must be a silent zero-removal no-op,
        // never throw. Pins the OperatingSystem.IsWindows() guard.
        // On Windows the test environment shouldn't have a stale
        // VPNRouter-TUN adapter unless the dev machine is mid-reproduce,
        // so we don't assert on the count there.
        var n = await TunAdapterDiagnostics.PreStartCleanupAsync(
            logger: null, context: "test.non-windows");

        if (!OperatingSystem.IsWindows())
            Assert.Equal(0, n);
    }

    [Fact]
    public void TunDiag_PreStartCleanup_NoTunAdapters_ReturnsSuccessNoOp()
    {
        // Empty netsh output — nothing to clean, parser returns empty list.
        // PreStartCleanupAsync would log "no stale TUN adapters found" and
        // return 0 in production; we exercise the same predicate path here.
        Assert.Empty(TunAdapterDiagnostics.ExtractStaleAdapterNames(string.Empty));

        var noTun = """
            Admin State    State          Type             Interface Name
            -------------------------------------------------------------------------
            Enabled        Connected      Dedicated        Ethernet
            Enabled        Connected      Dedicated        Wi-Fi
            Enabled        Disconnected   Loopback         Loopback Pseudo-Interface 1
            """;
        Assert.Empty(TunAdapterDiagnostics.ExtractStaleAdapterNames(noTun));
    }

    [Fact]
    public void TunDiag_PreStartCleanup_OneStaleTun_RemovesIt()
    {
        // VPNRouter-TUN row in netsh inventory — parser surfaces it as a
        // removal target. PreStartCleanupAsync would then run
        // netsh disable + Remove-NetAdapter against this name.
        var output = """
            Admin State    State          Type             Interface Name
            -------------------------------------------------------------------------
            Enabled        Connected      Dedicated        Ethernet
            Enabled        Disconnected   Dedicated        VPNRouter-TUN
            """;
        var result = TunAdapterDiagnostics.ExtractStaleAdapterNames(output);
        Assert.Single(result);
        Assert.Equal("VPNRouter-TUN", result[0], ignoreCase: true);
    }

    [Fact]
    public void TunDiag_PreStartCleanup_SingBoxFallbackName_Detects()
    {
        // sing-box's auto-name fallback when our InterfaceName isn't honoured —
        // pattern is "sing-box-tun" + optional "-XXXX" suffix. Both forms
        // (bare and suffixed) belong to us, both are removable.
        var bare = TunAdapterDiagnostics.ExtractStaleAdapterNames("""
            Admin State    State          Type             Interface Name
            Enabled        Disconnected   Dedicated        sing-box-tun
            """);
        Assert.Single(bare);

        var suffixed = TunAdapterDiagnostics.ExtractStaleAdapterNames("""
            Admin State    State          Type             Interface Name
            Enabled        Disconnected   Dedicated        sing-box-tun-abc12345
            """);
        Assert.Single(suffixed);
        Assert.Equal("sing-box-tun-abc12345", suffixed[0], ignoreCase: true);
    }

    [Fact]
    public void TunDiag_PreStartCleanup_UnrelatedWintunAdapter_LeavesAlone()
    {
        // CRITICAL defensive test: WireGuard, AmneziaWG, OpenVPN TAP and
        // other coexisting VPN tools all create wintun-class adapters with
        // their own names. PreStartCleanupAsync must NEVER touch them —
        // Bug-r9-E (separate chip) handles "another VPN detected" UX, this
        // path is for VPNRouter's own orphans only. A regression here
        // would silently kill the user's other VPN.
        var output = """
            Admin State    State          Type             Interface Name
            -------------------------------------------------------------------------
            Enabled        Connected      Dedicated        Ethernet
            Enabled        Connected      Dedicated        Wintun Userspace Tunnel
            Enabled        Connected      Dedicated        wg-AmneziaWG
            Enabled        Connected      Dedicated        TAP-Windows Adapter V9
            Enabled        Connected      Dedicated        OpenVPN Wintun
            """;
        var result = TunAdapterDiagnostics.ExtractStaleAdapterNames(output);
        Assert.Empty(result);
    }

    [Fact]
    public void TunDiag_PreStartCleanup_MixedAdapters_OnlyOursDetected()
    {
        // Realistic mixed inventory: our adapter alongside someone else's
        // wintun. Only VPNRouter-TUN comes back; the AmneziaWG entry is
        // left alone. Defensive belt-and-braces against the parser drifting
        // toward broad "anything with wintun in the name" matching.
        var output = """
            Admin State    State          Type             Interface Name
            -------------------------------------------------------------------------
            Enabled        Connected      Dedicated        Ethernet
            Enabled        Disconnected   Dedicated        VPNRouter-TUN
            Enabled        Connected      Dedicated        Wintun Userspace Tunnel
            Enabled        Connected      Dedicated        wg0
            """;
        var result = TunAdapterDiagnostics.ExtractStaleAdapterNames(output);
        Assert.Single(result);
        Assert.Equal("VPNRouter-TUN", result[0], ignoreCase: true);
    }

    [Fact]
    public void TunDiag_PreStartCleanup_DuplicateRows_DedupedByName()
    {
        // netsh sometimes lists the same adapter twice (admin-state row +
        // operational-state row, or after a partial rename). The parser
        // dedupes so PreStartCleanupAsync doesn't try to remove the same
        // device twice.
        var output = """
            Admin State    State          Type             Interface Name
            -------------------------------------------------------------------------
            Enabled        Connected      Dedicated        VPNRouter-TUN
            Disabled       Disconnected   Dedicated        VPNRouter-TUN
            """;
        var result = TunAdapterDiagnostics.ExtractStaleAdapterNames(output);
        Assert.Single(result);
    }

    [Fact]
    public void SingBoxManager_DefaultTunInterfaceName_MatchesVpnRouterTun()
    {
        // Pin the constant so a future rename in SingBoxManager doesn't
        // silently desync from
        // <see cref="ConfigGenerator.GenerateTun"/> / install.ps1 / r5
        // orphan cleanup which all assume "VPNRouter-TUN".
        //
        // The constant is private (intentionally — it's an internal
        // detail), but
        // <c>InternalsVisibleTo("VPNRouter.Tests")</c> isn't enough for
        // private-static access. Use reflection to read it; this also
        // catches accidental visibility changes (e.g. someone marking
        // it public, which would break the encapsulation).
        var field = typeof(SingBoxManager).GetField(
            "DefaultTunInterfaceName",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var value = (string?)field!.GetValue(null);
        Assert.Equal("VPNRouter-TUN", value);
    }
}
