using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Pins <see cref="ServerHealthPhaseMapper"/>: the pure bridge from the existing quick
/// (<see cref="ServerProbeStatus"/>) and deep (<see cref="DeepVerifyResult"/>) probe
/// outputs into <see cref="ServerHealthPhases"/>. The load-bearing rule: a LOCAL sing-box
/// failure must never read as a server protocol block.
/// </summary>
public class ServerHealthPhaseMapperTests
{
    // ── Quick probe → phases ────────────────────────────────────────────────

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

    // ── Deep verify → phases ────────────────────────────────────────────────

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
        // The point: our sing-box never carried a request, so this says NOTHING about the server.
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

    // ── R1: typed failure phases (string heuristic = legacy fallback only) ──

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
        // The typed phase is authoritative: an error TEXT that looks server-meaningful
        // ("http failed") must not override a typed local-infra phase.
        var r = DeepVerifyResult.Failed("http failed", DeepVerifyFailurePhase.LocalSpawn);
        Assert.Equal(PhaseOutcome.Unknown,
            ServerHealthPhaseMapper.FromDeepVerify(r).ProxiedHttpControl);
    }

    [Fact]
    public void TcpOk_UnsupportedByVerifier_ClassifiesAsUntested_NeverBlocked()
    {
        // E2E guardrail: an AWG/xhttp server on a core that can't verify it must read
        // as "protocol untested" — never as ProtocolHandshakeBlockedLikely.
        var phases = ServerHealthPhaseMapper.Merge(
            ServerHealthPhaseMapper.FromQuickProbe(ServerProbeStatus.Ok),
            ServerHealthPhaseMapper.FromDeepVerify(
                DeepVerifyResult.Failed("deep verify: xhttp needs the lx core (with_xhttp)",
                    DeepVerifyFailurePhase.UnsupportedByVerifier)));
        var verdict = ServerHealthClassifier.Classify(phases).Verdict;
        Assert.Equal(ServerHealthVerdict.TcpOpenProtocolUntested, verdict);
        Assert.NotEqual(ServerHealthVerdict.ProtocolHandshakeBlockedLikely, verdict);
    }

    // ── R4: blocked-target canary rides the deep-verify result ──────────────

    [Fact]
    public void FromDeepVerify_OkWithCanaryFail_YieldsOnlyControlWorks()
    {
        var phases = ServerHealthPhaseMapper.FromDeepVerify(
            new DeepVerifyResult(true, 120, null, null, BlockedCanary: PhaseOutcome.Fail));
        Assert.Equal(PhaseOutcome.Pass, phases.ProxiedHttpControl);
        Assert.Equal(PhaseOutcome.Fail, phases.BlockedTargetCanary);

        // The audit's key case end-to-end: tunnel up, blocked service still dark.
        Assert.Equal(ServerHealthVerdict.OnlyControlWorks,
            ServerHealthClassifier.Classify(phases).Verdict);
    }

    [Fact]
    public void FromDeepVerify_OkWithoutCanary_StaysHealthy_BackCompat()
    {
        // Old results / skipped canaries (Unknown default) must not change the verdict.
        var phases = ServerHealthPhaseMapper.FromDeepVerify(new DeepVerifyResult(true, 120, null, null));
        Assert.Equal(PhaseOutcome.Unknown, phases.BlockedTargetCanary);
        Assert.Equal(ServerHealthVerdict.Healthy, ServerHealthClassifier.Classify(phases).Verdict);
    }

    // ── Merge ───────────────────────────────────────────────────────────────

    [Fact]
    public void Merge_LaterNonUnknownWins_UnionsPhases()
    {
        var quick = ServerHealthPhaseMapper.FromQuickProbe(ServerProbeStatus.Ok);           // TcpConnect=Pass
        var deep = ServerHealthPhaseMapper.FromDeepVerify(DeepVerifyResult.Failed("timeout")); // ProxiedHttp=Fail
        var merged = ServerHealthPhaseMapper.Merge(quick, deep);
        Assert.Equal(PhaseOutcome.Pass, merged.TcpConnect);
        Assert.Equal(PhaseOutcome.Fail, merged.ProxiedHttpControl);
    }

    // ── End-to-end through the classifier (the whole point of the bridge) ────

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
        // GUARDRAIL: a reachable host whose deep verify failed only because OUR sing-box
        // couldn't start must NOT be condemned as protocol-blocked — it stays "protocol untested".
        var phases = ServerHealthPhaseMapper.Merge(
            ServerHealthPhaseMapper.FromQuickProbe(ServerProbeStatus.Ok),
            ServerHealthPhaseMapper.FromDeepVerify(DeepVerifyResult.Failed("sing-box spawn failed")));
        var verdict = ServerHealthClassifier.Classify(phases).Verdict;
        Assert.Equal(ServerHealthVerdict.TcpOpenProtocolUntested, verdict);
        Assert.NotEqual(ServerHealthVerdict.ProtocolHandshakeBlockedLikely, verdict);
    }
}
