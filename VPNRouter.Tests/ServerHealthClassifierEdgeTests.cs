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
    public void NoTcpPhase_DeepVerifyPass_UdpAppFail_IsUdpOrAppProfileFailed()
        => Assert.Equal(ServerHealthVerdict.UdpOrAppProfileFailed,
            Verdict(new ServerHealthPhases(
                TcpConnect: PhaseOutcome.Skipped,
                ProxiedHttpControl: PhaseOutcome.Pass,
                UdpAppProfile: PhaseOutcome.Fail)));

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
    public void TlsSkipped_TcpPass_HttpPass_IsHealthy()
        => Assert.Equal(ServerHealthVerdict.Healthy,
            Verdict(new ServerHealthPhases(
                TcpConnect: PhaseOutcome.Pass,
                TlsCamouflage: PhaseOutcome.Skipped,
                ProxiedHttpControl: PhaseOutcome.Pass)));

    [Fact]
    public void TlsSkipped_TcpOnly_IsStillTcpOpenProtocolUntested()
        => Assert.Equal(ServerHealthVerdict.TcpOpenProtocolUntested,
            Verdict(new ServerHealthPhases(
                TcpConnect: PhaseOutcome.Pass, TlsCamouflage: PhaseOutcome.Skipped)));

    [Fact]
    public void CanaryAndUdpSkipped_HttpPass_IsHealthy()
        => Assert.Equal(ServerHealthVerdict.Healthy,
            Verdict(new ServerHealthPhases(
                TcpConnect: PhaseOutcome.Pass,
                ProxiedHttpControl: PhaseOutcome.Pass,
                BlockedTargetCanary: PhaseOutcome.Skipped,
                UdpAppProfile: PhaseOutcome.Skipped)));

    [Fact]
    public void CanaryFail_AndUdpFail_CanaryVerdictWins()
        => Assert.Equal(ServerHealthVerdict.OnlyControlWorks,
            Verdict(new ServerHealthPhases(
                TcpConnect: PhaseOutcome.Pass,
                ProxiedHttpControl: PhaseOutcome.Pass,
                BlockedTargetCanary: PhaseOutcome.Fail,
                UdpAppProfile: PhaseOutcome.Fail)));

    [Fact]
    public void CanaryPass_UdpFail_IsUdpOrAppProfileFailed()
        => Assert.Equal(ServerHealthVerdict.UdpOrAppProfileFailed,
            Verdict(new ServerHealthPhases(
                TcpConnect: PhaseOutcome.Pass,
                ProxiedHttpControl: PhaseOutcome.Pass,
                BlockedTargetCanary: PhaseOutcome.Pass,
                UdpAppProfile: PhaseOutcome.Fail)));

    [Fact]
    public void CanaryFail_UdpPass_IsOnlyControlWorks()
        => Assert.Equal(ServerHealthVerdict.OnlyControlWorks,
            Verdict(new ServerHealthPhases(
                TcpConnect: PhaseOutcome.Pass,
                ProxiedHttpControl: PhaseOutcome.Pass,
                BlockedTargetCanary: PhaseOutcome.Fail,
                UdpAppProfile: PhaseOutcome.Pass)));

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

    [Fact]
    public void CanaryEvaluate_AllFreshPassed_IsCleanPass()
    {
        var agg = CanaryPolicy.Evaluate(true, new[] { (true, false), (true, false) });
        Assert.Equal(PhaseOutcome.Pass, agg.BlockedTargetCanary);
        Assert.False(agg.StaleOrAmbiguous);
    }

    [Fact]
    public void DirectBlockedTargetProbes_AreOffByDefault()
        => Assert.False(CanaryPolicy.DirectProbesDefaultEnabled);
}
