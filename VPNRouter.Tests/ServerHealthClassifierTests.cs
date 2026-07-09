using System.Linq;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Pins the phased server-health classifier against the audit's regression cases
/// (plans/audit-import-2026-07-09/01-audit-vector-map-batch1.md — RU-ASN/TSPU block +
/// blocked-target canary sections) and the ADR rule table
/// (plans/adr-urltest-verification-2026-07-09.md). Pure, no network.
/// </summary>
public class ServerHealthClassifierTests
{
    private static ServerHealthVerdict Verdict(ServerHealthPhases p)
        => ServerHealthClassifier.Classify(p).Verdict;

    // ── Host-level ──────────────────────────────────────────────────────────

    [Fact]
    public void DnsFail_IsHostUnreachable()
        => Assert.Equal(ServerHealthVerdict.HostUnreachable,
            Verdict(new ServerHealthPhases(Dns: PhaseOutcome.Fail)));

    [Fact]
    public void TcpFail_IsHostUnreachable()
        => Assert.Equal(ServerHealthVerdict.HostUnreachable,
            Verdict(new ServerHealthPhases(Dns: PhaseOutcome.Pass, TcpConnect: PhaseOutcome.Fail)));

    [Fact]
    public void NothingRan_IsUnknown()
        => Assert.Equal(ServerHealthVerdict.Unknown, Verdict(new ServerHealthPhases()));

    // ── Audit RU-block regression 1 & 2: TCP alive but protocol does not carry ──

    [Fact]
    public void TcpOk_HandshakeFail_IsProtocolBlockedLikely_NotHealthy()
    {
        // Audit test 1: TCP OK + DeepVerify handshake error => ProtocolHandshakeBlockedLikely, not Ok.
        var v = Verdict(new ServerHealthPhases(
            TcpConnect: PhaseOutcome.Pass, ProxyHandshake: PhaseOutcome.Fail));
        Assert.Equal(ServerHealthVerdict.ProtocolHandshakeBlockedLikely, v);
        Assert.NotEqual(ServerHealthVerdict.Healthy, v);
    }

    [Fact]
    public void TcpOk_ProxiedHttpFail_NoHandshakePhase_IsProtocolBlockedLikely()
    {
        // Audit test 2: TCP OK (+ SSH OK, host reachable) but proxied HTTP fails. The common
        // deep-verify shape doesn't isolate a handshake phase, so TCP-reachable + HTTP-fail
        // must still read as a likely protocol/subnet block, not Healthy/TcpOpen.
        var v = Verdict(new ServerHealthPhases(
            TcpConnect: PhaseOutcome.Pass, ProxiedHttpControl: PhaseOutcome.Fail));
        Assert.Equal(ServerHealthVerdict.ProtocolHandshakeBlockedLikely, v);
    }

    [Fact]
    public void TcpOk_TlsCamouflageFail_IsProtocolBlockedLikely()
        => Assert.Equal(ServerHealthVerdict.ProtocolHandshakeBlockedLikely,
            Verdict(new ServerHealthPhases(TcpConnect: PhaseOutcome.Pass, TlsCamouflage: PhaseOutcome.Fail)));

    [Fact]
    public void TcpOk_ProxyHandshakeOk_HttpFail_IsProxyStartedButHttpFailed()
    {
        // Handshake explicitly succeeded → distinct softer verdict (mid-stream break), not "blocked".
        var v = Verdict(new ServerHealthPhases(
            TcpConnect: PhaseOutcome.Pass,
            ProxyHandshake: PhaseOutcome.Pass,
            ProxiedHttpControl: PhaseOutcome.Fail));
        Assert.Equal(ServerHealthVerdict.ProxyStartedButHttpFailed, v);
    }

    [Fact]
    public void TcpOnly_NothingDeeper_IsTcpOpenProtocolUntested_NotHealthy()
    {
        var v = Verdict(new ServerHealthPhases(Dns: PhaseOutcome.Pass, TcpConnect: PhaseOutcome.Pass));
        Assert.Equal(ServerHealthVerdict.TcpOpenProtocolUntested, v);
        Assert.NotEqual(ServerHealthVerdict.Healthy, v);
    }

    // ── Blocked-target canary regression ────────────────────────────────────

    [Fact]
    public void ControlOk_BlockedCanaryFail_IsOnlyControlWorks_NotHealthy()
    {
        // Canary test 1: Control OK + YouTube fail => OnlyControlWorks, not ConnectedOk.
        var v = Verdict(new ServerHealthPhases(
            TcpConnect: PhaseOutcome.Pass,
            ProxiedHttpControl: PhaseOutcome.Pass,
            BlockedTargetCanary: PhaseOutcome.Fail));
        Assert.Equal(ServerHealthVerdict.OnlyControlWorks, v);
        Assert.NotEqual(ServerHealthVerdict.Healthy, v);
    }

    [Fact]
    public void ControlOk_BlockedCanaryOk_IsHealthy()
        => Assert.Equal(ServerHealthVerdict.Healthy,
            Verdict(new ServerHealthPhases(
                TcpConnect: PhaseOutcome.Pass,
                ProxiedHttpControl: PhaseOutcome.Pass,
                BlockedTargetCanary: PhaseOutcome.Pass)));

    [Fact]
    public void ControlOk_UdpAppFail_IsUdpOrAppProfileFailed()
        => Assert.Equal(ServerHealthVerdict.UdpOrAppProfileFailed,
            Verdict(new ServerHealthPhases(
                TcpConnect: PhaseOutcome.Pass,
                ProxiedHttpControl: PhaseOutcome.Pass,
                UdpAppProfile: PhaseOutcome.Fail)));

    [Fact]
    public void ControlOk_NoCanary_IsHealthy()
        => Assert.Equal(ServerHealthVerdict.Healthy,
            Verdict(new ServerHealthPhases(
                TcpConnect: PhaseOutcome.Pass, ProxiedHttpControl: PhaseOutcome.Pass)));

    // ── Provider/ASN grouping (audit regression 3 & 4) ──────────────────────

    [Fact]
    public void SameAsn_MultipleBlocked_OtherAsnHealthy_FlagsProviderHighRisk()
    {
        // Audit test 3: many servers in same ASN/prefix fail at protocol phase => grouped warning.
        var results = new (string, ServerHealthVerdict)[]
        {
            ("AS-HighRisk", ServerHealthVerdict.ProtocolHandshakeBlockedLikely),
            ("AS-HighRisk", ServerHealthVerdict.ProtocolHandshakeBlockedLikely),
            ("AS-Good",     ServerHealthVerdict.Healthy),
        };
        var risks = ServerHealthClassifier.AnalyzeProviderRisk(results);
        Assert.True(risks.Single(r => r.Asn == "AS-HighRisk").HighRisk);
        Assert.False(risks.Single(r => r.Asn == "AS-Good").HighRisk);
    }

    [Fact]
    public void AllAsnsBlocked_NoHealthyElsewhere_DoesNotFlagHighRisk()
    {
        // Audit test 4: one server fails but nothing else proves the client works =>
        // do NOT condemn a subnet; the whole client path may just be down.
        var results = new (string, ServerHealthVerdict)[]
        {
            ("AS-A", ServerHealthVerdict.ProtocolHandshakeBlockedLikely),
            ("AS-A", ServerHealthVerdict.ProtocolHandshakeBlockedLikely),
            ("AS-B", ServerHealthVerdict.ProtocolHandshakeBlockedLikely),
        };
        var risks = ServerHealthClassifier.AnalyzeProviderRisk(results);
        Assert.All(risks, r => Assert.False(r.HighRisk));
    }

    [Fact]
    public void SingleBlockedInAsn_BelowThreshold_NotHighRisk()
    {
        var results = new (string, ServerHealthVerdict)[]
        {
            ("AS-A", ServerHealthVerdict.ProtocolHandshakeBlockedLikely),
            ("AS-B", ServerHealthVerdict.Healthy),
        };
        Assert.False(ServerHealthClassifier.AnalyzeProviderRisk(results).Single(r => r.Asn == "AS-A").HighRisk);
    }

    [Fact]
    public void ProviderRisk_IgnoresBlankAsn()
    {
        var results = new (string, ServerHealthVerdict)[]
        {
            ("", ServerHealthVerdict.ProtocolHandshakeBlockedLikely),
            ("  ", ServerHealthVerdict.ProtocolHandshakeBlockedLikely),
        };
        Assert.Empty(ServerHealthClassifier.AnalyzeProviderRisk(results));
    }
}
