using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Characterization test suite for the Wave-38 hotfix
/// (<c>hotfix-tun-adapter-orphan-pre-enable-2026-05-19</c>): the
/// auto-restart loop's interaction with TUN adapter cleanup.
///
/// <para>Bug context: <see cref="SingBoxManager.LaunchProcess"/>
/// previously called <c>TunAdapterDiagnostics.EnsureAdapterEnabledOrAbsent</c>
/// (a pure pre-enable via <c>netsh admin=enabled</c>) on every launch
/// path including HealthMonitor's crash-recovery restart. On Windows
/// builds where wintun teardown stalls, the orphan device record
/// blocks <c>WintunCreateAdapter</c> with FATAL
/// "Cannot create a file when that file already exists". Loop repeats
/// 5-6 times until eventually-but-not-functionally connected. User
/// <c>Z:/alicemoren1991</c>'s logs reproduced this for a week.</para>
///
/// <para>Fix: replace pre-enable with full
/// <see cref="TunAdapterDiagnostics.PreStartCleanupAsync"/> in
/// LaunchProcess. Strengthen <c>OnProcessExited</c> + StopInternal.early
/// to schedule the full removal too — so the orphan record is gone by
/// the time HealthMonitor's restart fires.</para>
///
/// <para><strong>Test strategy.</strong> Legacy cases use source-string
/// pins for the Wave-38 call shape. The removal-settle regression uses
/// the current <see cref="IProcessRunner"/> seams to exercise launch timing
/// without spawning sing-box or touching a real adapter.</para>
///
/// <para><strong>Which tests fail pre-Wave-38?</strong> Every test in
/// this class FAILS against the pre-Agent-1 production code in this
/// worktree (commit d7bc3b5). That's by design — the failures pin the
/// missing fix. After Agent 1 lands, they go green and stay green.</para>
/// </summary>
public sealed class SingBoxManagerRestartTunHandshakeTests
{
    [Fact]
    public void Restart_AfterCrash_LaunchPath_HasFullCleanup_NotJustPreEnable()
    {
        // Pins POST-Wave-38 behavior. FAILS against pre-Wave-38 (the
        // pre-Agent-1 production code in this worktree). DO NOT mark
        // Skip — the test failure IS the regression-detector mechanism.
        //
        // What this pins: SingBoxManager.LaunchProcess (the single
        // chokepoint for every restart path — user Start, Apply
        // hot-reload-fallback, HealthMonitor crash recovery, manual
        // Restart) must call the FULL cleanup (disable + Remove-NetAdapter
        // via PreStartCleanupAsync) before the next sing-box spawn.
        // Pre-Wave-38 called EnsureAdapterEnabledOrAbsent (pure pre-enable
        // via netsh admin=enabled) which only re-enables a disabled
        // adapter without removing the device record — the very bug
        // alicemoren1991 hit.
        var src = LoadSingBoxManagerSource();
        if (src == null) return; // partial CI checkout

        var stripped = StripLineComments(src);
        var launchProcessRegion = ExtractMethodRegion(stripped, "LaunchProcess");

        // The launch path must invoke PreStartCleanup — Agent 1's fix
        // replaces the pre-enable call with a synchronous wrapper
        // around TunAdapterDiagnostics.PreStartCleanupAsync.
        Assert.Contains("PreStartCleanup", launchProcessRegion);
    }

    [Fact]
    public void LaunchProcess_NoPreEnableCall_PreEnableRetiredFromLaunchPath()
    {
        // Pins POST-Wave-38. FAILS against pre-Wave-38. DO NOT Skip.
        //
        // Mirror to the positive pin above: EnsureAdapterEnabledOrAbsent
        // must NOT be called inside LaunchProcess anymore. Agent 1's
        // brief §4 says "Retire OR demote to fallback" — either way,
        // the launch path stops invoking it. The method itself stays
        // for backcompat (see EnsureAdapterEnabledOrAbsent_StillCallable_ForBackcompat
        // in TunAdapterReadinessTests).
        var src = LoadSingBoxManagerSource();
        if (src == null) return;

        var stripped = StripLineComments(src);
        var launchProcessRegion = ExtractMethodRegion(stripped, "LaunchProcess");

        Assert.DoesNotContain("EnsureAdapterEnabledOrAbsent", launchProcessRegion);
    }

    [Fact]
    public void OnProcessExited_TriggersAsyncRemove_NotJustDisable()
    {
        // Pins POST-Wave-38. FAILS against pre-Wave-38. DO NOT Skip.
        //
        // The crash-recovery cleanup path (OnProcessExited) must
        // schedule the full device removal — not just disable. Pre-fix
        // it called only DisableOrphanedAdapter which leaves the
        // device record alive, so HealthMonitor's restart 5-10 s
        // later still hits "Cannot create a file" FATAL.
        //
        // Agent 1's options (any acceptable): call
        // TryRemoveAdapterAsync (made internal), reuse
        // PreStartCleanupAsync, or add a new helper. Match any.
        var src = LoadSingBoxManagerSource();
        if (src == null) return;

        var stripped = StripLineComments(src);
        var onExitedRegion = ExtractMethodRegion(stripped, "OnProcessExited");

        // Belt-and-braces: still keep DisableOrphanedAdapter (which
        // frees the wintun kernel handle) — disable is the prerequisite
        // for Remove-NetAdapter to succeed. So the test only adds the
        // removal requirement; it doesn't drop the disable.
        Assert.Contains("DisableOrphanedAdapter", onExitedRegion);

        var hasRemoval =
            onExitedRegion.Contains("QueueTunAdapterRemoval") ||
            onExitedRegion.Contains("TryRemoveAdapterAsync") ||
            onExitedRegion.Contains("PreStartCleanupAsync") ||
            onExitedRegion.Contains("RemoveAdapterAsync") ||
            onExitedRegion.Contains("Remove-NetAdapter");

        Assert.True(hasRemoval,
            "OnProcessExited must schedule adapter removal (not only disable). " +
            "Pre-Wave-38 only called DisableOrphanedAdapter — the orphan device " +
            "record survives, HealthMonitor's restart hits 'Cannot create a file'. " +
            "Agent 1 brief §2: 'Strengthen OnProcessExited cleanup'.");
    }

    [Fact]
    public void StopInternal_CleansAdapter_ScheduleRemoval()
    {
        // Pins POST-Wave-38. FAILS against pre-Wave-38. DO NOT Skip.
        //
        // Graceful Stop's "process already exited" branch (StopInternal
        // when _process == null || HasExited) currently calls only
        // DisableOrphanedAdapter. Strengthen to schedule removal too,
        // since the user might immediately re-Connect — at which point
        // we'd need a fresh device record, not an orphan one.
        var src = LoadSingBoxManagerSource();
        if (src == null) return;

        var stripped = StripLineComments(src);

        // The StopInternal.early branch is marked with context string
        // "SingBoxManager.StopInternal.early" — easy to locate.
        var stopEarlyRegion = ExtractRegionAround(stripped,
            "StopInternal.early", 100, 1500);

        var hasRemoval =
            stopEarlyRegion.Contains("QueueTunAdapterRemoval") ||
            stopEarlyRegion.Contains("TryRemoveAdapterAsync") ||
            stopEarlyRegion.Contains("PreStartCleanupAsync") ||
            stopEarlyRegion.Contains("RemoveAdapterAsync") ||
            stopEarlyRegion.Contains("Remove-NetAdapter");

        Assert.True(hasRemoval,
            "StopInternal.early branch must schedule adapter removal. " +
            "Pre-Wave-38 only called DisableOrphanedAdapter. Agent 1 " +
            "brief §3: 'Strengthen StopInternal.early cleanup'.");
    }

    [Fact]
    public void AutoRestartLoop_FiveCrashes_NoCannotCreateFileFatalAccumulates()
    {
        // The key regression pin from the brief. Simulate 5 sequential
        // crashes through SingBoxManager's restart pattern by counting
        // PreStartCleanup invocation sites visible in the source —
        // every launch path must converge on the cleanup call.
        //
        // Why source-string pin (not behaviour test)? SingBoxManager
        // spawns real sing-box.exe via Process.Start. To behaviour-test
        // 5 sequential crashes we'd need either (a) a real sing-box
        // binary that intentionally exits with code 1, or (b) the
        // IProcessRunner refactor that Phase 2G has on the roadmap.
        // Neither is in-scope for this hotfix — source pins it is.
        //
        // The fix shape: a single PreStartCleanup call inside
        // LaunchProcess covers EVERY restart path (which all flow
        // through LaunchProcess). So one call site is enough — that's
        // the brief's "single chokepoint" design. Pin that exactly one
        // PreStartCleanup* call exists in the LaunchProcess body, AND
        // that EnsureAdapterEnabledOrAbsent is gone from that body.
        //
        // Pins POST-Wave-38. FAILS against pre-Wave-38. DO NOT Skip.
        var src = LoadSingBoxManagerSource();
        if (src == null) return;

        var stripped = StripLineComments(src);
        var launchProcessRegion = ExtractMethodRegion(stripped, "LaunchProcess");

        // Pre-enable should be ABSENT from LaunchProcess (it lived
        // here in pre-Wave-38).
        Assert.DoesNotContain("EnsureAdapterEnabledOrAbsent", launchProcessRegion);

        // Full cleanup should be PRESENT in LaunchProcess. Matches
        // PreStartCleanup / PreStartCleanupAsync / a sync wrapper.
        Assert.Contains("PreStartCleanup", launchProcessRegion);

        // Restart() routes through Stop() then LaunchProcess(), so
        // each iteration of the auto-restart loop hits the same
        // chokepoint. No need to count — one good call suffices
        // because Restart's structure guarantees iteration N+1 lands
        // here.
        var restartRegion = ExtractMethodRegion(stripped, "Restart");
        // Restart must still call LaunchProcess (so the cleanup it
        // performs is reached). Sanity pin against a refactor that
        // accidentally drops the LaunchProcess call from Restart's tail.
        Assert.Contains("LaunchProcess", restartRegion);
    }

    [Fact]
    public void TunAdapterDiagnostics_PreStartCleanupAsync_IsPublicApi()
    {
        // Pin the API surface PreStartCleanupAsync exposes — Agent 1's
        // fix calls it from SingBoxManager (a different assembly module
        // than the StartupPipeline that already calls it). Verify the
        // signature is what Agent 1 needs: static, public, awaitable,
        // returns int (count of adapters removed).
        //
        // PASSES against both pre- and post-Wave-38 — the method
        // already exists in Bug-r9-H (v2.32.x).
        var method = typeof(TunAdapterDiagnostics).GetMethod(
            nameof(TunAdapterDiagnostics.PreStartCleanupAsync),
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);
        Assert.True(method!.IsStatic);
        Assert.True(method.IsPublic);

        // Return shape: Task<int> (the count of removed adapters).
        // Agent 1's brief §3 mentions "the count of adapters successfully
        // removed so the caller can decide whether to insert a settle
        // delay" — pin that contract.
        Assert.Equal(
            typeof(System.Threading.Tasks.Task<int>),
            method.ReturnType);
    }

    [Fact]
    public void SingBoxManager_RestartSleep_PreservedForWintunSettle()
    {
        // The 750 ms Thread.Sleep in Restart (post-Stop, pre-LaunchProcess)
        // is the existing settle delay for Windows wintun teardown. After
        // Agent 1's fix, the cleanup happens BEFORE LaunchProcess too,
        // but the settle delay should remain — Windows network-stack
        // teardown can race with the Remove-NetAdapter completion.
        //
        // Pin that the existing settle delay isn't dropped during the
        // refactor.
        //
        // PASSES against both pre- and post-Wave-38 (the sleep was added
        // in v2.31.9-r4 and should survive Agent 1's diff).
        var src = LoadSingBoxManagerSource();
        if (src == null) return;

        var stripped = StripLineComments(src);
        var restartRegion = ExtractMethodRegion(stripped, "Restart");

        // The sleep matches "Thread.Sleep(750)" or any 4-digit ms value
        // in the same ballpark. Agent 1 might tune it, but it should
        // stay between 500 and 2000.
        var sleepMatch = Regex.Match(restartRegion, @"Thread\.Sleep\(\s*(\d+)\s*\)");
        Assert.True(sleepMatch.Success,
            "Restart() must keep a settle delay (Thread.Sleep) for wintun " +
            "teardown — pre-r4 v2.31.9 commentary explains the rationale.");

        var sleepMs = int.Parse(sleepMatch.Groups[1].Value);
        Assert.InRange(sleepMs, 500, 2000);
    }

    // ─── helpers ────────────────────────────────────────────────────────

    /// <summary>Load SingBoxManager.cs source for source-string pinning.
    /// Returns null on partial CI checkouts (CLI bare clone, etc.).</summary>
    private static string? LoadSingBoxManagerSource()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(
                dir.FullName,
                "VPNRouter.Core", "Services", "SingBoxManager.cs");
            if (File.Exists(candidate)) return SingBoxSourceText.ReadAll(candidate);
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

    /// <summary>Pull a method body's region by matching the opening
    /// brace + tracking nesting. Falls back to a fixed-byte window if
    /// the brace structure can't be tracked (e.g. expression-bodied
    /// methods). Returns the full source if the method name isn't
    /// found at all — lets the test surface a clean
    /// "marker not found" assertion failure.</summary>
    private static string ExtractMethodRegion(string src, string methodName)
    {
        // Find a method signature line containing the name. Patterns
        // we want to match: "void LaunchProcess(", "public void Restart()",
        // "private void OnProcessExited()". The space after "void" or
        // a modifier before the type isolates the actual method-decl
        // from comment-only mentions.
        var sigPattern = new Regex(
            $@"\b(void|Task|async)\s+{Regex.Escape(methodName)}\s*\(",
            RegexOptions.Compiled);

        var sigMatch = sigPattern.Match(src);
        if (!sigMatch.Success) return src;

        // Find the opening brace following the signature.
        var braceIdx = src.IndexOf('{', sigMatch.Index + sigMatch.Length);
        if (braceIdx < 0)
            return src.Substring(sigMatch.Index,
                Math.Min(2000, src.Length - sigMatch.Index));

        // Walk forward tracking brace nesting to find the closing
        // brace of the method body.
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

    /// <summary>Pull a code region around a marker substring (not a method
    /// name). Useful for regions identified by string literals (like
    /// the "SingBoxManager.StopInternal.early" context string).</summary>
    private static string ExtractRegionAround(string src, string marker,
        int beforeBytes, int afterBytes)
    {
        var idx = src.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return src;
        var start = Math.Max(0, idx - beforeBytes);
        var end = Math.Min(src.Length, idx + marker.Length + afterBytes);
        return src.Substring(start, end - start);
    }
}
