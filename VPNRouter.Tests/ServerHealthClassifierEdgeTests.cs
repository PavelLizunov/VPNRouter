using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class ServerHealthClassifierEdgeTests
{
    private static ServerHealthVerdict Verdict(ServerHealthPhases p)
        => ServerHealthClassifier.Classify(p).Verdict;

    [Theory]
    [InlineData(PhaseOutcome.Skipped)]
    [InlineData(PhaseOutcome.Unknown)]
    public void NoTcpPhase_DeepVerifyPass_IsHealthy(PhaseOutcome tcp)
    {
        var v = Verdict(new ServerHealthPhases(TcpConnect: tcp, ProxiedHttpControl: PhaseOutcome.Pass));
        Assert.Equal(ServerHealthVerdict.Healthy, v);
    }

    [Fact]
    public void NoTcpPhase_DeepVerifyPass_CanaryFail_IsOnlyControlWorks()
        => Assert.Equal(ServerHealthVerdict.OnlyControlWorks,
            Verdict(new ServerHealthPhases(
                TcpConnect: PhaseOutcome.Skipped,
                ProxiedHttpControl: PhaseOutcome.Pass,
                BlockedTargetCanary: PhaseOutcome.Fail)));

    [Fact]
    public void SkippedTcp_DeepVerifyFail_StaysUnknown_NotBlocked()
    {
        var v = Verdict(new ServerHealthPhases(
            TcpConnect: PhaseOutcome.Skipped, ProxiedHttpControl: PhaseOutcome.Fail));
        Assert.Equal(ServerHealthVerdict.Unknown, v);
    }

    [Fact]
    public void TcpFail_ButDeepVerifyPass_ContradictoryPhases_HostLevelWins()
    {
        var v = Verdict(new ServerHealthPhases(
            TcpConnect: PhaseOutcome.Fail, ProxiedHttpControl: PhaseOutcome.Pass));
        Assert.Equal(ServerHealthVerdict.HostUnreachable, v);
    }

    [Fact]
    public void UdpNativeQuickSkip_DeepVerifyOk_ClassifiesHealthy()
    {
        var phases = ServerHealthPhaseMapper.Merge(
            ServerHealthPhaseMapper.FromQuickProbe(ServerProbeStatus.SkippedNotApplicable),
            ServerHealthPhaseMapper.FromDeepVerify(new DeepVerifyResult(true, 90, null, null)));
        Assert.Equal(ServerHealthVerdict.Healthy, ServerHealthClassifier.Classify(phases).Verdict);
    }

    [Fact]
    public void UdpNativeQuickSkip_DeepVerifyRealFailure_IsNotCondemnedAsBlocked()
    {
        var phases = ServerHealthPhaseMapper.Merge(
            ServerHealthPhaseMapper.FromQuickProbe(ServerProbeStatus.SkippedNotApplicable),
            ServerHealthPhaseMapper.FromDeepVerify(DeepVerifyResult.Failed("timeout")));
        var verdict = ServerHealthClassifier.Classify(phases).Verdict;
        Assert.Equal(ServerHealthVerdict.Unknown, verdict);
        Assert.NotEqual(ServerHealthVerdict.ProtocolHandshakeBlockedLikely, verdict);
    }

    [Fact]
    public void CanaryEvaluate_MixedFreshResults_IsPassButAmbiguous_NotCleanGlobalOk()
    {
        var agg = CanaryPolicy.Evaluate(true, new[] { (true, false), (false, false) });
        Assert.Equal(PhaseOutcome.Pass, agg.BlockedTargetCanary);
        Assert.True(agg.StaleOrAmbiguous);
    }
}
