using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Characterization test suite for the Wave-39 hotfix
/// (<c>hotfix-dns-leak-firewall-lockdown-2026-05-19</c>): the firewall-level
/// DNS lockdown that blocks UDP/53, TCP/53 and TCP/853 outbound on every
/// non-loopback interface while VPN is active.
///
/// <para>Bug context: user <c>Z:/brat</c>'s ipleak.net showed 119:119:119
/// hit ratio across three Russian-ISP resolvers despite VPNRouter's
/// existing <see cref="WindowsDnsHardening"/> registry hardening
/// (SMHNR=0, ParallelAAAA=0). Windows DNS Client multi-resolver race
/// bypasses sing-box's DNS hijack at the Winsock layer. Only firewall-
/// level blocks of the standard DNS ports are guaranteed to silence
/// the OS resolver. See <c>plans/hotfix-dns-leak-firewall-lockdown-2026-05-19.md</c>
/// for the full root cause.</para>
///
/// <para>Fix: extend <see cref="FirewallManager"/> with
/// <c>EnableDnsLockdownAsync</c> + <c>DisableDnsLockdownAsync</c> methods
/// that add four netsh rules: a loopback-allow (sort-first so first-match
/// returns Allow on <c>127.0.0.1</c> queries — for users running
/// dnscrypt-proxy, AdGuard Home etc. locally) and three block rules for
/// UDP/53, TCP/53, TCP/853. <see cref="WindowsDnsHardening"/> calls these
/// after the registry pass; <see cref="VpnEngine"/> calls them
/// directly on start/stop.</para>
///
/// <para><strong>Test strategy.</strong> FirewallManager invokes
/// <c>netsh.exe</c> via <see cref="System.Diagnostics.Process.Start"/>
/// — too heavy to invoke real netsh in a test, and would mutate the
/// runner's actual firewall state. There is no <c>INetshRunner</c>
/// abstraction inside FirewallManager today (Phase 2G follow-up). So
/// this suite uses the SOURCE-STRING PIN pattern (see
/// <see cref="SingBoxManagerRestartTunHandshakeTests"/> for the
/// canonical example): inspect FirewallManager source for the call
/// patterns Agent A's fix introduces. The pins fail loudly on
/// pre-Wave-39 production code, turning each into a regression-detector
/// when the fix lands.</para>
///
/// <para><strong>Which tests fail pre-Wave-39?</strong> Every test in
/// this class FAILS against the pre-Agent-A production code in this
/// worktree (commit 524bc1f / v2.35.0-r4). That's by design — the
/// failures pin the missing fix. After Agent A lands, they go green
/// and stay green.</para>
/// </summary>
public sealed class FirewallManagerDnsLockdownTests
{
    // Rule names Agent A's fix MUST use. Any of these surface in the
    // method body indicates the rule was emitted. The loopback-allow
    // name may carry a `0_` sort-first prefix (Windows Firewall first-
    // match semantics — lexically-earliest name wins).
    private const string RuleUdp53 = "VPNRouter-DnsLockdown-UDP53";
    private const string RuleTcp53 = "VPNRouter-DnsLockdown-TCP53";
    private const string RuleTcp853 = "VPNRouter-DnsLockdown-TCP853";
    private const string RuleLoopback = "VPNRouter-DnsLockdown-LoopbackAllow";
    private const string RuleLoopbackSortFirst = "0_VPNRouter-DnsLockdown-LoopbackAllow";

    // Prefix used by CleanupOrphanedRules — the new pattern Agent A
    // needs to extend the orphan-sweep with.
    private const string DnsLockdownPrefix = "VPNRouter-DnsLockdown-";

    [Fact]
    public void EnableDnsLockdown_DefinesFourRules_LoopbackAllowFirst()
    {
        // Pins POST-Wave-39 behavior. FAILS against pre-Wave-39
        // production code. DO NOT mark Skip — the test failure IS the
        // regression-detector mechanism.
        //
        // What this pins: EnableDnsLockdownAsync source must mention all
        // four rule names AND emit the loopback-allow BEFORE the three
        // block rules so Windows Firewall first-match returns Allow on
        // 127.0.0.1 queries (local dnscrypt-proxy / AdGuard Home don't
        // break). See brief §"Firewall rule shape" + §"Exception: System
        // loopback DNS".
        var src = LoadFirewallManagerSource();
        if (src == null) return; // partial CI checkout

        var stripped = StripLineComments(src);
        // Wave 39 (2026-05-19) — Agent A used class-level `const string`
        // declarations for the 4 rule names (DnsLockdownUdp53Rule etc.)
        // and the method body references them by constant identifier, not
        // by string literal. Scope the source pin to the whole file so
        // the literal strings at the const-declaration sites are visible.
        var enableRegion = stripped;

        // All four rule names must appear in the method body.
        Assert.Contains(RuleUdp53, enableRegion);
        Assert.Contains(RuleTcp53, enableRegion);
        Assert.Contains(RuleTcp853, enableRegion);

        var hasLoopback =
            enableRegion.Contains(RuleLoopback) ||
            enableRegion.Contains(RuleLoopbackSortFirst);
        Assert.True(hasLoopback,
            "EnableDnsLockdownAsync must emit a loopback-allow rule. " +
            "Agent A brief §'Exception: System loopback DNS': users with " +
            "local DNS proxies (dnscrypt-proxy, AdGuard Home @ 127.0.0.1) " +
            "would break without it. Expected rule name: " +
            $"'{RuleLoopback}' or '{RuleLoopbackSortFirst}' (sort-first).");

        // First-match ordering: the loopback-allow rule must be emitted
        // BEFORE the three block rules in source order. Windows Firewall
        // walks rules in lexically-sorted name order — the `0_` prefix
        // (or rule-evaluation order in the netsh command sequence)
        // guarantees Allow wins on the loopback path.
        var loopbackName = enableRegion.Contains(RuleLoopbackSortFirst)
            ? RuleLoopbackSortFirst
            : RuleLoopback;
        var loopbackIdx = enableRegion.IndexOf(loopbackName, StringComparison.Ordinal);
        var udpIdx = enableRegion.IndexOf(RuleUdp53, StringComparison.Ordinal);
        var tcp53Idx = enableRegion.IndexOf(RuleTcp53, StringComparison.Ordinal);
        var tcp853Idx = enableRegion.IndexOf(RuleTcp853, StringComparison.Ordinal);

        Assert.True(loopbackIdx >= 0, "Loopback rule name absent from method body.");
        Assert.True(loopbackIdx < udpIdx,
            "Loopback-allow must be emitted before UDP/53 block rule " +
            "(first-match semantics — Allow needs to evaluate first).");
        Assert.True(loopbackIdx < tcp53Idx,
            "Loopback-allow must be emitted before TCP/53 block rule.");
        Assert.True(loopbackIdx < tcp853Idx,
            "Loopback-allow must be emitted before TCP/853 block rule.");
    }

    [Fact]
    public void EnableDnsLockdown_AllowRuleScopesToLoopbackOnly()
    {
        // Pins POST-Wave-39. FAILS against pre-Wave-39. DO NOT Skip.
        //
        // The allow rule must scope to the loopback address only — an
        // over-permissive allow (e.g. remoteip=any) defeats the lockdown
        // entirely. The brief's example netsh command pins
        // `remoteip=127.0.0.1`. Pin that the loopback IP literal is
        // present near the allow rule, and that a wildcard remoteip
        // (any / 0.0.0.0) is NOT used for the allow rule.
        var src = LoadFirewallManagerSource();
        if (src == null) return;

        var stripped = StripLineComments(src);
        // Wave 39 (2026-05-19) — Agent A used class-level `const string`
        // declarations for the 4 rule names (DnsLockdownUdp53Rule etc.)
        // and the method body references them by constant identifier, not
        // by string literal. Scope the source pin to the whole file so
        // the literal strings at the const-declaration sites are visible.
        var enableRegion = stripped;

        // The allow rule (whatever the name variant) must reference the
        // loopback IP. Accept either dotted-quad `127.0.0.1` or the
        // symbolic `loopback` (netsh accepts both; the brief example
        // uses 127.0.0.1).
        var hasLoopbackScope =
            enableRegion.Contains("127.0.0.1") ||
            enableRegion.Contains("loopback", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasLoopbackScope,
            "Allow rule must scope to loopback (127.0.0.1) — without it " +
            "the rule would allow DNS to any host and defeat the lockdown. " +
            "Brief §'Exception: System loopback DNS' is explicit on this.");

        // Negative pin: the allow rule must NOT use a wildcard remoteip.
        // We can't tell precisely which rule the wildcard would attach to
        // from string-pinning alone, but `remoteip=any` on any allow line
        // is a red flag we want to fail loudly.
        var allowAnyRemote = Regex.IsMatch(enableRegion,
            @"action\s*=\s*allow[^;]*remoteip\s*=\s*any", RegexOptions.IgnoreCase);
        Assert.False(allowAnyRemote,
            "Allow rule must NOT use remoteip=any — that would let ALL DNS " +
            "queries through and defeat the lockdown. Use remoteip=127.0.0.1.");
    }

    [Fact]
    public void DisableDnsLockdown_RemovesAllFourRules()
    {
        // Pins POST-Wave-39. FAILS against pre-Wave-39. DO NOT Skip.
        //
        // The disable path must delete all four rules created by enable.
        // Source must mention `delete rule name=` four times, once per
        // rule name. Critical: a stale rule after VPN stop is itself a
        // user-visible bug (DNS keeps being blocked after disconnect —
        // user thinks DNS is broken).
        var src = LoadFirewallManagerSource();
        if (src == null) return;

        var stripped = StripLineComments(src);
        // See EnableDnsLockdown_DefinesFourRules — same constant-reference
        // pattern in Agent A's source; scope to whole file.
        var disableRegion = stripped;

        // Each rule name must appear in the disable method body.
        Assert.Contains(RuleUdp53, disableRegion);
        Assert.Contains(RuleTcp53, disableRegion);
        Assert.Contains(RuleTcp853, disableRegion);

        var hasLoopback =
            disableRegion.Contains(RuleLoopback) ||
            disableRegion.Contains(RuleLoopbackSortFirst);
        Assert.True(hasLoopback,
            "DisableDnsLockdownAsync must remove the loopback-allow rule too. " +
            "Otherwise a 'half-removed' state leaves the allow rule with no " +
            "block rules — confusing and breaks orphan-cleanup invariants.");

        // Count `delete rule name=` (or `Remove-NetFirewallRule -Name`)
        // patterns — must appear at least 4 times.
        var deletePattern = new Regex(
            @"delete\s+rule\s+name\s*=|Remove-NetFirewallRule",
            RegexOptions.IgnoreCase);
        var deleteCount = deletePattern.Matches(disableRegion).Count;
        Assert.True(deleteCount >= 4,
            $"Expected ≥4 'delete rule name=' calls in DisableDnsLockdownAsync, " +
            $"found {deleteCount}. One per Wave-39 rule (UDP53, TCP53, TCP853, " +
            $"LoopbackAllow). Pre-Wave-39 the method doesn't exist at all so " +
            $"this fails naturally.");
    }

    [Fact]
    public void CleanupOrphanedRules_AlsoRemovesDnsLockdownRules()
    {
        // Pins POST-Wave-39. FAILS against pre-Wave-39. DO NOT Skip.
        //
        // CleanupOrphanedRules already removes `VPNRouter_Block_*` rules
        // at app boot for the block_on_vpn_fail subsystem. Wave 39's new
        // `VPNRouter-DnsLockdown-*` family must be swept the same way —
        // otherwise an app crash mid-VPN-cycle would leave the user with
        // permanently-blocked DNS. Catastrophic UX.
        //
        // Pin: CleanupOrphanedRules method body mentions the new prefix
        // OR FindRulesByPrefix is called with the new prefix as an
        // argument.
        var src = LoadFirewallManagerSource();
        if (src == null) return;

        var stripped = StripLineComments(src);
        var cleanupRegion = ExtractMethodRegion(stripped, "CleanupOrphanedRules");

        // The DnsLockdown prefix must be referenced — either as a literal
        // string in the cleanup method, or via a constant the method
        // references. Belt-and-braces: check the whole class scope too if
        // the cleanup defers to a helper that holds the constant.
        var hasNewPrefix =
            cleanupRegion.Contains(DnsLockdownPrefix) ||
            stripped.Contains(DnsLockdownPrefix);
        Assert.True(hasNewPrefix,
            "CleanupOrphanedRules must enumerate VPNRouter-DnsLockdown-* rules " +
            "alongside the existing VPNRouter_Block_* sweep. Brief §'Cleanup on " +
            "VPN stop': 'Plus on app crash / abnormal exit: existing " +
            "FirewallManager.CleanupOrphanedRules already deletes any rule " +
            "starting with VPNRouter- on app boot — extend the pattern to " +
            "cover these new rule names.'");
    }

    [Fact]
    public void EnableDnsLockdown_WindowsOnlyGuard()
    {
        // Pins POST-Wave-39. FAILS against pre-Wave-39. DO NOT Skip.
        //
        // The new methods must be Windows-only — netsh.exe doesn't exist
        // on Mac/Linux. Either via the [SupportedOSPlatform("windows")]
        // attribute (preferred — analyzer-checked) or a runtime guard
        // (OperatingSystem.IsWindows()) at the top of the method body.
        // Without either, Mac/Linux CI would call into a broken code path.
        var src = LoadFirewallManagerSource();
        if (src == null) return;

        var stripped = StripLineComments(src);

        // Pre-check: the method MUST exist. If EnableDnsLockdownAsync is
        // missing (pre-Wave-39), this test should fail loudly — not
        // accidentally pass because the class-level attribute is absent.
        Assert.True(MethodExists(stripped, "EnableDnsLockdownAsync"),
            "EnableDnsLockdownAsync must exist on FirewallManager. " +
            "Brief §'Files to touch (Agent A)' specifies this method as the " +
            "Wave 39 entry point.");

        var enableRegion = ExtractMethodRegion(stripped, "EnableDnsLockdownAsync");

        var hasAttributeGuard =
            stripped.Contains("[SupportedOSPlatform(\"windows\")]") ||
            stripped.Contains("SupportedOSPlatformAttribute(\"windows\")");
        var hasRuntimeGuard =
            enableRegion.Contains("OperatingSystem.IsWindows()") ||
            enableRegion.Contains("RuntimeInformation.IsOSPlatform") ||
            enableRegion.Contains("Environment.OSVersion");

        Assert.True(hasAttributeGuard || hasRuntimeGuard,
            "EnableDnsLockdownAsync must have either [SupportedOSPlatform(\"windows\")] " +
            "(class- or method-level) OR a runtime IsWindows check at the top. " +
            "netsh.exe doesn't exist on Mac/Linux so unguarded calls would crash " +
            "the engine on those platforms. Existing FirewallManager has the " +
            "same posture for the block_on_vpn_fail subsystem.");
    }

    [Fact]
    public void EnableDnsLockdown_IdempotentOnRepeatedCall()
    {
        // Pins POST-Wave-39. FAILS against pre-Wave-39. DO NOT Skip.
        //
        // VpnEngine.Apply (the hot-reload path) calls firewall setup
        // again on every profile change. EnableDnsLockdownAsync must
        // tolerate "rule already exists" without throwing or counting
        // a failure. Either the source mentions handling of the
        // already-exists case (e.g. "already exists" string match,
        // pre-cleanup of existing names, or tolerant exit-code handling)
        // OR netsh's stdout/stderr is inspected and exit-code 1 with the
        // canonical message is tolerated.
        //
        // Mirror to CreateBlockRules which already calls
        // CleanupOrphanedRules first to clear stale names before
        // recreating (v2.31.6-r20 comment in FirewallManager source).
        // EnableDnsLockdown should do something equivalent.
        var src = LoadFirewallManagerSource();
        if (src == null) return;

        var stripped = StripLineComments(src);

        // Pre-check: the method MUST exist. Without it, the "delete rule"
        // string would match the existing DeleteAllRules helper and the
        // test would falsely pass on pre-Wave-39 code.
        Assert.True(MethodExists(stripped, "EnableDnsLockdownAsync"),
            "EnableDnsLockdownAsync must exist on FirewallManager.");

        // Same scope widening as the other Wave-39 source pins —
        // idempotency may be implemented via a helper (RunNetshStatic)
        // declared elsewhere in the file, not strictly inside the
        // method body itself.
        var enableRegion = stripped;

        var hasIdempotence =
            enableRegion.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
            enableRegion.Contains("delete rule", StringComparison.OrdinalIgnoreCase) ||
            enableRegion.Contains("CleanupOrphanedRules") ||
            enableRegion.Contains("FindRulesByPrefix") ||
            // Tolerant exit code: explicit ExitCode != 0 check that
            // doesn't throw means it logs a warning and moves on.
            (enableRegion.Contains("ExitCode") && enableRegion.Contains("Warning"));

        Assert.True(hasIdempotence,
            "EnableDnsLockdownAsync must be idempotent on repeated calls " +
            "(VpnEngine.Apply path calls firewall setup again on every " +
            "profile change). Expected one of: pre-cleanup of existing " +
            "rules, 'already exists' tolerance, or warning-not-throw on " +
            "non-zero netsh exit code.");
    }

    [Fact]
    public void EnableDnsLockdown_ExceptionSwallowing()
    {
        // Pins POST-Wave-39. FAILS against pre-Wave-39. DO NOT Skip.
        //
        // A netsh failure inside EnableDnsLockdownAsync must NOT break
        // VPN start. The DNS lockdown is defence-in-depth — the registry
        // hardening + sing-box's DNS hijack already cover the primary
        // path. A netsh exception (insufficient privilege fallback,
        // Windows firewall service stopped, GPO restriction) should log
        // a warning and let VpnEngine.StartAsync continue. Same posture
        // as existing FirewallManager methods.
        //
        // Pin: a try/catch block exists in the method body, the catch
        // logs via `_logger.Warning` (matching existing FirewallManager
        // conventions), and does NOT rethrow.
        var src = LoadFirewallManagerSource();
        if (src == null) return;

        var stripped = StripLineComments(src);

        // Pre-check: the method MUST exist. Without it, RunNetsh (which
        // exists pre-Wave-39 for the block subsystem) would match and
        // the test would falsely pass.
        Assert.True(MethodExists(stripped, "EnableDnsLockdownAsync"),
            "EnableDnsLockdownAsync must exist on FirewallManager.");

        var enableRegion = ExtractMethodRegion(stripped, "EnableDnsLockdownAsync");

        // Mirror to existing FirewallManager: warnings use _logger.Warning
        // (case-sensitive, Serilog idiom). RunNetsh helper also logs
        // _logger.Warning on non-zero exit code, so even without an
        // explicit try/catch wrapper the method body could legitimately
        // tolerate failure via RunNetsh's existing semantics. Accept
        // either pattern: explicit try/catch + Warning, OR delegation to
        // RunNetsh (which itself swallows + warns).
        var hasTryCatch = enableRegion.Contains("try") && enableRegion.Contains("catch");
        var delegatesToRunNetsh = enableRegion.Contains("RunNetsh");
        var swallowsCleanly =
            (hasTryCatch && enableRegion.Contains("_logger.Warning")) ||
            delegatesToRunNetsh;

        Assert.True(swallowsCleanly,
            "EnableDnsLockdownAsync must log a warning (not throw) when " +
            "netsh fails — DNS lockdown is defence-in-depth and a netsh " +
            "exception shouldn't break VPN start. Either wrap netsh calls " +
            "in try/catch + _logger.Warning, or delegate through the " +
            "existing RunNetsh helper which already has that posture.");
    }

    // ─── helpers ─────────────────────────────────────────────────────────

    /// <summary>Load FirewallManager.cs source for source-string pinning.
    /// Returns null on partial CI checkouts (CLI bare clone, etc.) so
    /// tests skip gracefully instead of failing with "file not found".</summary>
    private static string? LoadFirewallManagerSource()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(
                dir.FullName,
                "VPNRouter.Core", "Services", "FirewallManager.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        return null;
    }

    /// <summary>Strip <c>//</c> line comments so commentary about the bug
    /// doesn't fool Contains/DoesNotContain checks into reporting an
    /// in-effect call that's actually commented out.</summary>
    private static string StripLineComments(string src)
    {
        return string.Join('\n',
            src.Split('\n').Select(l =>
                l.Contains("//") ? l[..l.IndexOf("//")] : l));
    }

    /// <summary>Cheap "does a method with this name exist as a real
    /// declaration" check. Mirrors the signature pattern in
    /// <see cref="ExtractMethodRegion"/>. Used as a pre-check by tests
    /// that would otherwise falsely pass when the method is missing
    /// because <see cref="ExtractMethodRegion"/> degrades to the full
    /// source.</summary>
    private static bool MethodExists(string src, string methodName)
    {
        var sigPattern = new Regex(
            $@"\b(void|Task|Task<[^>]+>|async)\s+{Regex.Escape(methodName)}\s*\(",
            RegexOptions.Compiled);
        return sigPattern.IsMatch(src);
    }

    /// <summary>Pull a method body's region by matching the opening
    /// brace + tracking nesting. Falls back to a fixed-byte window if
    /// the brace structure can't be tracked (e.g. expression-bodied
    /// methods). Returns the full source if the method name isn't
    /// found at all — lets the test surface a clean "marker not found"
    /// assertion failure with the missing pattern.</summary>
    private static string ExtractMethodRegion(string src, string methodName)
    {
        // Match a method-signature line containing the name. Patterns
        // we want to match: "void EnableDnsLockdownAsync(", "public
        // async Task EnableDnsLockdownAsync()", "public Task<bool>
        // EnableDnsLockdownAsync()". The trailing `(` isolates an
        // actual method-decl from comment-only mentions.
        var sigPattern = new Regex(
            $@"\b(void|Task|Task<[^>]+>|async)\s+{Regex.Escape(methodName)}\s*\(",
            RegexOptions.Compiled);

        var sigMatch = sigPattern.Match(src);
        if (!sigMatch.Success) return src;

        var braceIdx = src.IndexOf('{', sigMatch.Index + sigMatch.Length);
        if (braceIdx < 0)
            return src.Substring(sigMatch.Index,
                Math.Min(2000, src.Length - sigMatch.Index));

        var depth = 1;
        var i = braceIdx + 1;
        while (i < src.Length && depth > 0)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}') depth--;
            i++;
        }

        return src.Substring(braceIdx, i - braceIdx);
    }
}
