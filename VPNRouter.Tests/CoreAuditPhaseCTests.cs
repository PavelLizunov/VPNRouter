using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Phase C (lifecycle) core-functionality audit — regression guards for the
/// three fixes the adversarial sweep (2026-06-04) produced. The HealthMonitor /
/// VpnEngine teardown paths are timer- and process-driven and not cleanly
/// unit-testable in isolation (the existing HealthMonitor suites use a no-op
/// StubFirewallManager), so — matching the FirewallManagerDnsLockdownTests
/// precedent — these are SOURCE-STRING pins that fail loudly if a future
/// refactor silently drops the fix. C5 concurrency core (B1/B2 guarded, B4
/// retired) + the deferred B3 state-machine class are tracked in
/// plans/singbox-lifecycle-hardening-v2.36.md.
/// </summary>
public sealed class CoreAuditPhaseCTests
{
    // ── C3-1: HealthMonitor.Stop() must reset the r10 #3 deferred-lift state ──
    [Fact]
    public void Stop_ResetsDeferredKillSwitchState_C3_1()
    {
        var region = ExtractMethod(LoadCore("HealthMonitor.cs"), "Stop");
        Assert.True(
            region.Contains("_deferredBlockRuleDisable") &&
            region.Contains("_lastFullRestart = DateTime.MinValue"),
            "HealthMonitor.Stop() must reset _deferredBlockRuleDisable (Interlocked.Exchange ->0) " +
            "AND _lastFullRestart = DateTime.MinValue, so a deferred kill-switch lift pending at " +
            "disconnect can't fire on the next session's first healthy tick via a stale " +
            "fallback-elapsed (C3-1: stale-state-across-reconnect leak-window reopen).");
    }

    // ── C4-1: debounce full-restart must arm the kill-switch like the crash path ──
    [Fact]
    public void DebounceRestart_ArmsKillSwitch_C4_1()
    {
        var region = ExtractMethod(LoadCore("HealthMonitor.cs"), "OnDebounceElapsed");
        Assert.True(
            region.Contains("EnableBlockRules") &&
            Regex.IsMatch(region, @"_deferredBlockRuleDisable,\s*1"),
            "OnDebounceElapsed's full-restart fallback must EnableBlockRules() before _singBox.Restart() " +
            "and arm the deferred lift (_deferredBlockRuleDisable=1) after — otherwise a config-change " +
            "TUN bounce runs with no kill-switch coverage (C4-1 leak during the ~16s warm-up).");
    }

    // ── C1-1: VpnEngine.Dispose() must tear down partial-start firewall/DNS ──
    [Fact]
    public void Dispose_TearsDownPartialStartState_C1_1()
    {
        var region = ExtractMethod(LoadCore("VpnEngine.cs"), "Dispose");
        Assert.True(
            region.Contains("_dnsHardening.Restore") && region.Contains("_firewall?.Dispose"),
            "VpnEngine.Dispose() must, when NOT IsRunning, still Restore DNS hardening + Dispose the " +
            "firewall (DeleteAllRules) — a mid-Start exception leaves block rules + HKLM hardening that " +
            "Stop() (only called when IsRunning) never cleans on the CLI/Service paths (C1-1 orphan).");
    }

    // ── helpers ──
    private static string LoadCore(string fileName)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "VPNRouter.Core", "Services", fileName);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException($"Could not locate VPNRouter.Core/Services/{fileName}");
    }

    private static string ExtractMethod(string src, string methodName)
    {
        // Require the return type immediately before the name so a comment
        // mentioning "...Stop()" (e.g. a field doc) can't be mistaken for the
        // method declaration — mirrors FirewallManagerDnsLockdownTests.
        var sig = new Regex($@"\b(void|Task|Task<[^>]+>|async)\s+{Regex.Escape(methodName)}\s*\(",
            RegexOptions.Compiled);
        var m = sig.Match(src);
        if (!m.Success) return src;
        var brace = src.IndexOf('{', m.Index + m.Length);
        if (brace < 0) return src.Substring(m.Index, Math.Min(2000, src.Length - m.Index));
        int depth = 1, i = brace + 1;
        while (i < src.Length && depth > 0) { if (src[i] == '{') depth++; else if (src[i] == '}') depth--; i++; }
        return src.Substring(brace, i - brace);
    }
}
