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
