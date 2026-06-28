using VPNRouter.App.ViewModels.FreeConfigs;
using VPNRouter.Core.Services.FreeConfigs;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// F4 (v2.45.0): the latency/status sort key, lifted off the VM so the FreeConfigs
/// list dedups/orders/caps on raw entries before building VMs. These pin the same
/// ordering the instance LatencySortKey produced.
/// </summary>
public sealed class FreeConfigSortKeyTests
{
    [Theory]
    [InlineData(FreeConfigStatus.Verified, 50, 50)]          // verified real RTT = latency (best rank)
    [InlineData(FreeConfigStatus.Verified, 0, 90_000)]       // verified, unmeasured
    [InlineData(FreeConfigStatus.Ok, 80, 100_080)]           // ok + latency offset
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
