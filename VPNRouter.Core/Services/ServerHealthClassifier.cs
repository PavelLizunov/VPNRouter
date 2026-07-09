#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace VPNRouter.Core.Services;

/// <summary>
/// Outcome of a single verification phase. A phase that never ran is
/// <see cref="Unknown"/> — it is NEVER promoted to <see cref="Pass"/>. That is the
/// whole point of this model: we stop turning "ping/TCP works" into "server works".
/// </summary>
public enum PhaseOutcome
{
    Unknown = 0,
    Pass,
    Fail,
    /// <summary>Not applicable to this protocol/scenario (e.g. no TLS layer on Shadowsocks).</summary>
    Skipped,
}

/// <summary>
/// The phases observed for one server, from cheapest/most-superficial (DNS, TCP) to the
/// real end-to-end signals (proxied control HTTP, blocked-target canary, UDP/app profile).
/// All default to <see cref="PhaseOutcome.Unknown"/> — a probe that did not run leaves its
/// phase <see cref="PhaseOutcome.Unknown"/>, never fabricated.
/// </summary>
public sealed record ServerHealthPhases(
    PhaseOutcome Dns = PhaseOutcome.Unknown,
    PhaseOutcome TcpConnect = PhaseOutcome.Unknown,
    PhaseOutcome TlsCamouflage = PhaseOutcome.Unknown,
    PhaseOutcome ProxyHandshake = PhaseOutcome.Unknown,
    PhaseOutcome ProxiedHttpControl = PhaseOutcome.Unknown,
    PhaseOutcome BlockedTargetCanary = PhaseOutcome.Unknown,
    PhaseOutcome UdpAppProfile = PhaseOutcome.Unknown);

/// <summary>
/// Single classified server-health verdict. See the audit vector map
/// (<c>plans/audit-import-2026-07-09/01-audit-vector-map-batch1.md</c>, RU-ASN/TSPU +
/// blocked-target-canary sections) and <c>plans/adr-urltest-verification-2026-07-09.md</c>.
/// </summary>
public enum ServerHealthVerdict
{
    /// <summary>Not enough phases ran to say anything.</summary>
    Unknown = 0,

    /// <summary>Proxied control HTTP works (and blocked-target canary, if tested). Usable.</summary>
    Healthy,

    /// <summary>DNS or TCP failed — the host itself is not reachable.</summary>
    HostUnreachable,

    /// <summary>TCP connects but nothing deeper ran. NOT "healthy" — the VPN protocol is unproven.</summary>
    TcpOpenProtocolUntested,

    /// <summary>
    /// TCP reaches the host but the VPN protocol does not carry traffic (TLS/proxy handshake
    /// or proxied HTTP failed). The RU-ASN/TSPU signal: ping/SSH/TCP alive, VLESS/Reality/AWG/HY2 blocked.
    /// </summary>
    ProtocolHandshakeBlockedLikely,

    /// <summary>The proxy handshake completed but proxied control HTTP still failed (mid-stream break).</summary>
    ProxyStartedButHttpFailed,

    /// <summary>Proxied control HTTP works but a blocked-target canary failed — tunnel up, censorship-bypass unproven.</summary>
    OnlyControlWorks,

    /// <summary>Proxied control HTTP works but the UDP/app-profile probe failed (games/voice path broken).</summary>
    UdpOrAppProfileFailed,
}

/// <summary>Result of classifying one server's phase outcomes.</summary>
public sealed record ServerHealthResult(ServerHealthVerdict Verdict, ServerHealthPhases Phases, string Reason);

/// <summary>Per-ASN/provider risk conclusion from grouped analysis.</summary>
public sealed record ProviderRisk(string Asn, bool HighRisk, int BlockedLikelyCount, string Reason);

/// <summary>
/// Pure, network-free classifier that turns observed per-phase probe outcomes into a
/// server-health verdict, plus a grouped provider/ASN risk analysis. All decision logic
/// lives here (zero I/O), mirroring <see cref="SplitTunnelPolicy"/> /
/// <see cref="ConnectionHealthClassifier"/> so it is golden-tested on CI with no network,
/// no sing-box, no platform code.
///
/// <para>The probes that PRODUCE these outcomes (quick TCP/TLS/UDP, deep sing-box HTTP,
/// blocked-target canary, ASN lookup) stay in their own services; they feed this core.
/// It takes primitives only — never a subscription URL or secret — so redaction stays
/// the callers' job via <c>DiagnosticsRedactor</c>.</para>
/// </summary>
public static class ServerHealthClassifier
{
    /// <summary>ASNs need at least this many TCP-reachable-but-protocol-blocked servers to be flagged HighRisk.</summary>
    public const int ProviderHighRiskThreshold = 2;

    /// <summary>
    /// Classify one server from its phase outcomes. First matching rule wins; TCP-alive is
    /// the pivot for the protocol-block signal. See the ADR for the rule table.
    /// </summary>
    public static ServerHealthResult Classify(ServerHealthPhases p)
    {
        if (p is null) throw new ArgumentNullException(nameof(p));

        // 1. Host-level failure.
        if (p.Dns == PhaseOutcome.Fail)
            return new(ServerHealthVerdict.HostUnreachable, p, "DNS resolution failed");
        if (p.TcpConnect == PhaseOutcome.Fail)
            return new(ServerHealthVerdict.HostUnreachable, p, "TCP connect failed");

        // 2/3/4 require TCP reachability as the pivot.
        if (p.TcpConnect == PhaseOutcome.Pass)
        {
            // 2. Proxied control HTTP works — distinguish healthy vs partial.
            if (p.ProxiedHttpControl == PhaseOutcome.Pass)
            {
                if (p.BlockedTargetCanary == PhaseOutcome.Fail)
                    return new(ServerHealthVerdict.OnlyControlWorks,
                        p, "tunnel up but a blocked-target canary failed — censorship-bypass unproven");
                if (p.UdpAppProfile == PhaseOutcome.Fail)
                    return new(ServerHealthVerdict.UdpOrAppProfileFailed,
                        p, "proxied HTTP ok but the UDP/app-profile probe failed");
                return new(ServerHealthVerdict.Healthy, p, "proxied control HTTP ok");
            }

            // 3. Handshake / proxied-HTTP failure on a TCP-reachable host = protocol block likely.
            //    A handshake failure is a stronger signal than a mid-stream HTTP break, so a
            //    clean proxy handshake + failed HTTP is reported as a distinct softer verdict.
            bool handshakeFailed = p.TlsCamouflage == PhaseOutcome.Fail
                                || p.ProxyHandshake == PhaseOutcome.Fail;
            if (handshakeFailed)
                return new(ServerHealthVerdict.ProtocolHandshakeBlockedLikely,
                    p, "host reachable at TCP but the VPN protocol handshake failed");

            if (p.ProxiedHttpControl == PhaseOutcome.Fail)
            {
                // Proxy handshake explicitly succeeded, HTTP still failed → mid-stream break.
                if (p.ProxyHandshake == PhaseOutcome.Pass)
                    return new(ServerHealthVerdict.ProxyStartedButHttpFailed,
                        p, "proxy handshake ok but proxied control HTTP failed mid-stream");
                // Handshake not separately observed (the common deep-verify shape): a
                // TCP-reachable host whose proxied HTTP fails is a likely protocol/subnet block.
                return new(ServerHealthVerdict.ProtocolHandshakeBlockedLikely,
                    p, "host reachable at TCP but proxied HTTP did not pass — protocol/subnet block likely");
            }

            // 4. TCP only, nothing deeper ran.
            return new(ServerHealthVerdict.TcpOpenProtocolUntested,
                p, "TCP reachable; VPN protocol not yet verified");
        }

        return new(ServerHealthVerdict.Unknown, p, "not enough phases ran");
    }

    /// <summary>
    /// Grouped analysis: flag an ASN as HighRisk when at least
    /// <see cref="ProviderHighRiskThreshold"/> of its servers are
    /// <see cref="ServerHealthVerdict.ProtocolHandshakeBlockedLikely"/> (TCP-reachable but the
    /// protocol is blocked) AND at least one server on a DIFFERENT ASN is
    /// <see cref="ServerHealthVerdict.Healthy"/> for the same client — so the failure is
    /// provider/subnet-specific, not a host-wide/client-wide outage. Ordinal ASN keys.
    /// </summary>
    public static IReadOnlyList<ProviderRisk> AnalyzeProviderRisk(
        IEnumerable<(string Asn, ServerHealthVerdict Verdict)> results)
    {
        if (results is null) throw new ArgumentNullException(nameof(results));

        var byAsn = new Dictionary<string, List<ServerHealthVerdict>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (asn, verdict) in results)
        {
            if (string.IsNullOrWhiteSpace(asn)) continue;
            if (!byAsn.TryGetValue(asn, out var list)) byAsn[asn] = list = new List<ServerHealthVerdict>();
            list.Add(verdict);
        }

        // "Some other ASN clearly works" — required so we don't flag a subnet when the whole
        // client path is just down.
        bool anyOtherHealthy(string asn) => byAsn
            .Where(kv => !string.Equals(kv.Key, asn, StringComparison.OrdinalIgnoreCase))
            .Any(kv => kv.Value.Contains(ServerHealthVerdict.Healthy));

        var risks = new List<ProviderRisk>();
        foreach (var (asn, verdicts) in byAsn)
        {
            int blocked = verdicts.Count(v => v == ServerHealthVerdict.ProtocolHandshakeBlockedLikely);
            bool highRisk = blocked >= ProviderHighRiskThreshold && anyOtherHealthy(asn);
            var reason = highRisk
                ? $"{blocked} servers on {asn} are TCP-reachable but protocol-blocked while another ASN works"
                : $"{blocked} protocol-blocked server(s) on {asn}";
            risks.Add(new ProviderRisk(asn, highRisk, blocked, reason));
        }
        return risks;
    }
}
