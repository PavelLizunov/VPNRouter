using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static partial class ConfigGenerator
{
    private static List<SingBoxOutbound> BuildOutbounds(AppSettings settings, out bool hasUdpProxy,
        out bool isDnsTunnel, out List<string> dnsTunnelResolverIps,
        out List<SingBoxEndpoint>? endpoints,
        out bool proxyIsUdpNativeOutbound,
        Func<VlessServerEntry, bool>? isServerAlive = null)
    {
        endpoints = null;
        proxyIsUdpNativeOutbound = false;
        var servers = settings.Vless.GetActiveServers();
        var hasRequestedChain = servers.Any(s => !string.IsNullOrEmpty(s.DetourVia));

        if (!ServerUriParser.NaiveRuntimeAvailable)
            servers = servers.Where(s =>
                !"naive".Equals(s.Protocol, StringComparison.OrdinalIgnoreCase)).ToList();

        if (!SingBoxFeatures.AwgAvailable)
            servers = servers.Where(s =>
                !"amneziawg".Equals(s.Protocol, StringComparison.OrdinalIgnoreCase)
                && !"awg".Equals(s.Protocol, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!SingBoxFeatures.XhttpAvailable)
            servers = servers.Where(s =>
                !"xhttp".Equals(s.Transport?.Type, StringComparison.OrdinalIgnoreCase)).ToList();

        if (settings.Vless.AutoSelectBestServer && servers.Count > 1 && !hasRequestedChain)
        {
            var records = servers
                .Select(s => (Server: s, Rec: ServerHealthStore.GetFreshRecord(s)))
                .ToList();

            var kept = records
                .Where(r => r.Rec?.Verdict != ServerHealthVerdict.ProtocolHandshakeBlockedLikely)
                .Select(r => r.Server).ToList();
            if (kept.Count >= 1 && kept.Count < servers.Count)
                servers = kept;

            var grouped = records
                .Where(r => !string.IsNullOrEmpty(r.Rec?.ProviderKey))
                .Select(r => (r.Rec!.ProviderKey!, r.Rec.Verdict));
            var highRisk = ServerHealthClassifier.AnalyzeProviderRisk(grouped)
                .Where(p => p.HighRisk)
                .Select(p => p.Asn)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (highRisk.Count > 0)
            {
                var survivors = servers.Where(s =>
                {
                    var rec = records.FirstOrDefault(r => ReferenceEquals(r.Server, s)).Rec;
                    return rec?.ProviderKey is null || !highRisk.Contains(rec.ProviderKey);
                }).ToList();
                if (survivors.Count >= 1 && survivors.Count < servers.Count)
                    servers = survivors;
            }
        }

        if (servers.Count == 0)
        {
            throw new InvalidOperationException(
                "ConfigGenerator: no active VLESS servers — refusing to generate sing-box config " +
                "with route rules pointing at a missing 'proxy' outbound. " +
                "Caller must populate settings.Vless.Servers (via VlessServersResolver.Resolve) " +
                "before calling Generate(). " +
                "See plans/vpnrouter-v2.28-flow-mismatch.md for context.");
        }

        var chainedTargets = servers.Where(s => !string.IsNullOrEmpty(s.DetourVia)).ToList();
        if (hasRequestedChain && chainedTargets.Count == 0)
        {
            throw new InvalidOperationException(
                "ConfigGenerator: the selected chained target is unavailable on this build — refusing a direct fallback.");
        }

        if (chainedTargets.Count > 0)
        {
            if (chainedTargets.Count != 1)
            {
                throw new InvalidOperationException(
                    "ConfigGenerator: expected exactly one chained target in active servers.");
            }

            var target = chainedTargets[0];
            var targetProto = (target.Protocol ?? "vless").ToLowerInvariant();
            if (targetProto != "vless" || "xhttp".Equals(target.Transport?.Type, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "ConfigGenerator: chained target uses unsupported protocol/transport — only VLESS is supported.");
            }

            var upstreams = servers.Where(s =>
                string.IsNullOrEmpty(s.DetourVia) &&
                !string.IsNullOrEmpty(s.OutboundId) &&
                string.Equals(s.OutboundId, target.DetourVia, StringComparison.OrdinalIgnoreCase)).ToList();

            if (upstreams.Count != 1)
            {
                throw new InvalidOperationException(
                    "ConfigGenerator: chained target references an absent or non-unique upstream in active servers.");
            }

            var upstream = upstreams[0];
            var upstreamProto = (upstream.Protocol ?? "vless").ToLowerInvariant();
            if (upstreamProto != "vless" || "xhttp".Equals(upstream.Transport?.Type, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "ConfigGenerator: chained upstream uses unsupported protocol/transport — only VLESS is supported.");
            }

            var upstreamOutbound = BuildVlessOutbound(upstream, "chain-entry");
            var targetOutbound = BuildVlessOutbound(target, "proxy");
            targetOutbound.Detour = "chain-entry";

            hasUdpProxy = false;
            isDnsTunnel = false;
            dnsTunnelResolverIps = new List<string>();
            endpoints = null;
            proxyIsUdpNativeOutbound = false;

            return new List<SingBoxOutbound>
            {
                targetOutbound,
                upstreamOutbound,
                new SingBoxOutbound { Type = "direct", Tag = "direct" },
                new SingBoxOutbound { Type = "direct", Tag = "dns-direct", UdpFragment = true },
            };
        }

        var awgActiveName = settings.Vless.ActiveServer;
        var awgActiveEntry = !string.IsNullOrEmpty(awgActiveName)
            ? servers.FirstOrDefault(s =>
                string.Equals(s.Name, awgActiveName, StringComparison.OrdinalIgnoreCase))
            : null;
        awgActiveEntry ??= servers.FirstOrDefault();
        var awgActive = (awgActiveEntry != null
            && ("amneziawg".Equals(awgActiveEntry.Protocol, StringComparison.OrdinalIgnoreCase)
                || "awg".Equals(awgActiveEntry.Protocol, StringComparison.OrdinalIgnoreCase)))
            ? awgActiveEntry : null;
        if (awgActive != null)
        {
            endpoints = new List<SingBoxEndpoint> { BuildAmneziaWgEndpoint(awgActive, "proxy") };
            hasUdpProxy = false;
            isDnsTunnel = false;
            dnsTunnelResolverIps = new List<string>();
            return new List<SingBoxOutbound>
            {
                new() { Type = "direct", Tag = "direct" },
                new() { Type = "direct", Tag = "dns-direct", UdpFragment = true },
            };
        }

        servers = servers.Where(s =>
            !"amneziawg".Equals(s.Protocol, StringComparison.OrdinalIgnoreCase)
            && !"awg".Equals(s.Protocol, StringComparison.OrdinalIgnoreCase)).ToList();

        var dnsTunnelEntry = servers.FirstOrDefault(s => s.IsDnsTunnel);
        isDnsTunnel = dnsTunnelEntry != null;
        dnsTunnelResolverIps = isDnsTunnel
            ? ExtractResolverIps(
                (dnsTunnelEntry!.DnsResolvers ?? new List<string>())
                .Concat(dnsTunnelEntry.DnsAuthoritative ?? new List<string>()))
            : new List<string>();

        var outbounds = new List<SingBoxOutbound>();

        var udpSibling = FindNaiveUdpSibling(servers, settings.Vless.Servers, isServerAlive);
        var tcpNaiveServers = udpSibling != null
            ? servers.Where(NaivePairing.IsNaive).ToList()
            : new List<VlessServerEntry>();
        var udpNativeActiveName = settings.Vless.ActiveServer;
        var udpNativeActiveEntry = !string.IsNullOrEmpty(udpNativeActiveName)
            ? servers.FirstOrDefault(s =>
                string.Equals(s.Name, udpNativeActiveName, StringComparison.OrdinalIgnoreCase))
            : servers.FirstOrDefault();
        var udpNativeActive = (udpNativeActiveEntry != null
            && ("hysteria2".Equals(udpNativeActiveEntry.Protocol, StringComparison.OrdinalIgnoreCase)
                || "tuic".Equals(udpNativeActiveEntry.Protocol, StringComparison.OrdinalIgnoreCase)))
            ? udpNativeActiveEntry : null;

        if (udpNativeActive != null)
        {
            AddOutboundGroup(outbounds, new List<VlessServerEntry> { udpNativeActive }, "proxy", "vless");
            hasUdpProxy = false;
            proxyIsUdpNativeOutbound = true;
        }
        else if (udpSibling != null && tcpNaiveServers.Count > 0)
        {
            AddOutboundGroup(outbounds, tcpNaiveServers, "proxy", "vless");
            AddOutboundGroup(outbounds, new List<VlessServerEntry> { udpSibling }, "proxy-udp", "vless-udp");
            hasUdpProxy = true;
        }
        else
        {
            var flowServers = servers.Where(s => !string.IsNullOrEmpty(s.Flow)).ToList();
            var noFlowServers = servers.Where(s => string.IsNullOrEmpty(s.Flow)).ToList();
            if (isServerAlive != null)
                noFlowServers = noFlowServers.Where(isServerAlive).ToList();
            hasUdpProxy = flowServers.Count > 0 && noFlowServers.Count > 0;

            if (hasUdpProxy)
            {
                AddOutboundGroup(outbounds, flowServers, "proxy", "vless");
                AddOutboundGroup(outbounds, noFlowServers, "proxy-udp", "vless-udp");
            }
            else
            {
                AddOutboundGroup(outbounds, servers, "proxy", "vless");
            }
        }

        outbounds.Add(new SingBoxOutbound { Type = "direct", Tag = "direct" });
        outbounds.Add(new SingBoxOutbound { Type = "direct", Tag = "dns-direct", UdpFragment = true });
        return outbounds;
    }

}
