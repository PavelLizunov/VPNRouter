using VPNRouter.App.ViewModels.FreeConfigs;
using VPNRouter.Core.Services.FreeConfigs;
using Xunit;

namespace VPNRouter.Tests;

public sealed class FreeConfigSortKeyTests
{
    [Theory]
    [InlineData(FreeConfigStatus.Verified, 50, 50)]
    [InlineData(FreeConfigStatus.Verified, 0, 90_000)]
    [InlineData(FreeConfigStatus.Ok, 80, 100_080)]
    [InlineData(FreeConfigStatus.Slow, 30, 200_030)]
    [InlineData(FreeConfigStatus.Implausible, 0, 400_000)]
    [InlineData(FreeConfigStatus.TlsFailed, 0, 500_000)]
    [InlineData(FreeConfigStatus.Timeout, 0, 1_000_000)]
    [InlineData(FreeConfigStatus.Unreachable, 0, 1_000_001)]
    public void SortKeyFor_MatchesStatusAndLatency(FreeConfigStatus status, int latency, int expected)
    {
        var e = new FreeConfigEntry { Status = status, LatencyMs = latency };
        Assert.Equal(expected, FreeConfigItemViewModel.SortKeyFor(e));
    }

    [Fact]
    public void VerifiedRtt_RanksBelowOk()
    {
        var verified = new FreeConfigEntry { Status = FreeConfigStatus.Verified, LatencyMs = 500 };
        var ok = new FreeConfigEntry { Status = FreeConfigStatus.Ok, LatencyMs = 10 };
        Assert.True(FreeConfigItemViewModel.SortKeyFor(verified) < FreeConfigItemViewModel.SortKeyFor(ok));
    }
}
