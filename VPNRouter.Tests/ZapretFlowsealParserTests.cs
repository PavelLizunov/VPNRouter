#nullable enable

using System.Collections.Generic;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class ZapretFlowsealParserTests
{
    [Fact]
    public void ParseTranscript_NewSingleBracketFormat_CountsScores()
    {
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
        Assert.Equal("general (ALT9)", winner);
    }

    [Fact]
    public void ParseTranscript_ReversedOrder_StillPicksHigherScorer()
    {
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

    [Fact]
    public void ParseTranscript_OldTwoBracketFormat_StillParses()
    {
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
        Assert.Equal("strat B", winner);
        Assert.Equal(1, perStrategy["strat A"].Passed);
        Assert.Equal(2, perStrategy["strat B"].Passed);
    }

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
        Assert.Equal(2, perStrategy["s"].Passed);
        Assert.Equal(4, perStrategy["s"].Total);
    }

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

    [Fact]
    public void ParseTranscript_SilentWrapperScoresHighest_ExcludedFromWinner()
    {
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

        Assert.Equal("general (ALT9)", winner);
        Assert.False(perStrategy.ContainsKey("_vpnrouter_silent"));
        Assert.True(perStrategy.ContainsKey("general (ALT9)"));
    }

    [Fact]
    public void ParseTranscript_OnlySilentWrapper_NoWinner()
    {
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
            ["lowratio"] = new() { Passed = 5, Total = 20 },
            ["highratio"] = new() { Passed = 5, Total = 8 },
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
