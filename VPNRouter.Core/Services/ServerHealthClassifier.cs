#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace VPNRouter.Core.Services;

public enum PhaseOutcome
{
    Unknown = 0,
    Pass,
    Fail,
    Skipped,
}

public sealed record ServerHealthPhases(
    PhaseOutcome Dns = PhaseOutcome.Unknown,
    PhaseOutcome TcpConnect = PhaseOutcome.Unknown,
    PhaseOutcome TlsCamouflage = PhaseOutcome.Unknown,
    PhaseOutcome ProxyHandshake = PhaseOutcome.Unknown,
    PhaseOutcome ProxiedHttpControl = PhaseOutcome.Unknown,
    PhaseOutcome BlockedTargetCanary = PhaseOutcome.Unknown,
    PhaseOutcome UdpAppProfile = PhaseOutcome.Unknown);

public enum ServerHealthVerdict
{
    Unknown = 0,

    Healthy,

    HostUnreachable,

    TcpOpenProtocolUntested,

    ProtocolHandshakeBlockedLikely,

    ProxyStartedButHttpFailed,

    OnlyControlWorks,

    UdpOrAppProfileFailed,
}

public sealed record ServerHealthResult(ServerHealthVerdict Verdict, ServerHealthPhases Phases, string Reason);

public sealed record ProviderRisk(string Asn, bool HighRisk, int BlockedLikelyCount, string Reason);

public static class ServerHealthClassifier
{
    public const int ProviderHighRiskThreshold = 2;

    public static ServerHealthResult Classify(ServerHealthPhases p)
    {
        if (p is null) throw new ArgumentNullException(nameof(p));

        if (p.Dns == PhaseOutcome.Fail)
            return new(ServerHealthVerdict.HostUnreachable, p, "DNS resolution failed");
        if (p.TcpConnect == PhaseOutcome.Fail)
            return new(ServerHealthVerdict.HostUnreachable, p, "TCP connect failed");

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

        if (p.TcpConnect == PhaseOutcome.Pass)
        {
            bool handshakeFailed = p.TlsCamouflage == PhaseOutcome.Fail
                                || p.ProxyHandshake == PhaseOutcome.Fail;
            if (handshakeFailed)
                return new(ServerHealthVerdict.ProtocolHandshakeBlockedLikely,
                    p, "host reachable at TCP but the VPN protocol handshake failed");

            if (p.ProxiedHttpControl == PhaseOutcome.Fail)
            {
                if (p.ProxyHandshake == PhaseOutcome.Pass)
                    return new(ServerHealthVerdict.ProxyStartedButHttpFailed,
                        p, "proxy handshake ok but proxied control HTTP failed mid-stream");
                return new(ServerHealthVerdict.ProtocolHandshakeBlockedLikely,
                    p, "host reachable at TCP but proxied HTTP did not pass — protocol/subnet block likely");
            }

            return new(ServerHealthVerdict.TcpOpenProtocolUntested,
                p, "TCP reachable; VPN protocol not yet verified");
        }

        return new(ServerHealthVerdict.Unknown, p, "not enough phases ran");
    }

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
