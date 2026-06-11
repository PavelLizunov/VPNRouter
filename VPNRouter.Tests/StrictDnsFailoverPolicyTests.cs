// v2.42.0 — StrictDns "all DNS via tunnel" runtime failover decision matrix.
//
// StrictDns forces dns.final = vpn-dns (DoH through the proxy). When the proxy
// goes unreachable (dead/slow server — the germany "endless loading / no
// internet" report, 2026-06-11), every DNS query hangs on that dead path with
// no fallback. The reconcile suppresses StrictDns (dns.final → local-dns, DoH on
// the real NIC) while the proxy is unreachable and re-arms on recovery — but
// ONLY in split/include mode where StrictDns is the sole driver of vpn-dns
// (full-tunnel / exclude legitimately route ALL traffic + DNS through the
// tunnel and must never fail over).
//
// StrictDnsFailoverPolicy.Decide holds the whole decision so the transition
// matrix is unit-testable on any OS; the probe + config regen + hot-reload live
// in HealthMonitor.

#nullable enable

using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class StrictDnsFailoverPolicyTests
{
    // ---- 8-cell truth table: (strictDnsSoleDriver, proxyHealthy, failedOver) ----
    // suppress = soleDriver && !proxyHealthy
    //   suppress && !failedOver -> FailOpen
    //   !suppress && failedOver -> ReArm
    //   otherwise               -> None

    [Theory]
    // NOT the sole driver (full-tunnel / exclude / StrictDns off): never suppress.
    [InlineData(false, false, false, StrictDnsAction.None)]
    [InlineData(false, true,  false, StrictDnsAction.None)]
    [InlineData(false, false, true,  StrictDnsAction.ReArm)]  // restore a stale override
    [InlineData(false, true,  true,  StrictDnsAction.ReArm)]  // mode changed while failed over
    // sole driver: follow proxy reachability.
    [InlineData(true,  true,  false, StrictDnsAction.None)]   // healthy, armed -> stay
    [InlineData(true,  false, false, StrictDnsAction.FailOpen)] // proxy DIED -> fail open
    [InlineData(true,  true,  true,  StrictDnsAction.ReArm)]  // proxy back -> re-arm
    [InlineData(true,  false, true,  StrictDnsAction.None)]   // proxy still dead, already open
    public void Decide_FullTruthTable(bool soleDriver, bool proxyHealthy, bool failedOver, StrictDnsAction expected)
    {
        Assert.Equal(expected, StrictDnsFailoverPolicy.Decide(soleDriver, proxyHealthy, failedOver));
    }

    [Fact]
    public void ProxyUnreachable_WhileArmed_FailsOpen()
    {
        // germany endless-loading: StrictDns on, proxy went dead → suppress so
        // DNS resolves via the direct resolver instead of hanging on the tunnel.
        Assert.Equal(StrictDnsAction.FailOpen,
            StrictDnsFailoverPolicy.Decide(strictDnsSoleDriver: true, proxyHealthy: false, currentlyFailedOver: false));
    }

    [Fact]
    public void ProxyRecovers_ReArms()
    {
        Assert.Equal(StrictDnsAction.ReArm,
            StrictDnsFailoverPolicy.Decide(strictDnsSoleDriver: true, proxyHealthy: true, currentlyFailedOver: true));
    }

    [Fact]
    public void FullTunnel_NeverFailsOver()
    {
        // Caller passes soleDriver=false for full-tunnel/exclude — even with a
        // dead proxy we must NOT touch dns.final (all traffic + DNS rides the
        // tunnel by design; failing over would leak / break the user's intent).
        Assert.Equal(StrictDnsAction.None,
            StrictDnsFailoverPolicy.Decide(strictDnsSoleDriver: false, proxyHealthy: false, currentlyFailedOver: false));
    }

    [Fact]
    public void SteadyStates_AreNoOps()
    {
        // armed + healthy → None; open + still-dead → None (no firewall/reload thrash).
        Assert.Equal(StrictDnsAction.None,
            StrictDnsFailoverPolicy.Decide(true, true, false));
        Assert.Equal(StrictDnsAction.None,
            StrictDnsFailoverPolicy.Decide(true, false, true));
    }
}
