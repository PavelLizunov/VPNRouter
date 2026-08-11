using System.IO;
using System.Reflection;

namespace VPNRouter.Tests;

/// <summary>
/// v2.49 regression pin for the four structural-change triggers
/// that <see cref="VPNRouter.Core.Services.VpnEngine.ApplyAsync"/> MUST
/// aggregate into <c>forceRestart</c>. Each trigger has its own
/// brat-2026-05-04..05 / earlier user-report origin:
///
/// <list type="number">
/// <item>
/// <b>RoutingMode change (split → full / full → split)</b> —
/// <c>v2.27.1</c>. Hot-reload accepts the new config (HTTP 204) but the
/// live TUN keeps its old route table, so e.g. flipping to full keeps
/// some traffic routed direct. brat-2026-05-04: «split в full ... мне
/// недостаточно нажать применить».
/// </item>
/// <item>
/// <b>TUN fingerprint change</b> — <c>v2.27.2</c>. Adapter properties
/// (interface name, IPv4, MTU, AutoRoute, StrictRoute, IPv6, RouteExclude)
/// are written into the kernel route table at adapter creation; Clash
/// API can't re-run the installer.
/// </item>
/// <item>
/// <b>Process list mutation</b> — <c>v2.31.8-r4</c>. Adding / removing
/// apps from the split-tunnel list while they hold open TCP sockets:
/// hot-reload accepts the rule but existing connections were routed at
/// SYN-time per the OLD rules and stay there until the app reconnects.
/// User report: «нажал применить, но ничего не изменилось — пришлось
/// stop+start».
/// </item>
/// </list>
///
/// <para>If any of these escalation paths are accidentally removed in
/// a future refactor, this test fails loudly. We pin the SOURCE strings
/// rather than the IL because <c>ApplyAsync</c> has many filesystem
/// dependencies that make true unit-testing expensive — string pin is
/// cheap and catches the regression class.</para>
/// </summary>
public sealed class VpnEngineApplyEscalationTests
{
    private const string StructuralAggregation =
        "forceRestart |= configModeChanged || routingModeChanged || tunChanged || appRoutingChanged;";

    [Fact]
    public void ApplyAsync_HasConfigModeEscalation()
    {
        var src = LoadVpnEngineSource();
        if (src == null) return;
        Assert.Contains("ConfigMode change detected", src);
        Assert.Contains(StructuralAggregation, src);
    }

    [Fact]
    public void ApplyAsync_HasRoutingModeEscalation()
    {
        var src = LoadVpnEngineSource();
        if (src == null) return; // CI without source — skip
        Assert.Contains("RoutingMode change detected", src);
        Assert.Contains(StructuralAggregation, src);
    }

    [Fact]
    public void ApplyAsync_HasTunFingerprintEscalation()
    {
        var src = LoadVpnEngineSource();
        if (src == null) return;
        Assert.Contains("TUN settings change detected", src);
        Assert.Contains(StructuralAggregation, src);
    }

    [Fact]
    public void ApplyAsync_HasEffectiveAppRoutingChangeEscalation()
    {
        var src = LoadVpnEngineSource();
        if (src == null) return;
        Assert.Contains("Effective app routing change detected", src);
        Assert.Contains("ComputeAppRoutingFingerprint", src);
        Assert.Contains(StructuralAggregation, src);
    }

    [Fact]
    public void ApplyAsync_PassesForceRestartToReloadConfigJson()
    {
        // Pin the v2.31.7-r1 fix: ReloadConfigJson MUST receive the
        // forceRestart parameter, otherwise the structural-change intent
        // gets lost and TryHotReload runs first regardless. brat-2026-05-04
        // 16:17:32 logs: PID stayed the same despite "Forced full restart".
        var src = LoadVpnEngineSource();
        if (src == null) return;
        // Either ReloadConfigJson(configJson, forceRestart) or
        // ReloadConfigJson(configJson, true) — both honour the contract.
        var hasForceRestartArg =
            src.Contains("ReloadConfigJson(configJson, forceRestart)") ||
            src.Contains("ReloadConfigJson(configJson, true)");
        Assert.True(hasForceRestartArg,
            "VpnEngine.ApplyAsync must pass forceRestart through to SingBoxManager.ReloadConfigJson — see v2.31.7-r1 brat fix.");
        var aggregationIndex = src.IndexOf(StructuralAggregation, StringComparison.Ordinal);
        var reloadIndex = src.IndexOf(
            "ReloadConfigJson(configJson, forceRestart)",
            StringComparison.Ordinal);
        Assert.True(aggregationIndex >= 0 && reloadIndex > aggregationIndex,
            "Structural changes must aggregate before ReloadConfigJson consumes forceRestart.");
    }

    private static string? LoadVpnEngineSource()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "VPNRouter.Core", "Services", "VpnEngine.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        return null;
    }

}
