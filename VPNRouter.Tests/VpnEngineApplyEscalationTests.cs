using System.IO;
using System.Reflection;

namespace VPNRouter.Tests;

/// <summary>
/// v2.31.9-r5 regression pin for the three structural-change triggers
/// that <see cref="VPNRouter.Core.Services.VpnEngine.ApplyAsync"/> MUST
/// escalate to <c>forceRestart = true</c> on. Each trigger has its own
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
    [Fact]
    public void ApplyAsync_HasRoutingModeEscalation()
    {
        var src = LoadVpnEngineSource();
        if (src == null) return; // CI without source — skip
        Assert.Contains("RoutingMode change detected", src);
        // The escalation line must set forceRestart in the same block.
        // Coarse pin: both literals appear within 400 chars of each other.
        AssertNearby(src, "RoutingMode change detected", "forceRestart = true", maxGap: 400);
    }

    [Fact]
    public void ApplyAsync_HasTunFingerprintEscalation()
    {
        var src = LoadVpnEngineSource();
        if (src == null) return;
        Assert.Contains("TUN settings change detected", src);
        AssertNearby(src, "TUN settings change detected", "forceRestart = true", maxGap: 400);
    }

    [Fact]
    public void ApplyAsync_HasProcessListChangeEscalation()
    {
        var src = LoadVpnEngineSource();
        if (src == null) return;
        Assert.Contains("Process list change detected", src);
        AssertNearby(src, "Process list change detected", "forceRestart = true", maxGap: 400);
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

    private static void AssertNearby(string src, string a, string b, int maxGap)
    {
        int idxA = src.IndexOf(a);
        Assert.True(idxA >= 0, $"Source must contain '{a}'");
        // Search for `b` AFTER `a`.
        int idxB = src.IndexOf(b, idxA);
        Assert.True(idxB >= 0, $"Source must contain '{b}' AFTER '{a}'");
        int gap = idxB - idxA;
        Assert.True(gap <= maxGap,
            $"'{a}' and '{b}' should be in the same block (within {maxGap} chars); actual gap = {gap}");
    }
}
