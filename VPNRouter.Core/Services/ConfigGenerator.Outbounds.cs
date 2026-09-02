using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static partial class ConfigGenerator
{
    // ─── Outbounds ────────────────────────────────────────────────────────────
    // sing-box 1.12+: removed "dns" and "block" outbound types
    // DNS hijacking is done via route rule action: "hijack-dns"
    // Blocking is done via route rule action: "reject"

    /// <summary>
    /// Build outbound list. Auto-detects UDP split:
    /// - If servers have BOTH flow and no-flow entries → dual outbound (TCP/UDP split)
    /// - Servers WITH flow → "proxy" (TCP, xtls-rprx-vision optimized)
    /// - Servers WITHOUT flow → "proxy-udp" (UDP, better for voice/video)
    /// - If all servers have same flow config → single "proxy" outbound
    /// </summary>
    private static List<SingBoxOutbound> BuildOutbounds(AppSettings settings, out bool hasUdpProxy,
        out bool isDnsTunnel, out List<string> dnsTunnelResolverIps,
        out List<SingBoxEndpoint>? endpoints,
        out bool proxyIsUdpNativeOutbound,
        Func<VlessServerEntry, bool>? isServerAlive = null)
    {
        endpoints = null; // default: no endpoints -> official-sing-box-compatible config
        // Set true only when the SOLE "proxy" outbound is a UDP-native transport
        // (Hy2/TUIC over QUIC) carrying TCP+UDP — so BuildRoute does NOT QUIC-reject
        // it. Distinct from the endpoints-based AWG signal (which also drives the
        // TUN-MTU cap + plain-UDP DNS; Hy2 wants neither — QUIC self-clamps + DoH
        // rides QUIC fine).
        proxyIsUdpNativeOutbound = false;
        var servers = settings.Vless.GetActiveServers();
        var hasRequestedChain = servers.Any(s => !string.IsNullOrEmpty(s.DetourVia));

        // macOS / Android naive backstop. The parser refuses naive at intake on
        // platforms without Cronet, so a naive entry can only reach generation
        // here via a settings.yaml carried over from a Windows/Linux box. Emitting
        // a naive outbound where libcronet is absent FATALs sing-box at start, so
        // drop naive entries on those platforms; the rest of the pool still works.
        // If this empties the pool the hard guard below fails loud (correct — no
        // usable proxy on this platform).
        if (!ServerUriParser.NaiveRuntimeAvailable)
            servers = servers.Where(s =>
                !"naive".Equals(s.Protocol, StringComparison.OrdinalIgnoreCase)).ToList();

        // AmneziaWG / XHTTP backstop (bug-hunt 2026-06-28, defense-in-depth).
        // The URI parsers gate these fork-only features at intake, but a
        // PERSISTED server reaches generation without re-entering a parser — a
        // stale / hand-edited config.yaml (protocol: amneziawg or
        // transport.type: xhttp) deserialized by SettingsLoader, or
        // VlessServersResolver aggregation. On an OFFICIAL build the emitted
        // `endpoints` wireguard block / `xhttp` transport FATALs upstream
        // sing-box at config load. Drop them when the bundled binary lacks the
        // fork (mirrors the naive backstop above); if that empties the pool the
        // hard guard below fails loud — fail-closed, never a bricking config.
        if (!SingBoxFeatures.AwgAvailable)
            servers = servers.Where(s =>
                !"amneziawg".Equals(s.Protocol, StringComparison.OrdinalIgnoreCase)
                && !"awg".Equals(s.Protocol, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!SingBoxFeatures.XhttpAvailable)
            servers = servers.Where(s =>
                !"xhttp".Equals(s.Transport?.Type, StringComparison.OrdinalIgnoreCase)).ToList();

        // urltest R5 (audit batch-1 #3): verdict-driven Auto-pool hygiene. When the
        // user opted into AutoSelectBestServer, drop pool members whose PERSISTED
        // health verdict is a FRESH ProtocolHandshakeBlockedLikely (TCP-reachable but
        // the VPN protocol failed a real proxied probe — the RU DPI/TSPU signature).
        // urltest's own generate_204 probe can't see that: a blocked member can keep
        // winning on latency while carrying no traffic. Rules:
        //  - ONLY in auto-select mode — a manually chosen server is never overridden;
        //  - fail-open: never drop below one member (all-blocked => keep the full
        //    pool and let urltest try — a wrong verdict must not brick connectivity);
        //  - freshness TTL lives in ServerHealthStore (a stale verdict never excludes).
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

            // R3: provider/subnet-level hygiene. Grouped analysis over the ORIGINAL
            // pool's records (the blocked members carry the evidence): a key with
            // >= ProviderHighRiskThreshold blocked-likely servers while ANOTHER key
            // is Healthy marks the whole subnet HighRisk — its remaining (untested)
            // members are dropped too, they share the blocked allocation. Same
            // fail-open rail: never below one member.
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

        // v2.28.2 hard guard: if we got here with no servers, the resulting
        // sing-box JSON would have route rules referencing a "proxy" outbound
        // tag that we never emit (because AddOutboundGroup short-circuits on
        // empty lists). sing-box loads that config but silently ignores the
        // process_name → proxy rule, so all routed traffic falls through to
        // route.final ("direct") — a silent leak. Worse, sing-box still runs
        // urltest probes against the upstream server which produce a wave of
        // "flow mismatch" errors in the server log (no VLESS handshake on a
        // raw TCP probe). Field-discovered in v2.28.1: VpnEngine.Apply
        // (hot-reload path) had no aggregation guard and would call us with
        // empty Vless.Servers when the user had only subscription-stored
        // servers in App.Subscriptions[].Servers. The fix is two-pronged:
        //   1. VlessServersResolver.Resolve() in StartAsync + Apply (callers).
        //   2. This guard here as a safety net so any future caller path
        //      that forgets to resolve fails loud instead of producing a
        //      silently-broken config.
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

        // AmneziaWG: a single AWG active server is a full WireGuard tunnel that carries ALL
        // traffic (TCP+UDP) natively — no UDP split, no proxy-udp. Emit it as a "proxy"
        // ENDPOINT (sing-box-lx with_awg); routes reference "proxy" (the endpoint tag) exactly
        // like an outbound. hasUdpProxy stays false (no separate proxy-udp outbound), but
        // BuildRoute is told proxyIsUdpNative so it does NOT QUIC-reject this UDP-native tunnel.
        // Requires a sing-box-lx client; gated at intake (SingBoxFeatures.AwgAvailable) AND by
        // the config-gen backstop above, so an official build never reaches this branch.
        // Only treat AWG as active when the SELECTED entry itself is AWG.
        // GetActiveServers() can return same-host siblings (active + same-IP
        // TCP/UDP pair), so a `FirstOrDefault(amneziawg)` would let an AWG
        // sibling HIJACK a selected VLESS/HY2/TUIC server on the same host —
        // silently swapping protocol, credentials and route semantics. Mirror
        // GetActiveServers' own active-resolution (by name, fallback first).
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

        // Active server is NOT AWG (the branch above returned otherwise): drop any
        // same-host AWG siblings GetActiveServers may have included, so they can't
        // be mis-built as a VLESS outbound (AWG has no uuid/transport).
        servers = servers.Where(s =>
            !"amneziawg".Equals(s.Protocol, StringComparison.OrdinalIgnoreCase)
            && !"awg".Equals(s.Protocol, StringComparison.OrdinalIgnoreCase)).ToList();

        // DNS-tunnel detection — the single source of truth for the route-layer
        // slipstream self-exclusion (see BuildRoute). When the active proxy is a
        // dns-tunnel server the VLESS outbound targets the local slipstream front
        // (127.0.0.1:7001); slipstream's OWN upstream traffic to the DNS resolvers
        // must be kept OUT of the tunnel or it loops back into itself.
        var dnsTunnelEntry = servers.FirstOrDefault(s => s.IsDnsTunnel);
        isDnsTunnel = dnsTunnelEntry != null;
        // Exclude BOTH the recursive resolver IPs AND the authoritative endpoint IP
        // (r7+ --authoritative) from the tunnel. r6 added the authoritative path but
        // not its IP here, so slipstream's queries to it got captured by full-tunnel
        // final=proxy and looped back to 127.0.0.1:7001 — breaking the data plane
        // (rx_bytes=0, no traffic). The authoritative endpoint must be reached DIRECT
        // (or fail closed on a whitelist net), never through the tunnel.
        dnsTunnelResolverIps = isDnsTunnel
            ? ExtractResolverIps(
                (dnsTunnelEntry!.DnsResolvers ?? new List<string>())
                .Concat(dnsTunnelEntry.DnsAuthoritative ?? new List<string>()))
            : new List<string>();

        var outbounds = new List<SingBoxOutbound>();

        // r5: NaiveProxy UDP pairing. naive can't carry UDP (HTTP/2 CONNECT is
        // TCP-only). When the active server is naive and the subscription
        // provides a co-located UDP-capable sibling (matching PairGroup tag, or
        // a matching base name as a pre-tag fallback), route ALL UDP through the
        // sibling (proxy-udp) while TCP stays on naive (proxy). The existing
        // hasUdpProxy route machinery then sends UDP → proxy-udp and skips the
        // QUIC block. Same physical node → same exit IP, no leak.
        var udpSibling = FindNaiveUdpSibling(servers, settings.Vless.Servers, isServerAlive);
        // r6 #2: the TCP "proxy" group must contain ONLY naive/TCP entries —
        // never the UDP sibling. GetActiveServers() returns every same-host
        // entry, so when naive and its paired HY2 share one host the sibling
        // lands in `servers` too; left in, sing-box's urltest could pick HY2
        // for TCP and defeat the whole point of naive (its DPI-evasion).
        // r10 (Codex follow-up #1): build the TCP group from naive entries ONLY,
        // so the UDP sibling AND any other same-host VLESS/HY2/TUIC are excluded
        // by construction — not just the one chosen sibling. Otherwise a same-host
        // non-naive server could be picked for TCP and defeat naive's DPI-evasion.
        var tcpNaiveServers = udpSibling != null
            ? servers.Where(NaivePairing.IsNaive).ToList()
            : new List<VlessServerEntry>();
        // r11 defensive guard: take the naive-pairing branch ONLY when the TCP
        // group is actually non-empty. Today this is always true when udpSibling
        // != null (both derive from `servers`, so a sibling implies a naive entry
        // is present), but if a future GetActiveServers() change ever broke that
        // invariant, emitting "proxy-udp" with no "proxy" would leave route rules
        // referencing a missing outbound -> silent leak. Falling through to the
        // standard split guarantees a "proxy" outbound is always built.
        // Selected Hy2/TUIC → SOLE "proxy": these tunnel BOTH TCP and UDP over one
        // QUIC transport, so a single outbound carries everything. Honour the user's
        // explicit UDP-native pick instead of letting the flow-split hand TCP to a
        // same-host VLESS-Reality sibling — that rides VLESS-over-TCP, throttled by
        // RU TSPU, the very reason to choose Hy2. Mirrors the AWG "selected entry
        // wins" name-resolution above (by name, fallback first) so a sibling can't
        // hijack the selection. Diagnosed 2026-07-02 (diag 20260702-183129): browser
        // 696x i/o-timeout on outbound/vless[proxy] while active = "Germany HY2".
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
            // Sole UDP-native proxy: no proxy-udp split, no VLESS TCP sibling, and
            // proxyIsUdpNativeOutbound tells BuildRoute NOT to QUIC-reject (Hy2/TUIC
            // carry QUIC over real UDP — no TCP-over-TCP meltdown to pre-empt).
            AddOutboundGroup(outbounds, new List<VlessServerEntry> { udpNativeActive }, "proxy", "vless");
            hasUdpProxy = false;
            proxyIsUdpNativeOutbound = true;
        }
        else if (udpSibling != null && tcpNaiveServers.Count > 0)
        {
            AddOutboundGroup(outbounds, tcpNaiveServers, "proxy", "vless");                                     // naive → TCP/all
            AddOutboundGroup(outbounds, new List<VlessServerEntry> { udpSibling }, "proxy-udp", "vless-udp"); // sibling → UDP
            hasUdpProxy = true;
        }
        else
        {
            // Auto-detect: split servers by flow presence (VLESS-vision TCP vs UDP)
            var flowServers = servers.Where(s => !string.IsNullOrEmpty(s.Flow)).ToList();
            var noFlowServers = servers.Where(s => string.IsNullOrEmpty(s.Flow)).ToList();
            // RB1: the UDP group must be ALIVE — drop dead no-flow servers when a
            // probe is available (never carry UDP on a dead node). If that empties
            // the group, fall through to a single outbound (UDP rides the flow proxy).
            if (isServerAlive != null)
                noFlowServers = noFlowServers.Where(isServerAlive).ToList();
            hasUdpProxy = flowServers.Count > 0 && noFlowServers.Count > 0;

            if (hasUdpProxy)
            {
                // Dual outbound: TCP → proxy (with flow), UDP → proxy-udp (no flow)
                AddOutboundGroup(outbounds, flowServers, "proxy", "vless");
                AddOutboundGroup(outbounds, noFlowServers, "proxy-udp", "vless-udp");
            }
            else
            {
                // Single outbound: all traffic → proxy
                AddOutboundGroup(outbounds, servers, "proxy", "vless");
            }
        }

        outbounds.Add(new SingBoxOutbound { Type = "direct", Tag = "direct" });
        // dns-direct: separate non-empty direct outbound for DNS servers.
        // sing-box 1.13 FATAL: "detour to empty direct outbound makes no sense"
        // when using detour:"direct" on a bare direct outbound. udp_fragment:true
        // makes it non-empty so we can route DNS through it.
        outbounds.Add(new SingBoxOutbound { Type = "direct", Tag = "dns-direct", UdpFragment = true });
        return outbounds;
    }

}
