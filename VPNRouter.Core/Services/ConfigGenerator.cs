using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using VPNRouter.Core.Json;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Generates sing-box 1.12+ compatible JSON config.
///
/// Migration from legacy API:
/// - DNS: uses new type-based server format (type: remote/local)
/// - DNS rules: uses action-based format (action: route/reject)
/// - Route: no more "dns" or "block" outbound types — uses action: "hijack-dns" and action: "reject"
/// </summary>
public static partial class ConfigGenerator
{

    // v2.44.4 (2026-06-27): hard ceiling on the TUN MTU at generation time.
    // A jumbo 9000 (the pre-v2.42 default) can get STUCK in a persisted config:
    // the 9000->1280 fix lived in the v5->v6 migration, so a config that already
    // passed v5->v6 on an older build never re-runs it, and Migrate_6_to_7 only
    // caught 1500 — so the value survives. A 9000 TUN MTU over a ~1500 proxied
    // path blackholes PMTUD: oversized DoH/HTTPS/HTTP2 segments silently vanish,
    // which stalls Roblox DNS + joins -> Error 277 (diag 20260627-203104: tester
    // on schema v6 + mtu 9000 + 1023 DNS exchanges >=10s; same subscription is
    // fine for users on a sane non-jumbo MTU). Clamp here so a stuck persisted
    // value can never reach sing-box, independent of migration state. Values
    // outside the product's persisted MTU contract fall back to the current
    // default; valid narrower settings are preserved.
    internal static int NormalizeTunMtu(int mtu)
        => mtu < TunSettings.MinimumMtu || mtu > TunSettings.MaximumMtu
            ? TunSettings.DefaultMtu
            : mtu;

    // macOS 26.5 + sing-box-lx: the system stack installs utun99 routes but
    // never receives TCP from the host (live repro 2026-07-11: 0 TCP inbounds,
    // gVisor with the identical config returned gstatic 204 in 0.6s).
    internal static string SelectTunStack(bool isMacOS)
        => isMacOS ? "gvisor" : "system";

    // v2.45.0-r11 (2026-07-02): AmneziaWG/WireGuard endpoint MTU = 1420 (the
    // wireguard-go DefaultMTU for a ~1500 underlay; AWG transport overhead == WG,
    // the obfuscation junk is handshake-only). A UDP WireGuard endpoint has NO
    // adaptive clamp, and the relevant sing-tun system-stack IPv4 fragment path
    // drops unsupported fragments without supplying an application-level MTU
    // fallback. Keep the TUN no larger than the endpoint.
    // WHY NOT 1280 (the r8 value — it was a regression): SDR (Dota/CS2) sends UDP
    // payloads up to 1300 B (1328 B IP) with NO PMTUD (GameNetworkingSockets#22);
    // a 1280 cap silently drops them -> match-connect dies even once region pings
    // work. The r8 "1280 fixes DNS" belief was wrong — the real DNS fix was plain-
    // UDP DNS (BuildVpnDnsServer), not this cap. TUN is clamped to min(user, this);
    // endpoint >= TUN (no inversion). Underlay < 1500 (PPPoE/mobile) may need lower
    // — see plans/mtu-fragmentation-robustness-2026-07-02.md.
    internal const int AwgEndpointMtu = 1420;

    /// <summary>
    /// Resolve the process list that per-app routing actually emits. Include mode
    /// honours an explicit list when present and otherwise falls back to the
    /// profile scanner; exclude mode always uses its own explicit list.
    /// </summary>
    internal static List<string> ResolveEffectiveAppProcesses(
        IEnumerable<string> resolvedProcessNames,
        AppSettings settings)
    {
        static List<string> Normalize(IEnumerable<string>? names) => (names ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Where(p => !p.Contains('*') && !p.Contains('?'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var routingAppsMode = (settings.App.RoutingAppsMode ?? "include")
            .ToLowerInvariant();
        if (routingAppsMode == "exclude")
            return Normalize(settings.App.RoutingAppsExclude);

        var explicitInclude = Normalize(settings.App.RoutingAppsInclude);
        if (explicitInclude.Count > 0 || settings.App.RoutingAppsIncludeInitialized)
            return explicitInclude;
        return Normalize(resolvedProcessNames);
    }

    /// <summary>
    /// Deterministic fingerprint of effective per-app routing. Full-tunnel mode
    /// deliberately ignores app-list edits because no per-app rule is emitted.
    /// Process-name casing is preserved because sing-box matching is
    /// case-sensitive; callers compare fingerprints ordinally.
    /// </summary>
    internal static string ComputeAppRoutingFingerprint(
        IEnumerable<string> resolvedProcessNames,
        AppSettings settings)
    {
        if ((settings.App.RoutingMode ?? "split")
            .Equals("full", StringComparison.OrdinalIgnoreCase))
            return "full";

        var mode = (settings.App.RoutingAppsMode ?? "include")
            .Equals("exclude", StringComparison.OrdinalIgnoreCase)
            ? "exclude"
            : "include";
        var processes = ResolveEffectiveAppProcesses(resolvedProcessNames, settings)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        return $"{mode}:{string.Join('\n', processes)}";
    }

    public static SingBoxConfig Generate(
        Profile profile,
        IEnumerable<string> resolvedProcessNames,
        AppSettings settings,
        bool? strictDnsOverride = null,
        Func<VlessServerEntry, bool>? isServerAlive = null)
    {
        // AM-1 (2026-05-11): per-app routing mode. Two paths:
        //  • "exclude" — user opted into inverted-split-tunnel. Take the
        //    processes from RoutingAppsExclude (NOT from
        //    resolvedProcessNames) and emit route rules that send them
        //    OUT of the proxy (action=route, outbound=direct) while
        //    route.final flips to "proxy".
        //  • "include" (default) — preserve legacy behaviour. The caller
        //    supplies resolvedProcessNames via VpnEngine which already
        //    layers Profile.Processes + CustomApps + CustomGroupApps +
        //    ExcludedApps; we keep using that list so the legacy profile
        //    system stays intact.
        //
        // Mode is read DIRECTLY from RoutingAppsMode below (NOT inferred from
        // which list happens to be populated). "exclude" → RoutingAppsExclude;
        // anything else → the include path. When the include list is empty
        // (clean install, never opened the Apps tab) we fall through to the
        // legacy resolvedProcessNames path — no surprise empty config.
        var routingAppsMode = (settings.App.RoutingAppsMode ?? "include")
            .ToLowerInvariant();
        var isExcludeMode = routingAppsMode == "exclude";
        var appsProcessList = ResolveEffectiveAppProcesses(resolvedProcessNames, settings);

        // macOS (Fix #2, deep-audit 2026-06-04): Chromium/Electron apps do their
        // network I/O under child "Helper" processes ("Google Chrome Helper
        // (Renderer)", etc.), NOT under the parent's name. sing-box matches
        // process_name EXACTLY (filepath.Base, no wildcards), so a routed parent
        // ("Google Chrome") never matches the helper actually connecting —
        // split-tunnel silently leaks for every space-/Electron-named app.
        // Windows is unaffected (children share the parent's chrome.exe name).
        // Expand each routed base-name to the standard macOS helper variants;
        // non-Chromium apps get inert names (no such process → no match → no
        // effect), so the expansion is safe to apply blanket in both modes.
        if (OperatingSystem.IsMacOS())
            appsProcessList = ExpandMacHelperNames(appsProcessList);

        var logPath = AppPaths.SingBoxLogPath;

        var outbounds = BuildOutbounds(settings, out bool hasUdpProxy,
            out bool isDnsTunnel, out var dnsTunnelResolverIps, out var endpoints,
            out bool proxyIsUdpNativeOutbound, isServerAlive);

        // A UDP-native ENDPOINT (AmneziaWG / WireGuard tagged "proxy") drives the TUN
        // MTU cap AND the plain-UDP DNS path — see AwgEndpointMtu. A Hy2/TUIC sole
        // proxy wants NEITHER (QUIC self-clamps its packets; DoH rides QUIC fine), so
        // those two stay gated on the endpoint signal only.
        var proxyIsUdpNative = endpoints != null && endpoints.Count > 0;
        // Whether the active "proxy" carries UDP natively — AWG endpoint OR a selected
        // Hy2/TUIC sole outbound. Used ONLY to suppress the QUIC-reject in BuildRoute
        // (rejecting QUIC on a UDP-native tunnel needlessly forces HTTP/3 apps to TCP).
        var proxyCarriesUdpNatively = proxyIsUdpNative || proxyIsUdpNativeOutbound;

        var config = new SingBoxConfig
        {
            Log = new SingBoxLog
            {
                Level = settings.App.LogLevel,
                Timestamp = true,
                Output = logPath
            },
            Dns = BuildDns(profile, appsProcessList, settings, isExcludeMode, strictDnsOverride, proxyIsUdpNative),
            Inbounds = BuildInbounds(settings, proxyIsUdpNative),
            Outbounds = outbounds,
            Endpoints = endpoints, // AmneziaWG "proxy" endpoint (sing-box-lx); null otherwise
            Route = BuildRoute(profile, appsProcessList, settings.App.RoutingMode, hasUdpProxy, isExcludeMode, settings.App.BlockQuicOnTcpProxy, isDnsTunnel, dnsTunnelResolverIps, proxyIsUdpNative: proxyCarriesUdpNatively),
            // P1 clash_api secret (2026-07-10): the controller address comes
            // from settings (pre-P1 the model default silently ignored a
            // user-changed clash_api port) and the bearer secret locks the
            // API to our own consumers — without it any local process / web
            // page (and on Android any installed app) could read live
            // connection metadata or issue control calls on 127.0.0.1:9090.
            Experimental = new SingBoxExperimental
            {
                ClashApi = new ClashApi
                {
                    ExternalController = string.IsNullOrWhiteSpace(settings.SingBox?.ClashApi)
                        ? "127.0.0.1:9090" : settings.SingBox.ClashApi,
                    Secret = string.IsNullOrEmpty(settings.SingBox?.ClashApiSecret)
                        ? null : settings.SingBox.ClashApiSecret,
                }
            }
        };

        // v2.30.0 — full custom rules engine (direct/proxy/block). Inserted
        // AFTER toggle-driven rules (BypassRussianTraffic, BlockAds) so
        // toggles ALWAYS WIN over user rules per user direction 2026-04-29
        // («toggles остаются и всегда приоритетнее»).
        //
        // Insertion order in the rule list (top = highest priority,
        // first-match-wins in sing-box):
        //   1. sniff (always-on, BuildRoute)
        //   2. hijack-dns (always-on, BuildRoute)
        //   3. ip_is_private → direct (always-on, BuildRoute)
        //   4. BlockAds rule_set → reject (toggle, ApplyAdBlock)
        //   5. BypassRussianTraffic geosite-ru → direct (toggle, ApplyGeoBypass)
        //   6. CustomRules in user-declared order (this call)
        //   7. process_name → proxy (auto, BuildRoute)
        //   8. final = direct (split) / proxy (full)
        //
        // Toggles (#4, #5) inserted AFTER this CustomRules block in code
        // — but each Apply* function inserts at the same "after sniff/
        // hijack/private" position, and Insert pushes existing entries
        // down, so the LAST Apply* runs ENDS UP FIRST in the rule list.
        // To make toggles win, we run them AFTER ApplyCustomRules below.
        //
        // v2.29.0-r4 CustomDirectRules superseded by CustomRules; field
        // kept for back-compat (SettingsMigrator empties it on v1->v2
        // migration).
        // v2.30.0-r17 — CustomRulesPriority toggle. Two orderings:
        //   "toggles_first" (default, r1-r16 behavior):
        //      Apply order: CustomRules → BlockAds → BypassRu
        //      → final list: BypassRu, BlockAds, CustomRules, ...
        //      → toggles win.
        //   "custom_first" (user opt-in):
        //      Apply order: BlockAds → BypassRu → CustomRules
        //      → final list: CustomRules, BypassRu, BlockAds, ...
        //      → custom rules win.
        //
        // The mechanism: each Apply* inserts at the same "after sniff/
        // hijack/private" slot, pushing existing entries down. So the
        // Apply* called LAST ends up FIRST in the rule list (highest
        // priority).
        var customFirst = string.Equals(
            settings.App.CustomRulesPriority,
            "custom_first",
            StringComparison.OrdinalIgnoreCase);

        if (customFirst)
        {
            // BlockAds + BypassRu first → CustomRules last → CustomRules wins.
            if (settings.App.BlockAds) ApplyAdBlock(config);
            if (settings.App.BypassRussianTraffic && GeoDataDownloader.AreGeoFilesAvailable())
                ApplyGeoBypass(config);
            if (settings.App.CustomRules?.Count > 0)
                ApplyCustomRules(config, settings.App.CustomRules);
        }
        else
        {
            // Default: CustomRules first → BlockAds + BypassRu last → toggles win.
            if (settings.App.CustomRules?.Count > 0)
                ApplyCustomRules(config, settings.App.CustomRules);
            if (settings.App.BlockAds) ApplyAdBlock(config);
            if (settings.App.BypassRussianTraffic && GeoDataDownloader.AreGeoFilesAvailable())
                ApplyGeoBypass(config);
        }

        return config;
    }

    /// <summary>
    /// Phase 4 (2026-05-18) — migrated from Newtonsoft.Json
    /// <c>JsonConvert.SerializeObject</c> to <c>System.Text.Json
    /// JsonSerializer.Serialize</c>. Wire format is byte-identical:
    /// <list type="bullet">
    ///   <item><c>WriteIndented=true</c> mirrors Newtonsoft's
    ///   <c>Formatting.Indented</c>.</item>
    ///   <item><c>DefaultIgnoreCondition=WhenWritingNull</c> mirrors
    ///   Newtonsoft's <c>NullValueHandling.Ignore</c>, and the per-property
    ///   <c>[JsonIgnore(Condition=WhenWritingNull)]</c> attributes on
    ///   <see cref="VPNConfig"/> reinforce it.</item>
    ///   <item>Every wire key is pinned via <c>[JsonPropertyName]</c> on
    ///   the model — no global naming policy could change this.</item>
    /// </list>
    /// The sing-box check integration tests run against the generated JSON
    /// and trip if any key drifts.
    /// </summary>
    internal static readonly JsonSerializerOptions SingBoxOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Phase 5 — Wave 25 AOT-2 (2026-05-18): SingBoxConfig is registered
        // in AppJsonContext so the generated sing-box JSON serialise/
        // deserialise routes through compiled JsonTypeInfo (AOT-safe).
        // The compose-with-fallback chain keeps Phase4StjRoundTripTests
        // green for the sing-box-check integration path (which also uses
        // these options to round-trip via JsonSerializer.Deserialize).
        TypeInfoResolver = JsonTypeInfoResolver.Combine(
            AppJsonContext.Default,
            new DefaultJsonTypeInfoResolver()),
    };

    public static string Serialize(SingBoxConfig config)
    {
        return JsonSerializer.Serialize(config, Json.AppJsonContext.Default.SingBoxConfig);
    }

    // ─── Inbounds ─────────────────────────────────────────────────────────────

    private static List<SingBoxInbound> BuildInbounds(AppSettings settings, bool proxyIsUdpNative = false)
    {
        // Effective = persisted user list + freshly auto-detected WG/AWG subnets
        // (deduped). The auto subnets are runtime-only and never persisted; see
        // TunSettings.GetEffectiveRouteExcludeAddress / StartupPipeline step 4.5.
        var routeExcludes = settings.Tun.GetEffectiveRouteExcludeAddress();
        var mtu = NormalizeTunMtu(settings.Tun.Mtu);
        return new List<SingBoxInbound>
        {
            new()
            {
                Type                    = "tun",
                Tag                     = "tun-in",
                InterfaceName           = OperatingSystem.IsMacOS() ? "utun99" : settings.Tun.InterfaceName,
                Address                 = new List<string> { settings.Tun.Ipv4Address },
                // AWG's TUN must not exceed its 1420 endpoint MTU, but a user may
                // deliberately choose a lower value for a narrower underlay.
                Mtu                     = proxyIsUdpNative
                                            ? Math.Min(mtu, AwgEndpointMtu)
                                            : mtu,
                AutoRoute               = settings.Tun.AutoRoute,
                StrictRoute             = false, // Always false — avoid dual stack errors
                RouteExcludeAddress     = routeExcludes.Count > 0
                                            ? routeExcludes
                                            : null,
                // NOTE: this is a NO-OP on the system stack and its r10 RCA was WRONG.
                // The lx fork parses endpoint_independent_nat (option/tun.go:62) but
                // never passes it into StackOptions; it was removed from sing-box in
                // 1.11. The system stack's udpnat2 is source-keyed with no inbound
                // filtering = full cone (EIM+EIF) BY CONSTRUCTION already. And SDR does
                // NOT send cross-relay replies (Valve opens one socket per relay, reply
                // is same-address) — so the r10 "full-cone fixes Dota" mechanism is
                // doubly refuted. The real Dota/SDR cause is the AWG single-socket
                // WSAENOBUFS burst-choke (H1); full research in
                // plans/sdr-research-realtime-games-nat-2026-07-02.md. Value kept true:
                // harmless, and would apply if we ever switch to the gVisor stack.
                EndpointIndependentNat  = true,
                Stack                   = SelectTunStack(OperatingSystem.IsMacOS())
                // sniff + sniff_override_destination removed — deprecated since 1.11
                // Sniffing now handled by route rule: action="sniff"
            }
        };
    }

}
