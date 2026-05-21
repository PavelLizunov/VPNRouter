// Phase 3+ (2026-05-21) — source-string pin for the
// EnableRaisingEvents=false-before-Kill pattern that moved from
// SingBoxManager.StopInternal to ProcessHandle.Dispose during the
// IProcessRunner adoption sweep.
//
// Why this lives in its own file: the invariant is a property of the
// IProcessRunner seam implementation, not of any one consumer
// (SingBoxManager, TgProxyManager, ZapretManager, VlessDeepVerifier).
// Pre-Phase-3+ the pattern existed only in SingBoxManager.cs and
// SingBoxManagerStateMachineTests pinned it there. Phase 3+ centralised
// the pattern in ProcessHandle.Dispose so every long-lived spawn
// inherits it transitively. The pin tracks the centralised
// implementation now.
//
// Brief: plans/phase3-iprocessrunner-singboxmanager-2026-05-21.md

#nullable enable

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Source-string pin for the EnableRaisingEvents=false-before-Kill
/// ordering inside <c>ProcessHandle.Dispose</c> (the concrete
/// <c>IProcessHandle</c> impl in <c>ProcessRunner.cs</c>).
///
/// <para>The invariant: when an <see cref="System.IDisposable.Dispose"/>
/// call lands on a still-running ProcessHandle, the underlying
/// <c>Process.EnableRaisingEvents = false</c> assignment MUST run
/// BEFORE the <c>Process.Kill(entireProcessTree: true)</c> call.
/// Without this ordering, Kill triggers the Exited callback on a
/// threadpool thread, which (for SingBoxManager) fires the Crashed
/// event → HealthMonitor's auto-restart loop kicks in for an
/// INTENTIONAL stop. The race-free way to suppress that callback is
/// to flip EnableRaisingEvents to false BEFORE the Kill — Process
/// delivers Exited only when the flag is true at the moment the event
/// would fire.</para>
///
/// <para>Pre-Phase-3+ the pattern lived in
/// <c>SingBoxManager.StopInternal</c> and was pinned by
/// <c>SingBoxManagerStateMachineTests.Stop_DisablesEventsBeforeKill_SourcePin</c>.
/// After the IProcessRunner migration, the pattern is centralised in
/// <c>ProcessHandle.Dispose</c> so EVERY long-lived spawn (SingBox,
/// TgProxy, Zapret, VlessDeepVerifier, future targets) inherits it
/// transitively. The pin moved here in lockstep — cleaner separation
/// of concerns.</para>
/// </summary>
public sealed class ProcessHandleDisposeOrderingTests
{
    [Fact]
    public void Dispose_DisablesEventsBeforeKill_SourcePin()
    {
        // THE key IProcessRunner seam invariant (formerly the
        // SingBoxManager intentional-stop pattern):
        //   _process.EnableRaisingEvents = false;       // BEFORE Kill
        //   Kill(entireProcessTree: true);              // implicit no-spurious-Exited
        //   _process.Dispose();
        //
        // Pin source ordering. Can't behaviour-test the ordering directly
        // because Process.EnableRaisingEvents has no observable side
        // effect from outside System.Diagnostics — the source pin is the
        // next best thing, and it covers every long-lived spawn consumer
        // through the shared seam.
        var src = LoadProcessRunnerSource();
        Assert.SkipUnless(src != null, "ProcessRunner.cs source not reachable from test cwd — source-pin skipped");

        // Strip // line comments so the long doc block ABOVE Dispose
        // (which describes the pattern) can't muddy the match.
        var stripped = StripLineComments(src!);

        // Collapse runs of whitespace to single spaces — robust against
        // CRLF vs LF + indentation drift across branches/refactors.
        var oneline = System.Text.RegularExpressions.Regex.Replace(
            stripped, @"\s+", " ");

        // Find the Dispose() method body. The class is sealed internal
        // (ProcessHandle), and its Dispose is the LAST public method on
        // the class. Find "public void Dispose" and walk forward.
        var disposeIdx = oneline.IndexOf("public void Dispose()", StringComparison.Ordinal);
        Assert.True(disposeIdx >= 0, "ProcessRunner.cs must contain a 'public void Dispose()' method on ProcessHandle");

        // Look at the next ~600 characters (enough for the small Dispose
        // body without picking up unrelated downstream code).
        var disposeRegion = oneline.Substring(
            disposeIdx,
            Math.Min(800, oneline.Length - disposeIdx));

        // Pin: EnableRaisingEvents = false must appear in the Dispose
        // region, BEFORE the Kill call.
        var enableIdx = disposeRegion.IndexOf(
            "_process.EnableRaisingEvents = false;",
            StringComparison.Ordinal);
        Assert.True(enableIdx > 0,
            "ProcessHandle.Dispose must set _process.EnableRaisingEvents = false " +
            "BEFORE killing the process (intentional-stop pattern that every " +
            "long-lived spawn consumer relies on transitively).");

        // The Kill call inside Dispose goes via the local Kill() method,
        // which calls `_process.Kill(entireProcessTree: ...)` — match
        // either the direct or indirect call shape.
        var killIdx = disposeRegion.IndexOf("Kill(", StringComparison.Ordinal);
        Assert.True(killIdx > 0,
            "ProcessHandle.Dispose must invoke Kill (directly or via the Kill helper).");

        Assert.True(enableIdx < killIdx,
            "Intentional-stop ordering violated: " +
            "_process.EnableRaisingEvents = false MUST come BEFORE Kill " +
            $"inside ProcessHandle.Dispose. Got EnableRaisingEvents at " +
            $"{enableIdx}, Kill at {killIdx}. This is the centralised " +
            "SingBoxManager intentional-stop invariant — Kill() must NOT " +
            "trigger the Exited callback for an intentional Dispose.");
    }

    [Fact]
    public void ProcessHandle_Kill_BeforeDispose_FiresKill_NotEnableRaisingEvents()
    {
        // Belt-and-braces pin: the STANDALONE Kill() method must NOT
        // touch EnableRaisingEvents. Only Dispose flips that flag. This
        // protects against a refactor that "helpfully" moves the
        // EnableRaisingEvents=false into Kill — which would be wrong,
        // because callers that Kill+WaitForExit (without Dispose) want
        // the Exited callback to fire (e.g. an external Kill in a
        // post-mortem path).
        var src = LoadProcessRunnerSource();
        Assert.SkipUnless(src != null, "ProcessRunner.cs source not reachable from test cwd — source-pin skipped");

        var stripped = StripLineComments(src!);
        var oneline = System.Text.RegularExpressions.Regex.Replace(
            stripped, @"\s+", " ");

        // Locate the standalone public void Kill method on ProcessHandle.
        var killIdx = oneline.IndexOf(
            "public void Kill(bool entireProcessTree",
            StringComparison.Ordinal);
        Assert.True(killIdx >= 0,
            "ProcessRunner.cs must contain ProcessHandle.Kill(bool entireProcessTree = ...)");

        // Find the body end — next "public " on the class (Dispose or
        // similar). Bound the search to the next 600 chars max.
        var nextPublic = oneline.IndexOf("public ", killIdx + 1, StringComparison.Ordinal);
        var endIdx = nextPublic < 0 ? Math.Min(oneline.Length, killIdx + 600)
                                    : Math.Min(nextPublic, killIdx + 600);
        var killRegion = oneline.Substring(killIdx, endIdx - killIdx);

        Assert.DoesNotContain("EnableRaisingEvents = false", killRegion);
    }

    // ─── helpers (mirror sibling source-pin loaders) ────────────────────

    /// <summary>Load ProcessRunner.cs source for source-string pinning.
    /// Walks up to 8 directories to handle partial-checkout CI scenarios
    /// where the test cwd isn't the repo root.</summary>
    private static string? LoadProcessRunnerSource()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(
                dir.FullName,
                "VPNRouter.Core", "Services", "ProcessRunner.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        return null;
    }

    /// <summary>Strip <c>//</c> line comments so commentary about the
    /// pattern doesn't fool Contains/DoesNotContain into reporting a
    /// phantom match.</summary>
    private static string StripLineComments(string src)
    {
        return string.Join('\n',
            src.Split('\n').Select(l =>
                l.Contains("//") ? l[..l.IndexOf("//")] : l));
    }
}
