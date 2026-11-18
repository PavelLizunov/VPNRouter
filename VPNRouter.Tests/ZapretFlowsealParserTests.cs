#nullable enable

using System.Collections.Generic;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class ZapretFlowsealParserTests
{
    [Fact]
    public void BestStrategyByScore_EmptyTable_ReturnsNull()
    {
        Assert.Null(ZapretAutoStrategy.BestStrategyByScore(
            new Dictionary<string, ZapretStrategyTestResult>()));
    }

    [Fact]
    public void BestStrategyByScore_RejectsCandidatesBelowTheWarmStartThreshold()
    {
        var scores = new Dictionary<string, ZapretStrategyTestResult> { ["general"] = new() { Passed = 2, Total = 8 } };
        Assert.Null(ZapretAutoStrategy.BestStrategyByScore(scores));
        scores["general"].Passed = 3;
        Assert.Equal("general", ZapretAutoStrategy.BestStrategyByScore(scores));
    }

    [Fact]
    public void BestStrategyByScore_EqualPasses_TieBreaksByRatio()
    {
        var table = new Dictionary<string, ZapretStrategyTestResult>
        {
            ["lowratio"] = new() { Passed = 5, Total = 15 },
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
