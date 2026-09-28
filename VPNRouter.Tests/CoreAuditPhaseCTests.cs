using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace VPNRouter.Tests;

public sealed class CoreAuditPhaseCTests
{
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
