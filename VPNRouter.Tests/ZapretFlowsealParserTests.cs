#nullable enable
// ============================================================================
// ZapretFlowsealParserTests.cs — v2.37.0-r53 (2026-05-28)
// ============================================================================
//
// Regression tests for the Flowseal `test zapret.ps1` stdout parser
// (ZapretAutoStrategy.ParseFlowsealTranscript + BestStrategyByScore).
//
// ROOT BUG (user report Z:\zapret 2026-05-28): Flowseal's current mode-2
// output moved the target id onto a separate "=== [flag][provider] TARGET ==="
// header line, so per-test status lines are now a BARE single bracket:
//   [HTTP]   code=405 … status=OK
// The old statusLineRx required two brackets ("[TargetId][HTTP] …") so it
// never matched → every strategy scored 0/0 → no winner → "стратегия не
// найдена" even though ALT3 (45/108) and ALT9 (75/108) clearly passed.
//
// These tests pin BOTH formats (new single-bracket + historical two-bracket),
// the empirical best-by-score winner selection, and the UNSUPPORTED-as-pass /
// LIKELY_BLOCKED-as-fail status semantics.
// ============================================================================

using System.Collections.Generic;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class ZapretFlowsealParserTests
{
    // ── New single-bracket format (the current Flowseal output) ─────────────

    [Fact]
    public void ParseTranscript_NewSingleBracketFormat_CountsScores()
    {
        // Exact line shape from the user's Z:\zapret transcript.
        const string transcript = @"
  [1/1] general (ALT3).bat
------------------------------------------------------------
  > Starting config...
[INFO] Targets: 2; Timeout: 5s

=== [🧠][Self check] US.GH-HPRN ===
[HTTP] code=405 buf_up=65536 bytes (64 KB) buf_down=131 bytes (0.1 KB) time=0.222468s status=OK
[TLS1.2] code=405 buf_up=65536 bytes (64 KB) buf_down=131 bytes (0.1 KB) time=0.264201s status=OK
[TLS1.3] code=405 buf_up=65536 bytes (64 KB) buf_down=131 bytes (0.1 KB) time=0.216726s status=OK
  No 16-20KB freeze pattern for this target.

=== [🇩🇪][AWS] DE.AWS-01 ===
[HTTP] code=000 buf_up=0 bytes (0 KB) buf_down=0 bytes (0 KB) time=5.000656s status=FAIL
[TLS1.2] code=000 buf_up=0 bytes (0 KB) buf_down=0 bytes (0 KB) time=5.001159s status=FAIL
[TLS1.3] code=000 buf_up=0 bytes (0 KB) buf_down=0 bytes (0 KB) time=5.000815s status=FAIL

=== ANALYTICS ===
general (ALT3).bat : OK: 3, FAIL: 3, UNSUP: 0, BLOCKED: 0
Best config: general (ALT3).bat
";
        var (winner, perStrategy) = ZapretAutoStrategy.ParseFlowsealTranscript(transcript);

        Assert.True(perStrategy.ContainsKey("general (ALT3)"));
        Assert.Equal(3, perStrategy["general (ALT3)"].Passed);
        Assert.Equal(6, perStrategy["general (ALT3)"].Total);
        Assert.Equal("general (ALT3)", winner);
    }

    [Fact]
    public void ParseTranscript_TwoStrategyRuns_PicksHigherScorer()
    {
        // Mirrors the user's two-[1/1]-run transcript: ALT3 scores lower,
        // ALT9 higher. The empirical best (ALT9) must win regardless of the
        // order the "Best config:" lines appear.
        const string transcript = @"
  [1/1] general (ALT3).bat
=== [F][P] T1 ===
[HTTP] code=405 ... status=OK
[TLS1.2] code=405 ... status=OK
[TLS1.3] code=405 ... status=OK
=== [F][P] T2 ===
[HTTP] code=000 ... status=FAIL
[TLS1.2] code=000 ... status=FAIL
[TLS1.3] code=000 ... status=FAIL
Best config: general (ALT3).bat

  [1/1] general (ALT9).bat
=== [F][P] T1 ===
[HTTP] code=200 ... status=OK
[TLS1.2] code=200 ... status=OK
[TLS1.3] code=200 ... status=OK
=== [F][P] T2 ===
[HTTP] code=403 ... status=OK
[TLS1.2] code=403 ... status=OK
[TLS1.3] code=403 ... status=OK
Best config: general (ALT9).bat
";
        var (winner, perStrategy) = ZapretAutoStrategy.ParseFlowsealTranscript(transcript);

        Assert.Equal(3, perStrategy["general (ALT3)"].Passed);
        Assert.Equal(6, perStrategy["general (ALT3)"].Total);
        Assert.Equal(6, perStrategy["general (ALT9)"].Passed);
        Assert.Equal(6, perStrategy["general (ALT9)"].Total);
        // ALT9 (6/6) beats ALT3 (3/6) — empirical best wins.
        Assert.Equal("general (ALT9)", winner);
    }

    [Fact]
    public void ParseTranscript_ReversedOrder_StillPicksHigherScorer()
    {
        // Same as above but ALT9 probed FIRST. Pre-r53 "last Best config: wins"
        // would wrongly return ALT3 here; empirical best-by-score returns ALT9.
        const string transcript = @"
  [1/1] general (ALT9).bat
=== [F][P] T1 ===
[HTTP] code=200 ... status=OK
[TLS1.2] code=200 ... status=OK
[TLS1.3] code=200 ... status=OK
=== [F][P] T2 ===
[HTTP] code=403 ... status=OK
[TLS1.2] code=403 ... status=OK
[TLS1.3] code=403 ... status=OK
Best config: general (ALT9).bat

  [1/1] general (ALT3).bat
=== [F][P] T1 ===
[HTTP] code=405 ... status=OK
[TLS1.2] code=405 ... status=OK
[TLS1.3] code=405 ... status=OK
=== [F][P] T2 ===
[HTTP] code=000 ... status=FAIL
[TLS1.2] code=000 ... status=FAIL
[TLS1.3] code=000 ... status=FAIL
Best config: general (ALT3).bat
";
        var (winner, _) = ZapretAutoStrategy.ParseFlowsealTranscript(transcript);
        Assert.Equal("general (ALT9)", winner);
    }

    // ── Historical two-bracket format (back-compat) ─────────────────────────

    [Fact]
    public void ParseTranscript_OldTwoBracketFormat_StillParses()
    {
        // The pre-format-drift shape: "[YT_LIVE@0][HTTP] … status=OK".
        const string transcript = @"
  [1/20] general (ALT3).bat
[YT_LIVE@0][HTTP] code=200 size=123 status=OK
[YT_LIVE@0][TLS1.2] code=200 size=123 status=OK
[YT_LIVE@0][TLS1.3] code=200 size=123 status=OK
Best config: general (ALT3).bat
";
        var (winner, perStrategy) = ZapretAutoStrategy.ParseFlowsealTranscript(transcript);
        Assert.Equal("general (ALT3)", winner);
        Assert.Equal(3, perStrategy["general (ALT3)"].Passed);
        Assert.Equal(3, perStrategy["general (ALT3)"].Total);
    }

    // ── Winner fallback when no "Best config:" line is present ──────────────

    [Fact]
    public void ParseTranscript_NoExplicitWinnerLine_FallsBackToBestScore()
    {
        const string transcript = @"
  [1/1] strat A.bat
[HTTP] code=200 ... status=OK
=== next ===
  [1/1] strat B.bat
[HTTP] code=200 ... status=OK
[TLS1.2] code=200 ... status=OK
";
        var (winner, perStrategy) = ZapretAutoStrategy.ParseFlowsealTranscript(transcript);
        // No "Best config:" anywhere — promote best-scoring (B: 2 > A: 1).
        Assert.Equal("strat B", winner);
        Assert.Equal(1, perStrategy["strat A"].Passed);
        Assert.Equal(2, perStrategy["strat B"].Passed);
    }

    // ── Status-value semantics ──────────────────────────────────────────────

    [Fact]
    public void ParseTranscript_UnsupportedCountsAsPass_LikelyBlockedCountsAsFail()
    {
        const string transcript = @"
  [1/1] s.bat
[HTTP] code=200 ... status=OK
[TLS1.2] code=0 ... status=UNSUPPORTED
[TLS1.3] code=000 buf_up=65347 bytes (63.8 KB) buf_down=0 bytes (0 KB) time=5.0s status=LIKELY_BLOCKED
[HTTP] code=000 ... status=FAIL
Best config: s.bat
";
        var (_, perStrategy) = ZapretAutoStrategy.ParseFlowsealTranscript(transcript);
        // OK + UNSUPPORTED = 2 pass; LIKELY_BLOCKED + FAIL = not pass; total 4.
        Assert.Equal(2, perStrategy["s"].Passed);
        Assert.Equal(4, perStrategy["s"].Total);
    }

    // ── Degenerate inputs ───────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("random noise\nno markers here\njust text")]
    public void ParseTranscript_NoParseableContent_ReturnsNullWinnerEmptyTable(string? transcript)
    {
        var (winner, perStrategy) = ZapretAutoStrategy.ParseFlowsealTranscript(transcript);
        Assert.Null(winner);
        Assert.Empty(perStrategy);
    }

    [Fact]
    public void ParseTranscript_ConfigHeaderButZeroPasses_NoWinner()
    {
        // A strategy that failed every target must NOT be promoted to winner.
        const string transcript = @"
  [1/1] dead.bat
[HTTP] code=000 ... status=FAIL
[TLS1.2] code=000 ... status=FAIL
";
        var (winner, perStrategy) = ZapretAutoStrategy.ParseFlowsealTranscript(transcript);
        Assert.Null(winner);
        Assert.Equal(0, perStrategy["dead"].Passed);
        Assert.Equal(2, perStrategy["dead"].Total);
    }

    // ── r54: _vpnrouter_silent wrapper exclusion ────────────────────────────

    [Fact]
    public void ParseTranscript_SilentWrapperScoresHighest_ExcludedFromWinner()
    {
        // Flowseal's all-configs sweep tests every *.bat including VPNRouter's
        // own runtime wrapper "_vpnrouter_silent.bat", which scores as high as
        // the active strategy. It must NOT win (no catalogue entry → would
        // recur "стратегия не найдена") nor appear in the per-strategy table.
        const string transcript = @"
  [1/2] _vpnrouter_silent.bat
=== T1 ===
[HTTP] code=200 ... status=OK
[TLS1.2] code=200 ... status=OK
[TLS1.3] code=200 ... status=OK
=== T2 ===
[HTTP] code=403 ... status=OK
[TLS1.2] code=403 ... status=OK
[TLS1.3] code=403 ... status=OK
  [2/2] general (ALT9).bat
=== T1 ===
[HTTP] code=200 ... status=OK
[TLS1.2] code=200 ... status=OK
[TLS1.3] code=000 ... status=FAIL
Best config: _vpnrouter_silent.bat
";
        var (winner, perStrategy) = ZapretAutoStrategy.ParseFlowsealTranscript(transcript);

        // Wrapper scored 6/6 (vs ALT9 2/3) and is even named in "Best config:",
        // yet the winner must be the real catalogue strategy.
        Assert.Equal("general (ALT9)", winner);
        Assert.False(perStrategy.ContainsKey("_vpnrouter_silent"));
        Assert.True(perStrategy.ContainsKey("general (ALT9)"));
    }

    [Fact]
    public void ParseTranscript_OnlySilentWrapper_NoWinner()
    {
        // If the wrapper is the ONLY thing probed, there's no usable catalogue
        // winner — must return null, not "_vpnrouter_silent".
        const string transcript = @"
  [1/1] _vpnrouter_silent.bat
=== T1 ===
[HTTP] code=200 ... status=OK
[TLS1.2] code=200 ... status=OK
Best config: _vpnrouter_silent.bat
";
        var (winner, perStrategy) = ZapretAutoStrategy.ParseFlowsealTranscript(transcript);
        Assert.Null(winner);
        Assert.Empty(perStrategy);
    }

    // ── BestStrategyByScore unit ────────────────────────────────────────────

    [Fact]
    public void BestStrategyByScore_EmptyTable_ReturnsNull()
    {
        Assert.Null(ZapretAutoStrategy.BestStrategyByScore(
            new Dictionary<string, ZapretStrategyTestResult>()));
    }

    [Fact]
    public void BestStrategyByScore_AllZeroPasses_ReturnsNull()
    {
        var table = new Dictionary<string, ZapretStrategyTestResult>
        {
            ["a"] = new() { Passed = 0, Total = 10 },
            ["b"] = new() { Passed = 0, Total = 5 },
        };
        Assert.Null(ZapretAutoStrategy.BestStrategyByScore(table));
    }

    [Fact]
    public void BestStrategyByScore_EqualPasses_TieBreaksByRatio()
    {
        var table = new Dictionary<string, ZapretStrategyTestResult>
        {
            ["lowratio"] = new() { Passed = 5, Total = 20 },  // 0.25
            ["highratio"] = new() { Passed = 5, Total = 8 },  // 0.625
        };
        Assert.Equal("highratio", ZapretAutoStrategy.BestStrategyByScore(table));
    }

    [Fact]
    public void BestStrategyByScore_PicksMaxPasses()
    {
        var table = new Dictionary<string, ZapretStrategyTestResult>
        {
            ["alt3"] = new() { Passed = 45, Total = 108 },
            ["alt9"] = new() { Passed = 75, Total = 108 },
        };
        Assert.Equal("alt9", ZapretAutoStrategy.BestStrategyByScore(table));
    }
}
