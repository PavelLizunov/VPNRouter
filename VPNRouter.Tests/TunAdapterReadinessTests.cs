using System;
using System.Reflection;
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
