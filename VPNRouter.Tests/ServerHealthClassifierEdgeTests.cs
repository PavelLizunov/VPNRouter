using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Unit-4 edge pins for the phased server-health model (audit regression lists in
/// plans/audit-import-2026-07-09/01-audit-vector-map-batch1.md + the ADR amendment in
/// plans/adr-urltest-verification-2026-07-09.md): PhaseOutcome.Skipped handling,
/// UDP-native transports with no TCP phase (Hysteria2/TUIC quick probe returns
/// SkippedNotApplicable), blocked-target-canary + UDP-app precedence, the canary
/// partial-pass rule, and the direct-probe safe default. Pure, no network.
/// </summary>
public class ServerHealthClassifierEdgeTests
{
    private static ServerHealthVerdict Verdict(ServerHealthPhases p)
        => ServerHealthClassifier.Classify(p).Verdict;

    // ── UDP-native transports: no TCP phase, deep verify is the real answer ──

    [Theory]
    [InlineData(PhaseOutcome.Skipped)]   // quick TCP probe not applicable (Hy2/TUIC pure UDP)
    [InlineData(PhaseOutcome.Unknown)]   // quick probe never ran (deep-verify-only flow)
    public void NoTcpPhase_DeepVerifyPass_IsHealthy(PhaseOutcome tcp)
    {
        // The dual of "TCP alone never reads as Healthy": a superficial phase that never
        // ran / doesn't apply must not veto a real end-to-end proxied-HTTP pass.
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
        // No host-liveness pivot: a UDP-native server whose deep verify failed could be
        // down OR blocked — never claim ProtocolHandshakeBlockedLikely without TCP evidence.
        var v = Verdict(new ServerHealthPhases(
            TcpConnect: PhaseOutcome.Skipped, ProxiedHttpControl: PhaseOutcome.Fail));
        Assert.Equal(ServerHealthVerdict.Unknown, v);
    }

    [Fact]
    public void TcpFail_ButDeepVerifyPass_ContradictoryPhases_HostLevelWins()
    {
        // Characterization: contradictory/stale data (TCP failed yet proxied HTTP "passed")
        // reads conservative — host-level failure fires first.
        var v = Verdict(new ServerHealthPhases(
            TcpConnect: PhaseOutcome.Fail, ProxiedHttpControl: PhaseOutcome.Pass));
        Assert.Equal(ServerHealthVerdict.HostUnreachable, v);
    }

    // ── Skipped means "not applicable": never a fail, never a fabricated pass ──

    [Fact]
    public void TlsSkipped_TcpPass_HttpPass_IsHealthy()
        // e.g. Shadowsocks — no TLS/camouflage layer; Skipped must not read as Fail.
        => Assert.Equal(ServerHealthVerdict.Healthy,
            Verdict(new ServerHealthPhases(
                TcpConnect: PhaseOutcome.Pass,
                TlsCamouflage: PhaseOutcome.Skipped,
                ProxiedHttpControl: PhaseOutcome.Pass)));

    [Fact]
    public void TlsSkipped_TcpOnly_IsStillTcpOpenProtocolUntested()
        // Skipped must not read as Pass either — nothing deeper ran, protocol unproven.
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

    // ── Canary + UDP-app combinations (precedence) ──────────────────────────

    [Fact]
    public void CanaryFail_AndUdpFail_CanaryVerdictWins()
        // Bypass-unproven is the stronger user-facing signal than a broken app profile.
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

    // ── Hy2/TUIC end-to-end through the mapper (the UDP-native story) ────────

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

    // ── Canary partial-pass (audit canary regression 3) + safe default (reg. 5) ──

    [Fact]
    public void CanaryEvaluate_MixedFreshResults_IsPassButAmbiguous_NotCleanGlobalOk()
    {
        // YouTube OK + a fresh less-popular blocked target failing => partial/ambiguous.
        var agg = CanaryPolicy.Evaluate(true, new[] { (true, false), (false, false) });
        Assert.Equal(PhaseOutcome.Pass, agg.BlockedTargetCanary);
        Assert.True(agg.StaleOrAmbiguous);
    }

    [Fact]
    public void CanaryEvaluate_AllFreshPassed_IsCleanPass()
    {
        // Two blocked-target OKs => the strong, unambiguous bypass-proven case.
        var agg = CanaryPolicy.Evaluate(true, new[] { (true, false), (true, false) });
        Assert.Equal(PhaseOutcome.Pass, agg.BlockedTargetCanary);
        Assert.False(agg.StaleOrAmbiguous);
    }

    [Fact]
    public void DirectBlockedTargetProbes_AreOffByDefault()
        // Audit safe default: direct probes can reveal user intent to the ISP.
        => Assert.False(CanaryPolicy.DirectProbesDefaultEnabled);
}
