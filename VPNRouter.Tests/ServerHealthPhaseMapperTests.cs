using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class ServerHealthPhaseMapperTests
{
    [Theory]
    [InlineData(ServerProbeStatus.Ok, PhaseOutcome.Pass)]
    [InlineData(ServerProbeStatus.Slow, PhaseOutcome.Pass)]
    [InlineData(ServerProbeStatus.Unreachable, PhaseOutcome.Fail)]
    [InlineData(ServerProbeStatus.Timeout, PhaseOutcome.Fail)]
    public void FromQuickProbe_SetsTcpConnect(ServerProbeStatus status, PhaseOutcome expected)
        => Assert.Equal(expected, ServerHealthPhaseMapper.FromQuickProbe(status).TcpConnect);

    [Fact]
    public void FromQuickProbe_TlsFailed_IsTcpPassPlusTlsFail()
    {
        var p = ServerHealthPhaseMapper.FromQuickProbe(ServerProbeStatus.TlsFailed);
        Assert.Equal(PhaseOutcome.Pass, p.TcpConnect);
        Assert.Equal(PhaseOutcome.Fail, p.TlsCamouflage);
    }

    [Theory]
    [InlineData(ServerProbeStatus.Implausible)]
    [InlineData(ServerProbeStatus.SkippedNotApplicable)]
    [InlineData(ServerProbeStatus.Unknown)]
    public void FromQuickProbe_Inconclusive_LeavesAllUnknown(ServerProbeStatus status)
    {
        var p = ServerHealthPhaseMapper.FromQuickProbe(status);
        Assert.Equal(PhaseOutcome.Unknown, p.TcpConnect);
        Assert.Equal(PhaseOutcome.Unknown, p.TlsCamouflage);
    }

    [Fact]
    public void FromDeepVerify_Ok_IsProxiedHttpPass()
        => Assert.Equal(PhaseOutcome.Pass,
            ServerHealthPhaseMapper.FromDeepVerify(new DeepVerifyResult(true, 120, null, null)).ProxiedHttpControl);

    [Theory]
    [InlineData("sing-box binary missing")]
    [InlineData("sing-box spawn failed")]
    [InlineData("sing-box didn't bind")]
    [InlineData("sing-box: panic on start")]
    [InlineData("placeholder credential: public_key")]
    [InlineData("cancelled")]
    public void FromDeepVerify_LocalInfraError_IsInconclusive_NotFail(string error)
    {
        var p = ServerHealthPhaseMapper.FromDeepVerify(DeepVerifyResult.Failed(error));
        Assert.Equal(PhaseOutcome.Unknown, p.ProxiedHttpControl);
    }

    [Theory]
    [InlineData("http 502")]
    [InlineData("timeout")]
    [InlineData("http failed")]
    public void FromDeepVerify_RealProxiedFailure_IsProxiedHttpFail(string error)
        => Assert.Equal(PhaseOutcome.Fail,
            ServerHealthPhaseMapper.FromDeepVerify(DeepVerifyResult.Failed(error)).ProxiedHttpControl);

    [Fact]
    public void FromDeepVerify_Null_IsAllUnknown()
        => Assert.Equal(PhaseOutcome.Unknown, ServerHealthPhaseMapper.FromDeepVerify(null).ProxiedHttpControl);

    [Theory]
    [InlineData(DeepVerifyFailurePhase.Precondition)]
    [InlineData(DeepVerifyFailurePhase.LocalSpawn)]
    [InlineData(DeepVerifyFailurePhase.SocksBind)]
    [InlineData(DeepVerifyFailurePhase.Cancelled)]
    public void FromDeepVerify_TypedLocalInfraPhase_IsInconclusive(DeepVerifyFailurePhase phase)
        => Assert.Equal(PhaseOutcome.Unknown,
            ServerHealthPhaseMapper.FromDeepVerify(
                DeepVerifyResult.Failed("whatever", phase)).ProxiedHttpControl);

    [Theory]
    [InlineData(DeepVerifyFailurePhase.ProxiedHttp)]
    [InlineData(DeepVerifyFailurePhase.Timeout)]
    public void FromDeepVerify_TypedServerMeaningfulPhase_IsFail(DeepVerifyFailurePhase phase)
        => Assert.Equal(PhaseOutcome.Fail,
            ServerHealthPhaseMapper.FromDeepVerify(
                DeepVerifyResult.Failed("whatever", phase)).ProxiedHttpControl);

    [Fact]
    public void FromDeepVerify_UnsupportedByVerifier_IsSkipped_NotFail()
        => Assert.Equal(PhaseOutcome.Skipped,
            ServerHealthPhaseMapper.FromDeepVerify(
                DeepVerifyResult.Failed("deep verify: AmneziaWG needs the lx core (with_awg)",
                    DeepVerifyFailurePhase.UnsupportedByVerifier)).ProxiedHttpControl);

    [Fact]
    public void FromDeepVerify_TypedPhase_BeatsContradictoryErrorString()
    {
        var r = DeepVerifyResult.Failed("http failed", DeepVerifyFailurePhase.LocalSpawn);
        Assert.Equal(PhaseOutcome.Unknown,
            ServerHealthPhaseMapper.FromDeepVerify(r).ProxiedHttpControl);
    }

    [Fact]
    public void TcpOk_UnsupportedByVerifier_ClassifiesAsUntested_NeverBlocked()
    {
        var phases = ServerHealthPhaseMapper.Merge(
            ServerHealthPhaseMapper.FromQuickProbe(ServerProbeStatus.Ok),
            ServerHealthPhaseMapper.FromDeepVerify(
                DeepVerifyResult.Failed("deep verify: xhttp needs the lx core (with_xhttp)",
                    DeepVerifyFailurePhase.UnsupportedByVerifier)));
        var verdict = ServerHealthClassifier.Classify(phases).Verdict;
        Assert.Equal(ServerHealthVerdict.TcpOpenProtocolUntested, verdict);
        Assert.NotEqual(ServerHealthVerdict.ProtocolHandshakeBlockedLikely, verdict);
    }

    [Fact]
    public void TcpOk_UserCancelledDeepVerify_ClassifiesAsUntested_NeverBlocked()
    {
        var phases = ServerHealthPhaseMapper.Merge(
            ServerHealthPhaseMapper.FromQuickProbe(ServerProbeStatus.Ok),
            ServerHealthPhaseMapper.FromDeepVerify(
                DeepVerifyResult.Failed("cancelled", DeepVerifyFailurePhase.Cancelled)));
        var verdict = ServerHealthClassifier.Classify(phases).Verdict;
        Assert.Equal(ServerHealthVerdict.TcpOpenProtocolUntested, verdict);
        Assert.NotEqual(ServerHealthVerdict.ProtocolHandshakeBlockedLikely, verdict);
    }

    [Fact]
    public void FromDeepVerify_OkWithCanaryFail_YieldsOnlyControlWorks()
    {
        var phases = ServerHealthPhaseMapper.FromDeepVerify(
            new DeepVerifyResult(true, 120, null, null, BlockedCanary: PhaseOutcome.Fail));
        Assert.Equal(PhaseOutcome.Pass, phases.ProxiedHttpControl);
        Assert.Equal(PhaseOutcome.Fail, phases.BlockedTargetCanary);

        Assert.Equal(ServerHealthVerdict.OnlyControlWorks,
            ServerHealthClassifier.Classify(phases).Verdict);
    }

    [Fact]
    public void FromDeepVerify_OkWithoutCanary_StaysHealthy_BackCompat()
    {
        var phases = ServerHealthPhaseMapper.FromDeepVerify(new DeepVerifyResult(true, 120, null, null));
        Assert.Equal(PhaseOutcome.Unknown, phases.BlockedTargetCanary);
        Assert.Equal(ServerHealthVerdict.Healthy, ServerHealthClassifier.Classify(phases).Verdict);
    }

    [Fact]
    public void Merge_LaterNonUnknownWins_UnionsPhases()
    {
        var quick = ServerHealthPhaseMapper.FromQuickProbe(ServerProbeStatus.Ok);
        var deep = ServerHealthPhaseMapper.FromDeepVerify(DeepVerifyResult.Failed("timeout"));
        var merged = ServerHealthPhaseMapper.Merge(quick, deep);
        Assert.Equal(PhaseOutcome.Pass, merged.TcpConnect);
        Assert.Equal(PhaseOutcome.Fail, merged.ProxiedHttpControl);
    }

    [Fact]
    public void TcpOk_ProxiedHttpFail_ClassifiesAsProtocolBlockedLikely()
    {
        var phases = ServerHealthPhaseMapper.Merge(
            ServerHealthPhaseMapper.FromQuickProbe(ServerProbeStatus.Ok),
            ServerHealthPhaseMapper.FromDeepVerify(DeepVerifyResult.Failed("http failed")));
        Assert.Equal(ServerHealthVerdict.ProtocolHandshakeBlockedLikely,
            ServerHealthClassifier.Classify(phases).Verdict);
    }

    [Fact]
    public void TcpOk_LocalSingBoxFailure_DoesNotClassifyAsBlocked()
    {
        var phases = ServerHealthPhaseMapper.Merge(
            ServerHealthPhaseMapper.FromQuickProbe(ServerProbeStatus.Ok),
            ServerHealthPhaseMapper.FromDeepVerify(DeepVerifyResult.Failed("sing-box spawn failed")));
        var verdict = ServerHealthClassifier.Classify(phases).Verdict;
        Assert.Equal(ServerHealthVerdict.TcpOpenProtocolUntested, verdict);
        Assert.NotEqual(ServerHealthVerdict.ProtocolHandshakeBlockedLikely, verdict);
    }
}
