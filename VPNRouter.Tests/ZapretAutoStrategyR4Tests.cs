#nullable enable
// ============================================================================
// ZapretAutoStrategyR4Tests.cs — v2.37.0-r4 (2026-05-25)
// ============================================================================
//
// Tests for v2.37.0-r4 ZapretAutoStrategy polish (Flowseal-probe ipset
// restore safety net + admin pre-check + FlowsealProgress score fields).
//
// Coverage:
//   * HasOrphanedIpsetFlag — file presence detection.
//   * RestoreIpsetAfterKill — no-op without flag, restores backup when
//     present, deletes stale flag when no backup, fully idempotent.
//   * FlowsealProgress — record carries OkCount/TotalChecks with
//     back-compat defaults so r3 callers (3-arg ctor) keep working.
//   * FlowsealSweepResult — record carries Diagnostic + ErrorLines with
//     back-compat defaults so r3 callers (4-arg ctor) keep working.
//   * IsRunningAsAdmin — returns false on non-Windows (covers the OS
//     guard inside RunFlowsealProbeAsync's `not_windows` early return).
//
// What's NOT in scope here:
//   * Live `RunFlowsealProbeAsync` integration with a real powershell
//     process — that's Windows-only + admin-only + requires installed
//     Flowseal binary. Smoke-tested via MCP at ship time per
//     `plans/v2.37.0-r4-polish-design-2026-05-25.md` §"Verification gate".
// ============================================================================

using System;
using System.IO;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class ZapretAutoStrategyR4Tests : IDisposable
{
    private readonly string _tempRoot;

    public ZapretAutoStrategyR4Tests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(),
            $"vpnrouter-r4-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(Path.Combine(_tempRoot, "lists"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { /* test cleanup is best-effort */ }
    }

    // ── HasOrphanedIpsetFlag ────────────────────────────────────────────────

    [Fact]
    public void HasOrphanedIpsetFlag_NoFlag_ReturnsFalse()
    {
        Assert.False(ZapretAutoStrategy.HasOrphanedIpsetFlag(_tempRoot));
    }

    [Fact]
    public void HasOrphanedIpsetFlag_FlagPresent_ReturnsTrue()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "ipset_switched.flag"), "");
        Assert.True(ZapretAutoStrategy.HasOrphanedIpsetFlag(_tempRoot));
    }

    [Fact]
    public void HasOrphanedIpsetFlag_NonexistentDir_ReturnsFalse()
    {
        // Should not throw — just return false.
        var bogus = Path.Combine(_tempRoot, "no-such-dir");
        Assert.False(ZapretAutoStrategy.HasOrphanedIpsetFlag(bogus));
    }

    // ── RestoreIpsetAfterKill ───────────────────────────────────────────────

    [Fact]
    public void RestoreIpsetAfterKill_NoFlag_NoOp()
    {
        // No flag, no backup, no live file — should silently succeed.
        ZapretAutoStrategy.RestoreIpsetAfterKill(_tempRoot, logger: null);

        // Verify nothing was created.
        Assert.False(File.Exists(Path.Combine(_tempRoot, "ipset_switched.flag")));
        Assert.False(File.Exists(Path.Combine(_tempRoot, "lists", "ipset-all.txt")));
    }

    [Fact]
    public void RestoreIpsetAfterKill_WithFlagAndBackup_RestoresAndDeletesFlag()
    {
        // Arrange: simulate orphaned state — flag + backup present, live
        // file is the "any" mode placeholder we want to overwrite.
        var listsDir = Path.Combine(_tempRoot, "lists");
        var flagPath = Path.Combine(_tempRoot, "ipset_switched.flag");
        var backupPath = Path.Combine(listsDir, "ipset-all.test-backup.txt");
        var livePath = Path.Combine(listsDir, "ipset-all.txt");

        File.WriteAllText(flagPath, "");
        File.WriteAllText(backupPath, "1.2.3.4/32\n5.6.7.8/32\n");
        File.WriteAllText(livePath, ""); // empty = "any" mode

        // Act
        ZapretAutoStrategy.RestoreIpsetAfterKill(_tempRoot, logger: null);

        // Assert: live file restored from backup, flag deleted, backup
        // consumed (move semantics — matches Flowseal's restore behavior).
        Assert.False(File.Exists(flagPath));
        Assert.False(File.Exists(backupPath));
        Assert.True(File.Exists(livePath));
        Assert.Equal("1.2.3.4/32\n5.6.7.8/32\n", File.ReadAllText(livePath));
    }

    [Fact]
    public void RestoreIpsetAfterKill_FlagOnlyMissingBackup_DeletesFlagLeavesLiveAlone()
    {
        // Arrange: flag present but no backup file (edge case — Flowseal
        // crashed BEFORE creating the backup, or backup was manually
        // cleaned up). We delete the flag but don't touch the live file.
        var listsDir = Path.Combine(_tempRoot, "lists");
        var flagPath = Path.Combine(_tempRoot, "ipset_switched.flag");
        var livePath = Path.Combine(listsDir, "ipset-all.txt");

        File.WriteAllText(flagPath, "");
        File.WriteAllText(livePath, "PRESERVED-ORIGINAL");

        // Act
        ZapretAutoStrategy.RestoreIpsetAfterKill(_tempRoot, logger: null);

        // Assert: flag deleted, live file untouched (we don't guess what
        // the original contents were — matches Flowseal's same-edge
        // behavior on its own next-run cleanup).
        Assert.False(File.Exists(flagPath));
        Assert.Equal("PRESERVED-ORIGINAL", File.ReadAllText(livePath));
    }

    [Fact]
    public void RestoreIpsetAfterKill_Idempotent_SafeToCallTwice()
    {
        // Should be safe to call repeatedly — the post-probe finally{}
        // block in MainWindowViewModel may invoke this on every Stop /
        // Cancel / exception path, even when there's nothing to restore.
        var listsDir = Path.Combine(_tempRoot, "lists");
        var flagPath = Path.Combine(_tempRoot, "ipset_switched.flag");
        var backupPath = Path.Combine(listsDir, "ipset-all.test-backup.txt");
        var livePath = Path.Combine(listsDir, "ipset-all.txt");

        File.WriteAllText(flagPath, "");
        File.WriteAllText(backupPath, "ORIGINAL-LIST");
        File.WriteAllText(livePath, "");

        ZapretAutoStrategy.RestoreIpsetAfterKill(_tempRoot, logger: null);
        ZapretAutoStrategy.RestoreIpsetAfterKill(_tempRoot, logger: null); // no-op now

        Assert.False(File.Exists(flagPath));
        Assert.Equal("ORIGINAL-LIST", File.ReadAllText(livePath));
    }

    // ── FlowsealProgress shape ──────────────────────────────────────────────

    [Fact]
    public void FlowsealProgress_ScoreFieldsDefault_Zero()
    {
        // r3 callers used the 3-arg constructor — score fields must
        // default to 0 so the ViewModel's "if (TotalChecks > 0)" branch
        // doesn't fire spuriously.
        var p = new ZapretAutoStrategy.FlowsealProgress(5, 20, "general (ALT3)");
        Assert.Equal(5, p.CurrentIndex);
        Assert.Equal(20, p.TotalCount);
        Assert.Equal("general (ALT3)", p.StrategyName);
        Assert.Equal(0, p.OkCount);
        Assert.Equal(0, p.TotalChecks);
    }

    [Fact]
    public void FlowsealProgress_WithScores_CarriesValues()
    {
        var p = new ZapretAutoStrategy.FlowsealProgress(
            CurrentIndex: 5, TotalCount: 20, StrategyName: "general (ALT3)",
            OkCount: 12, TotalChecks: 18);
        Assert.Equal(12, p.OkCount);
        Assert.Equal(18, p.TotalChecks);
    }

    [Fact]
    public void FlowsealProgress_ScoreOnlyUpdate_EmptyStrategyName()
    {
        // Per the parser convention, when only the score advances inside
        // an already-running config, StrategyName is the empty string —
        // ViewModel keeps the last-known name. Verifying the shape holds.
        var p = new ZapretAutoStrategy.FlowsealProgress(5, 20, string.Empty, 3, 6);
        Assert.Equal(string.Empty, p.StrategyName);
        Assert.Equal(3, p.OkCount);
        Assert.Equal(6, p.TotalChecks);
    }

    // ── FlowsealSweepResult shape (Diagnostic + ErrorLines) ─────────────────

    [Fact]
    public void FlowsealSweepResult_BackCompatCtor_DiagnosticAndErrorLinesDefault()
    {
        // r3 callers used the 4-arg constructor; new fields must default
        // to null / empty so existing test assertions keep passing.
        var r = new ZapretAutoStrategy.FlowsealSweepResult(
            Winner: "general (ALT3)", TestedCount: 20, TotalCount: 20,
            FullOutput: "<output>");
        Assert.Equal("general (ALT3)", r.Winner);
        Assert.Null(r.Diagnostic);
        Assert.Null(r.ErrorLines);
    }

    [Fact]
    public void FlowsealSweepResult_WithDiagnostic_CarriesTypedToken()
    {
        var r = new ZapretAutoStrategy.FlowsealSweepResult(
            Winner: null, TestedCount: 0, TotalCount: 0, FullOutput: "",
            Diagnostic: "not_admin", ErrorLines: Array.Empty<string>());
        Assert.Equal("not_admin", r.Diagnostic);
        Assert.NotNull(r.ErrorLines);
        Assert.Empty(r.ErrorLines!);
    }

    [Fact]
    public void FlowsealSweepResult_WithErrorLines_PreservesList()
    {
        var errs = new[] { "[ERROR] zapret service installed", "[WARN] curl missing" };
        var r = new ZapretAutoStrategy.FlowsealSweepResult(
            Winner: null, TestedCount: 1, TotalCount: 20, FullOutput: "",
            Diagnostic: "canceled", ErrorLines: errs);
        Assert.Equal(2, r.ErrorLines!.Count);
        Assert.Contains("[ERROR] zapret service installed", r.ErrorLines);
        Assert.Contains("[WARN] curl missing", r.ErrorLines);
    }

    // ── IsRunningAsAdmin ────────────────────────────────────────────────────

    [Fact]
    public void IsRunningAsAdmin_NonWindows_ReturnsFalse()
    {
        // The early-return guard relies on this being false off-Windows.
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(ZapretAutoStrategy.IsRunningAsAdmin());
        }
        // On Windows we can't assert true (depends on test-run elevation)
        // but the call must not throw.
        else
        {
            // Smoke: just verifies it returns a bool without throwing.
            var _ = ZapretAutoStrategy.IsRunningAsAdmin();
        }
    }

    // ── FlowsealMaxSweepTime sanity ─────────────────────────────────────────

    [Fact]
    public void FlowsealMaxSweepTime_IsTenMinutes()
    {
        // r4 C.2 — hard cap of 10 minutes documented in design plan.
        // Pin via test so a future "let's make it 30 min" tweak surfaces
        // the policy change loudly during code review.
        Assert.Equal(TimeSpan.FromMinutes(10), ZapretAutoStrategy.FlowsealMaxSweepTime);
    }
}
