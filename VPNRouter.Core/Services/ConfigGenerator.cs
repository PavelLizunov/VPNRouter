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
public static class ConfigGenerator
{
    // T4: domains whose DNS can be resolved off the congested proxy detour (real-NIC
    // Cloudflare DoH) when AppConfig.ResolveGameDnsOffProxy is on. Suffix-matched.
    private static readonly List<string> RealtimeGameDnsSuffixes = new()
    {
        "roblox.com",
        "rbxcdn.com",
        "steamserver.net",
        "steampowered.com",
        "steamstatic.com",
        "dota2.com",
    };

    // v2.44.4 (2026-06-27): hard ceiling on the TUN MTU at generation time.
    // A jumbo 9000 (the pre-v2.42 default) can get STUCK in a persisted config:
    // the 9000->1280 fix lives in the v5->v6 migration, so a config that already
    // passed v5->v6 on an older build never re-runs it, and Migrate_6_to_7 only
    // caught 1500 — so the value survives. A 9000 TUN MTU over a ~1500 proxied
    // path blackholes PMTUD: oversized DoH/HTTPS/HTTP2 segments silently vanish,
    // which stalls Roblox DNS + joins -> Error 277 (diag 20260627-203104: tester
    // on schema v6 + mtu 9000 + 1023 DNS exchanges >=10s; same subscription is
    // fine for users on the 1280 default). Clamp here so a stuck persisted value
    // can never reach sing-box, independent of migration state. 1280 = IPv6
    // minimum, traverses any VLESS/Reality/Hysteria2/TUIC encapsulation.
    private const int MaxSafeTunMtu = 1500;
    private const int SafeTunMtuFallback = 1280;
    internal static int NormalizeTunMtu(int mtu)
        => (mtu <= 0 || mtu > MaxSafeTunMtu) ? SafeTunMtuFallback : mtu;

    // v2.45.0-r8 (2026-07-01): AmneziaWG/WireGuard endpoints carry a FIXED inner
    // MTU (1280 = IPv6 minimum; survives WG + AWG-obfuscation encapsulation over
    // any ~1500 underlay). Unlike a VLESS/Reality TCP tunnel — where TCP MSS
    // auto-clamps to the path — a UDP WireGuard endpoint has NO adaptive clamp, so
    // an oversized segment (a TUN packet, or a DoH TLS ServerHello flight) larger
    // than the endpoint MTU blackholes on a PMTUD hole exactly like the mtu-9000
    // incident above. Diag 20260701-122336: TUN 1337 over a 1280 AWG endpoint ->
    // 548 DNS exchanges >=5s (cold DoH handshakes up to 56s) -> Dota region pings
    // all time out. So when a UDP-native endpoint is active we (a) cap the TUN MTU
    // to this, and (b) resolve via plain UDP DNS inside the tunnel (BuildVpnDnsServer)
    // instead of a fragile DoH handshake. Also the AWG endpoint's own mtu.
    internal const int AwgEndpointMtu = 1280;

    public static SingBoxConfig Generate(
        Profile profile,
        IEnumerable<string> resolvedProcessNames,
        AppSettings settings,
        bool? strictDnsOverride = null,
        Func<VlessServerEntry, bool>? isServerAlive = null)
    {
        // Filter out wildcard patterns — sing-box process_name doesn't support globs
        // Only pass exact .exe names (no * or ?)
        // Preserve original case — sing-box process_name matching is case-sensitive
        // (Go map lookup against filepath.Base from QueryFullProcessImageName)
        var processes = resolvedProcessNames
            .Where(p => !p.Contains('*') && !p.Contains('?'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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

        List<string> appsProcessList;
        if (isExcludeMode)
        {
            appsProcessList = (settings.App.RoutingAppsExclude ?? new List<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Where(p => !p.Contains('*') && !p.Contains('?'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else
        {
            // Include mode. If the user explicitly populated
            // RoutingAppsInclude we honour it verbatim (override path);
            // otherwise we use the legacy resolved list from
            // Profile/CustomApps. This keeps users that never touched the
            // new toggle on their previous behaviour byte-for-byte.
            var explicitInclude = (settings.App.RoutingAppsInclude ?? new List<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Where(p => !p.Contains('*') && !p.Contains('?'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            appsProcessList = explicitInclude.Count > 0 ? explicitInclude : processes;
        }

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
            out bool isDnsTunnel, out var dnsTunnelResolverIps, out var endpoints, isServerAlive);

        // A UDP-native proxy (AmneziaWG / WireGuard endpoint tagged "proxy") drives
        // the TUN MTU cap AND the plain-UDP DNS path — see AwgEndpointMtu.
        var proxyIsUdpNative = endpoints != null && endpoints.Count > 0;

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
            Route = BuildRoute(profile, appsProcessList, settings.App.RoutingMode, hasUdpProxy, isExcludeMode, settings.App.BlockQuicOnTcpProxy, isDnsTunnel, dnsTunnelResolverIps, proxyIsUdpNative: proxyIsUdpNative),
            Experimental = new SingBoxExperimental()
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

    // ─── Ad blocking ──────────────────────────────────────────────────────────

    private const string AdBlockRuleSetTag = "vpnrouter-adblock";
    private const string AdBlockRuleSetUrl =
        "https://raw.githubusercontent.com/REIJI007/AdBlock_Rule_For_Sing-box/main/adblock_reject.srs";
    private const string AdBlockRuleSetFilename = "adblock_reject.srs";

    private static void ApplyAdBlock(SingBoxConfig config)
    {
        // v2.31.9-r3: replace the prior `type:remote` rule-set with a
        // C#-managed local cache. Pre-r3 sing-box fetched
        // raw.githubusercontent.com synchronously at startup; a TLS
        // handshake timeout (slow / GeoIP-blocked / GFW user) was FATAL
        // and HealthMonitor looped on the same crash. brat-2026-05-05
        // logged 4 FATALs in 90 seconds before fluke-success.
        //
        // Now we ensure the .srs is on disk *first* (with bounded timeout
        // + stale-fallback in RuleSetCacheManager), and reference it as
        // `type:local`. If the cache manager returns null (no cached
        // copy AND fetch failed), we OMIT the rule-set entirely —
        // losing ad blocking is fine; losing the entire VPN is not.
        var localPath = RuleSetCacheManager.EnsureLocal(
            AdBlockRuleSetUrl,
            AdBlockRuleSetFilename);
        if (string.IsNullOrEmpty(localPath))
        {
            // Graceful degradation: skip the rule-set. The DNS + Route
            // rules below would hang sing-box on missing tag, so skip
            // them too — done by early-return.
            Serilog.Log.Logger.Warning(
                "[ConfigGenerator] AdBlock rule-set unavailable (offline + no cache); generating config WITHOUT ad blocking");
            return;
        }

        config.Route.RuleSet ??= new List<RuleSetEntry>();
        config.Route.RuleSet.Add(new RuleSetEntry
        {
            Type = "local",
            Tag = AdBlockRuleSetTag,
            Format = "binary",
            Path = localPath,
        });

        // 2. DNS rule — reject DNS queries for ad domains (before other rules)
        config.Dns.Rules.Insert(0, new DnsRule
        {
            RuleSet = new List<string> { AdBlockRuleSetTag },
            Action = "reject"
        });

        // 3. Route rule — reject connections to ad domains (after sniff/
        // hijack-dns/ip_is_private, before any user/process rules).
        //
        // v2.30.1 regression fix: previously Insert(0, ...) which placed
        // adblock AT THE TOP — before sniff. That left destination domains
        // unset when matching, so the subsequent ApplyGeoBypass loop —
        // which scans `for sniff/hijack-dns/private prefix` — broke on
        // adblock's `action=reject` and gave up at insertAt=0. Net result:
        //   [BypassRu, AdBlock, sniff, hijack-dns, private, ..., final=proxy]
        // BypassRu's `geosite-ru → direct` then never matched because
        // sniff hadn't run, so all `.ru` traffic fell through to the
        // `final=proxy` outbound — exactly the symptom user reported
        // 2026-04-30 ("Full Tunnel + RU bypass enabled, but 2ip.ru / Avito
        // show non-Russian IP").
        //
        // Correct ordering — keep sniff/hijack/private first, then
        // toggles + custom rules behind:
        //   [sniff, hijack-dns, private, BypassRu, AdBlock, ...custom, final]
        config.Route.Rules.Insert(FindCustomRulesInsertionPoint(config), new RouteRule
        {
            RuleSet = new List<string> { AdBlockRuleSetTag },
            Action = "reject"
        });
    }

    // ─── Custom rules engine (v2.30.0) ────────────────────────────────────────

    /// <summary>
    /// v2.30.0 — apply user-defined custom routing rules. Three actions:
    /// direct / proxy / block. Each enabled rule maps to one
    /// <see cref="RouteRule"/> entry; block rules with domain-type match
    /// also produce a DNS-level reject rule so the lookup itself fails
    /// (saves a roundtrip + matches user expectation of "blocked = invisible").
    ///
    /// <para>Insertion point: after sniff/hijack-dns/private-ip rules.
    /// Rules from this method end up BEFORE the toggle-driven rules
    /// (BypassRussianTraffic, BlockAds) because those run LATER and
    /// each Apply* method inserts at the same position — pushing earlier
    /// inserts down. Toggles win, per user direction 2026-04-29.</para>
    ///
    /// <para>Invalid rules (unknown action/type, malformed value) are
    /// silently skipped — the parser already validates and reports
    /// errors at edit time.</para>
    /// </summary>
    /// <remarks>Internal so unit tests can call directly.</remarks>
    internal static void ApplyCustomRules(SingBoxConfig config, List<CustomRule> rules)
    {
        int insertAt = FindCustomRulesInsertionPoint(config);

        // Iterate in reverse so each Insert at insertAt preserves the
        // relative order of input rules (last inserted ends up at insertAt;
        // first inserted ends up at insertAt+N-1). Mirrors the v2.29 pattern.
        for (int idx = rules.Count - 1; idx >= 0; idx--)
        {
            var rule = rules[idx];
            if (!rule.Enabled) continue;

            // v2.31.9-r5: for geosite/geoip rules, register rule-set FIRST
            // (which may fail gracefully if offline — see
            // RuleSetCacheManager). If no rule-sets registered, skip the
            // route rule too — emitting a route rule that references a
            // missing rule-set tag would FATAL sing-box at startup.
            var isGeoType =
                rule.Type.Equals("geosite", StringComparison.OrdinalIgnoreCase) ||
                rule.Type.Equals("geoip", StringComparison.OrdinalIgnoreCase);
            if (isGeoType)
            {
                var registered = EnsureCustomRuleSetEntry(config, rule.Type, rule.Value);
                if (registered.Count == 0)
                {
                    // Cache miss + fetch fail for ALL referenced names —
                    // skip this whole rule so we don't emit a route rule
                    // that would crash sing-box. Better to lose a custom
                    // rule than the entire VPN.
                    continue;
                }
                // Note: if SOME names registered and others didn't, the
                // built route rule below still references the original
                // (full) value list. sing-box will warn about the missing
                // tags but won't FATAL — `route.rule_set` ignores unknown
                // tags as of 1.13.x. Future polish: filter the rule's
                // value list to the registered subset.
            }

            var built = BuildCustomRouteRule(rule);
            if (built == null) continue;
            config.Route.Rules.Insert(insertAt, built);

            // For block actions on domain-types, also insert DNS reject.
            // Saves a DNS roundtrip + matches "blocked = invisible" UX.
            if (rule.Action.Equals("block", StringComparison.OrdinalIgnoreCase)
                && IsDomainTypeForDns(rule.Type))
            {
                var dnsReject = BuildCustomDnsRejectRule(rule);
                if (dnsReject != null)
                    config.Dns.Rules.Insert(0, dnsReject);
            }
        }
    }

    /// <summary>Find the insertion point after sniff/hijack-dns/private
    /// rules. Mirrors the v2.29.0-r4 logic; extracted for reuse.</summary>
    private static int FindCustomRulesInsertionPoint(SingBoxConfig config)
    {
        int insertAt = 0;
        for (int i = 0; i < config.Route.Rules.Count; i++)
        {
            var r = config.Route.Rules[i];
            if (r.Action == "sniff" || r.Action == "hijack-dns" || r.IpIsPrivate == true || r.IsInfrastructure)
            {
                insertAt = i + 1;
                continue;
            }
            break;
        }
        return insertAt;
    }

    /// <summary>v2.30.0 — convert a <see cref="CustomRule"/> into a
    /// sing-box <see cref="RouteRule"/>. Action-specific output:
    /// <list type="bullet">
    /// <item>direct ⇒ action=route, outbound=direct</item>
    /// <item>proxy ⇒ action=route, outbound=proxy (or proxy-udp when network=udp)</item>
    /// <item>block ⇒ action=reject, method=default (TCP RST)</item>
    /// </list>
    /// Returns null on invalid action/type or empty value.</summary>
    /// <remarks>Internal so unit tests can call directly.</remarks>
    internal static RouteRule? BuildCustomRouteRule(CustomRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Value)) return null;
        var values = rule.Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(v => v.Length > 0)
            .ToList();
        if (values.Count == 0) return null;

        var route = new RouteRule();

        // Action mapping.
        switch ((rule.Action ?? "direct").ToLowerInvariant())
        {
            case "direct":
                route.Action = "route";
                route.Outbound = "direct";
                break;
            case "proxy":
                route.Action = "route";
                // Default to "proxy" (TCP outbound). For network=udp we'd
                // ideally route to "proxy-udp" if it exists, but the
                // proxy-udp tag depends on outbound generation timing.
                // For v2.30 keep simple: always "proxy". Mismatched udp
                // traffic still works because sing-box's default selector
                // handles UDP gracefully.
                route.Outbound = "proxy";
                break;
            case "block":
                route.Action = "reject";
                // method=default produces TCP RST — apps fail-fast
                // (better UX than silent drop for explicit user blocks).
                // sing-box default is also "default", so we don't set
                // the field explicitly to keep config minimal.
                break;
            default:
                return null; // unknown action
        }

        // Type → match field mapping.
        switch ((rule.Type ?? "domain_suffix").ToLowerInvariant())
        {
            case "domain":
                route.Domain = values;
                break;
            case "domain_suffix":
                route.DomainSuffix = values;
                break;
            case "domain_keyword":
                route.DomainKeyword = values;
                break;
            case "ip_cidr":
                route.IpCidr = values;
                break;
            case "port":
                var ports = new List<int>();
                foreach (var v in values)
                    if (int.TryParse(v, out var p) && p >= 1 && p <= 65535)
                        ports.Add(p);
                if (ports.Count == 0) return null;
                route.Port = ports;
                break;
            case "port_range":
                // sing-box uses port_range field; RouteRule model doesn't
                // currently expose it. For v2.30 we encode port_range as
                // multiple discrete ports up to 50 entries (anything wider
                // should use a dedicated field; backlog for v2.31).
                // For simplicity use the existing Port list with a range
                // expansion capped at 50 to avoid bloat.
                var rangePorts = new List<int>();
                foreach (var v in values)
                {
                    if (TryParsePortRange(v, out var min, out var max))
                    {
                        // Cap range expansion at 50 ports.
                        var step = Math.Max(1, (max - min) / 50);
                        for (int p = min; p <= max; p += step) rangePorts.Add(p);
                        if (!rangePorts.Contains(max)) rangePorts.Add(max);
                    }
                }
                if (rangePorts.Count == 0) return null;
                route.Port = rangePorts.Distinct().Take(64).ToList();
                break;
            case "network":
                // network is a scalar in sing-box. For v2.30 take first value.
                route.Network = values[0].ToLowerInvariant();
                break;
            case "process_name":
                // Case-sensitive — preserve user input casing.
                route.ProcessName = values;
                break;
            case "geosite":
            case "geoip":
                // sing-box rule_set match: the value is the rule_set tag.
                // Tag = "user-{geosite|geoip}-{name}" to avoid collision
                // with built-in tags (vpnrouter-geosite-ru / -geoip-ru).
                var tagPrefix = rule.Type.Equals("geosite", StringComparison.OrdinalIgnoreCase)
                    ? "user-geosite-" : "user-geoip-";
                route.RuleSet = values.Select(v => tagPrefix + v).ToList();
                break;
            default:
                return null;
        }
        return route;
    }

    /// <summary>v2.30.0 — for block-action rules with domain-type match,
    /// build a DNS-level reject so the lookup itself fails. Returns null
    /// when the rule type isn't a domain match (DNS rejects only apply
    /// to domain queries).</summary>
    private static DnsRule? BuildCustomDnsRejectRule(CustomRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Value)) return null;
        var values = rule.Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(v => v.Length > 0)
            .ToList();
        if (values.Count == 0) return null;

        var dns = new DnsRule { Action = "reject" };
        switch (rule.Type.ToLowerInvariant())
        {
            case "domain":         dns.Domain = values; break;
            case "domain_suffix":  dns.DomainSuffix = values; break;
            case "domain_keyword": dns.DomainKeyword = values; break;
            case "geosite":
                dns.RuleSet = values.Select(v => "user-geosite-" + v).ToList();
                break;
            default: return null;
        }
        return dns;
    }

    private static bool IsDomainTypeForDns(string type) => type.ToLowerInvariant() switch
    {
        "domain" or "domain_suffix" or "domain_keyword" or "geosite" => true,
        _ => false,
    };

    /// <summary>v2.30.0 — register the rule_set entry that sing-box
    /// needs to load the .srs file.
    ///
    /// <para>v2.31.9-r5: same hardening as <see cref="ApplyAdBlock"/>.
    /// Pre-r5 these were <c>type:remote</c> with
    /// <c>download_detour:direct</c> pointing at SagerNet's GitHub raw.
    /// Same fragility as the AdBlock rule-set: TLS handshake timeout
    /// during sing-box startup = FATAL = HealthMonitor crash loop. Now
    /// pre-fetched via <see cref="RuleSetCacheManager"/> with stale
    /// fallback; rule-sets that can't be fetched (and have no cache)
    /// are silently skipped — config still passes <c>sing-box check</c>
    /// because the route rule referencing the missing tag is also
    /// skipped at the call site (see
    /// <see cref="ApplyCustomRules"/>).</para>
    ///
    /// <para>Returns the list of TAGs that were successfully registered.
    /// Caller compares this against the input <paramref name="value"/>
    /// list to decide which route rules to emit / skip.</para>
    /// </summary>
    // macOS Chromium/Electron helper-process suffixes (Fix #2). These are the
    // child processes that actually open sockets; the parent (e.g. "Google
    // Chrome") rarely connects directly. sing-box matches process_name exactly,
    // so the parent name alone leaks the helpers' traffic past split-tunnel.
    private static readonly string[] MacHelperSuffixes =
    {
        " Helper",
        " Helper (GPU)",
        " Helper (Renderer)",
        " Helper (Plugin)",
    };

    // macOS apps whose real network I/O runs under FIXED system process names
    // that are NOT derivable from the app name by a suffix rule (Fix #2b, from
    // live r1 Mac logs 2026-06-04: Safari connects via com.apple.WebKit.Networking
    // — 73 conns — and com.apple.Safari.SearchHelper, never "Safari" or "Safari
    // Helper"). Keyed by the routed base-name; values are the process_name(s)
    // sing-box must match. NOTE: WebKit's Networking/GPU services are SHARED
    // across all WebKit clients, so routing Safari also routes other WebKit web
    // traffic — that's the only way to route Safari at all on macOS (it does no
    // network I/O under its own name), and "route my web browsing" is the intent.
    private static readonly Dictionary<string, string[]> MacKnownIoProcesses =
        new(StringComparer.Ordinal)
        {
            ["Safari"] = new[]
            {
                "com.apple.WebKit.Networking",    // the network I/O process
                "com.apple.WebKit.WebContent",
                "com.apple.WebKit.GPU",
                "com.apple.Safari.SearchHelper",  // search-bar suggestions
            },
        };

    /// <summary>
    /// macOS (Fix #2 / #2b): expand each routed app base-name to the child
    /// process(es) that actually open sockets, which sing-box matches by exact
    /// process_name. Two cases: (a) Chromium/Electron apps use "&lt;App&gt; Helper
    /// (Renderer)" etc. — append the suffixes; (b) a few apps (Safari/WebKit) do
    /// their I/O under FIXED Apple XPC names that are NOT derivable from the app
    /// name — look those up in <see cref="MacKnownIoProcesses"/>. Preserves
    /// original case (process_name is case-sensitive — golden rule #7), dedups
    /// case-sensitively, and never re-expands a name that is already a helper.
    /// </summary>
    internal static List<string> ExpandMacHelperNames(IEnumerable<string> names)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string n)
        {
            if (n.Length > 0 && seen.Add(n)) result.Add(n);
        }

        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            Add(name);

            // Don't expand something that is itself already a helper.
            if (name.Contains(" Helper", StringComparison.Ordinal)) continue;

            // Apps with FIXED I/O process names (Safari/WebKit): add those and
            // SKIP the Chromium suffix expansion (it would only emit inert
            // "Safari Helper" names that never match a real process).
            if (MacKnownIoProcesses.TryGetValue(name, out var ioNames))
            {
                foreach (var io in ioNames) Add(io);
                continue;
            }

            // Chromium/Electron: "<App> Helper (Renderer)" etc.
            foreach (var suffix in MacHelperSuffixes)
                Add(name + suffix);
        }
        return result;
    }

    private static List<string> EnsureCustomRuleSetEntry(SingBoxConfig config, string type, string value)
    {
        config.Route.RuleSet ??= new List<RuleSetEntry>();

        var values = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(v => v.Length > 0)
            .ToList();

        var isSite = type.Equals("geosite", StringComparison.OrdinalIgnoreCase);
        var prefix = isSite ? "user-geosite-" : "user-geoip-";
        var urlBase = isSite
            ? "https://raw.githubusercontent.com/SagerNet/sing-geosite/rule-set/geosite-"
            : "https://raw.githubusercontent.com/SagerNet/sing-geoip/rule-set/geoip-";

        var registered = new List<string>();
        foreach (var name in values)
        {
            var tag = prefix + name;
            if (config.Route.RuleSet.Any(rs => rs.Tag == tag))
            {
                registered.Add(tag);
                continue;
            }

            var srsName = (isSite ? "user-geosite-" : "user-geoip-") + name + ".srs";
            var localPath = RuleSetCacheManager.EnsureLocal(urlBase + name + ".srs", srsName);
            if (string.IsNullOrEmpty(localPath))
            {
                Serilog.Log.Logger.Warning(
                    "[ConfigGenerator] Custom rule-set '{Tag}' unavailable (offline + no cache); rule will be omitted",
                    tag);
                continue;
            }

            config.Route.RuleSet.Add(new RuleSetEntry
            {
                Type = "local",
                Tag = tag,
                Format = "binary",
                Path = localPath,
            });
            registered.Add(tag);
        }
        return registered;
    }

    private static bool TryParsePortRange(string s, out int min, out int max)
    {
        min = max = 0;
        var dashIdx = s.IndexOf('-');
        if (dashIdx < 1 || dashIdx == s.Length - 1) return false;
        if (!int.TryParse(s[..dashIdx], out min)) return false;
        if (!int.TryParse(s[(dashIdx + 1)..], out max)) return false;
        return min >= 1 && max <= 65535 && min <= max;
    }

    // ─── Custom direct rules (v2.29.0, kept for back-compat) ─────────────────

    /// <summary>v2.29.0 — apply user-defined direct routing rules.
    /// Each enabled rule yields a sing-box route rule with action=route /
    /// outbound=direct (the explicit "direct" outbound tag, not the
    /// dns-direct one — the latter is reserved for DNS detour).
    ///
    /// <para>Inserted after sniff/hijack-dns/private-ip rules, BEFORE the
    /// auto-generated process_name rules and the geo bypass rule. So a
    /// user-tagged domain/CIDR wins over both:
    /// <list type="number">
    /// <item>Process-name based "route via proxy" rules.</item>
    /// <item>RU geo "route via direct" rule (the bypass), which would
    /// otherwise be redundant for the same destinations but doesn't
    /// hurt to have two paths matching → both yield direct.</item>
    /// </list></para>
    ///
    /// <para>Invalid values are silently skipped (logged via
    /// <see cref="LeakProtection"/>). The UI runs validation before save
    /// so this is a defensive belt-and-suspenders.</para>
    /// </summary>
    /// <remarks>Internal so unit tests can call directly.</remarks>
    internal static void ApplyCustomDirectRules(SingBoxConfig config, List<CustomDirectRule> rules)
    {
        // Find insertion point: after sniff/hijack-dns/private-ip rules.
        int insertAt = 0;
        for (int i = 0; i < config.Route.Rules.Count; i++)
        {
            var r = config.Route.Rules[i];
            if (r.Action == "sniff" || r.Action == "hijack-dns" || r.IpIsPrivate == true || r.IsInfrastructure)
            {
                insertAt = i + 1;
                continue;
            }
            break;
        }

        // Iterate in reverse so each Insert at insertAt preserves the
        // relative order of input rules (last inserted ends up at insertAt;
        // first inserted ends up at insertAt+N-1).
        for (int idx = rules.Count - 1; idx >= 0; idx--)
        {
            var rule = rules[idx];
            if (!rule.Enabled) continue;
            var built = BuildCustomDirectRouteRule(rule);
            if (built == null) continue;
            config.Route.Rules.Insert(insertAt, built);
        }
    }

    /// <summary>Convert a <see cref="CustomDirectRule"/> to a sing-box
    /// <see cref="RouteRule"/>. Returns null on invalid type / empty value
    /// / failed CSV parse — caller skips those.</summary>
    /// <remarks>Internal so unit tests can call directly.</remarks>
    internal static RouteRule? BuildCustomDirectRouteRule(CustomDirectRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Value)) return null;
        var values = rule.Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(v => v.Length > 0)
            .ToList();
        if (values.Count == 0) return null;

        var route = new RouteRule
        {
            Action = "route",
            Outbound = "direct",
        };

        switch ((rule.Type ?? "domain_suffix").ToLowerInvariant())
        {
            case "domain":
                route.Domain = values;
                break;
            case "domain_suffix":
                route.DomainSuffix = values;
                break;
            case "domain_keyword":
                route.DomainKeyword = values;
                break;
            case "ip_cidr":
                route.IpCidr = values;
                break;
            case "port":
                var ports = new List<int>();
                foreach (var v in values)
                    if (int.TryParse(v, out var p) && p >= 1 && p <= 65535)
                        ports.Add(p);
                if (ports.Count == 0) return null;
                route.Port = ports;
                break;
            case "process_name":
                // sing-box process_name matching is case-sensitive (Go map
                // lookup against filepath.Base from the OS); preserve user's
                // input casing as-is. Wildcards are NOT supported by
                // sing-box, so we accept whatever the user typed but
                // don't expand them.
                route.ProcessName = values;
                break;
            default:
                return null; // unknown type, skip
        }
        return route;
    }

    // ─── Russian geo bypass ───────────────────────────────────────────────────

    private const string GeoIpRuleSetTag = "vpnrouter-geoip-ru";
    private const string GeoSiteRuleSetTag = "vpnrouter-geosite-ru";
    private const string DirectDnsRuTag = "vpnrouter-dns-ru";

    private static void ApplyGeoBypass(SingBoxConfig config)
    {
        // 1. Add rule_set entries pointing to local .srs files
        config.Route.RuleSet ??= new List<RuleSetEntry>();
        var geoIpPath = AppPaths.GeoIpRuPath.Replace('\\', '/');
        var geoSitePath = AppPaths.GeoSiteRuPath.Replace('\\', '/');

        config.Route.RuleSet.Add(new RuleSetEntry
        {
            Type = "local",
            Tag = GeoIpRuleSetTag,
            Format = "binary",
            Path = geoIpPath
        });
        config.Route.RuleSet.Add(new RuleSetEntry
        {
            Type = "local",
            Tag = GeoSiteRuleSetTag,
            Format = "binary",
            Path = geoSitePath
        });

        // 2. Add Russian DNS server (Yandex 77.88.8.8) routed via dns-direct
        // outbound (real NIC, no proxy, no routing loop)
        config.Dns.Servers.Add(new DnsServer
        {
            Tag = DirectDnsRuTag,
            Type = "udp",
            Server = "77.88.8.8",
            Detour = "dns-direct"
        });

        // 3. Add DNS rule: RU domains use Russian DNS resolver
        config.Dns.Rules.Insert(0, new DnsRule
        {
            RuleSet = new List<string> { GeoSiteRuleSetTag },
            Action = "route",
            Server = DirectDnsRuTag
        });

        // 4. Add route rule: RU sites/IPs go direct (BEFORE process_name rules)
        // Find insertion point: after sniff/hijack-dns/private-ip rules
        int insertAt = 0;
        for (int i = 0; i < config.Route.Rules.Count; i++)
        {
            var r = config.Route.Rules[i];
            if (r.Action == "sniff" || r.Action == "hijack-dns" || r.IpIsPrivate == true || r.IsInfrastructure)
            {
                insertAt = i + 1;
                continue;
            }
            break;
        }

        // v2.27.1-r2 — domain-only bypass (drop geoip-ru from the route rule).
        //
        // Previously the route rule OR'd geosite-ru + geoip-ru, which looks
        // thorough but misroutes large international services:
        //
        //   Google / Cloudflare / Akamai / Valve keep edge-cache nodes
        //   INSIDE Russian ISP infrastructure. Those edge IPs sit in RU
        //   netblocks, so MaxMind (and therefore sing-box's geoip-ru) tags
        //   them "RU". Our route rule then sent YouTube video chunks, CF-
        //   cached static assets, etc. out via outbound/direct — and since
        //   the user is physically on a Russian ISP, those bypassed flows
        //   hit the same throttling / MITM that the VPN was there to avoid.
        //
        //   Repro from the v2.27.0 production dump:
        //     grep 'outbound/direct\[direct\]' singbox.log | grep 142.251
        //     → 4 YouTube IPs going direct instead of via VLESS.
        //   User-visible symptom: "YouTube отваливается в браузере".
        //
        // geosite-ru (domain-based) is the right matcher for "Russian
        // service" — it keys on .ru TLD + curated Russian-service domains,
        // and the DNS rule above already routes those lookups to a
        // Russian resolver so the returned IPs are whatever the local
        // authority says. Adding geoip-ru on top was over-matching.
        //
        // Pure-IP Russian traffic (someone dialling 77.88.8.8 directly with
        // no DNS) is rare and acceptable to leave going through VPN — the
        // trade-off beats breaking YouTube for everyone.
        config.Route.Rules.Insert(insertAt, new RouteRule
        {
            RuleSet = new List<string> { GeoSiteRuleSetTag },
            Action = "route",
            Outbound = "direct"
        });
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

    // ─── DNS (sing-box 1.12+ format) ──────────────────────────────────────────

    /// <summary>
    /// Common public TLDs refused as bare LAN suffixes (G6 leak guard) — adding
    /// one would route every lookup under it to the system/ISP resolver in
    /// plaintext. Not exhaustive; just the obvious foot-guns.
    /// </summary>
    private static readonly HashSet<string> PublicTldDenyList =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "com", "net", "org", "io", "co", "dev", "app", "ru", "info", "biz",
            "online", "xyz", "me", "tv", "cc", "us", "uk", "de", "edu", "gov"
        };

    private static SingBoxDns BuildDns(Profile profile, List<string> processes, AppSettings settings, bool isExcludeMode = false, bool? strictDnsOverride = null, bool proxyIsUdpNative = false)
    {
        var routingMode = settings.App.RoutingMode ?? "split";
        var isFullTunnel = routingMode.Equals("full", StringComparison.OrdinalIgnoreCase);

        // v2.42.0 StrictDns runtime failover: HealthMonitor can pass
        // strictDnsOverride=false to suppress "all DNS via tunnel" when the
        // proxy is unreachable (germany endless-loading). null = honour the
        // persisted setting. Full-tunnel / exclude mode still force vpn-dns
        // regardless — there StrictDns isn't the sole driver and all traffic
        // legitimately rides the tunnel. See StrictDnsFailoverPolicy.
        var strictDns = strictDnsOverride ?? settings.App.StrictDns;

        // AM-1: in exclude mode `processes` holds the apps we are KEEPING
        // direct, so route.final flips to "proxy". The DNS default
        // mirrors that: by default DNS goes through the VPN; only the
        // listed exclude-apps get the local resolver (so they don't leak
        // their queries inside the tunnel when they're not even using
        // it). StrictDns and Full tunnel keep their existing semantics
        // (override to vpn-dns).
        var defaultVpnDns = isFullTunnel || isExcludeMode || strictDns;

        var dns = new SingBoxDns
        {
            // ipv4_only protects from IPv6 leaks (when VPN tunnels only IPv4) AND
            // skips slow AAAA queries (+100-300ms each). Disable only if user
            // explicitly wants IPv6 via dns.strategy in config.yaml.
            // G5 (2026-06-27): also force ipv4_only whenever the TUN itself carries
            // no IPv6 — an AAAA answer can't traverse an IPv4-only tunnel, so
            // skipping it avoids the "address not valid in its context" dial-fails
            // and the per-query stall, independent of ForceIpv4Only.
            Strategy = (settings.App.ForceIpv4Only || !settings.Tun.Ipv6Enabled) ? "ipv4_only" : null,
            // Strict DNS: all queries via VPN (no leaks possible).
            // Full tunnel: all DNS through VPN by default.
            // Exclude mode (AM-1): unmatched apps go via VPN, so DNS final = vpn-dns.
            // Include mode split tunnel: unmatched apps go direct, so DNS final = local-dns.
            Final = defaultVpnDns ? "vpn-dns" : "local-dns",
            Servers = new List<DnsServer>
            {
                // Tunnelled resolver (Detour="proxy"). DoH over a TCP tunnel, plain
                // UDP over a UDP-native (AmneziaWG) tunnel — see BuildVpnDnsServer.
                BuildVpnDnsServer(settings, proxyIsUdpNative),
                // Local DNS — Cloudflare DoH via dns-direct outbound (real NIC).
                // type:local would call getaddrinfo() → system resolver → ISP DNS,
                // which leaks queries to ISP for any process not in the routed list
                // (e.g. Windows DnsCache svchost.exe). DoH via Cloudflare hides queries.
                new()
                {
                    Tag        = "local-dns",
                    Type       = "https",
                    Server     = "1.1.1.1",
                    Path       = "/dns-query",
                    Detour     = "dns-direct"
                }
            },
            Rules = new List<DnsRule>()
        };

        // G6 (2026-06-27): split-DNS for private / LAN domains. Without this,
        // sing-box's blanket DNS hijack (route protocol=dns -> hijack-dns) sends
        // EVERY app's lookups — including DIRECT (non-routed) apps in split
        // tunnel — to the remote DoH (local-dns / vpn-dns), which cannot answer
        // LAN names (nas.local, printer.lan). Route private suffixes to the
        // SYSTEM resolver instead; public domains don't match and fall through to
        // dns.final unchanged (no ISP leak). Suppressed under StrictDns (the user
        // opted into all-DNS-via-VPN, accepting LAN breakage).
        if (settings.App.ResolveLanViaSystemDns && !strictDns)
        {
            dns.Servers.Add(new DnsServer
            {
                Tag  = "dns-system",
                Type = "local" // OS resolver — knows LAN/mDNS names, bypasses TUN
            });

            var lanSuffixes = new List<string> { "local", "lan", "home.arpa", "internal" };
            if (settings.App.LanDnsSuffixes != null)
            {
                foreach (var s in settings.App.LanDnsSuffixes)
                {
                    var t = s?.Trim().TrimStart('.');
                    if (string.IsNullOrEmpty(t)) continue;
                    // Leak guard (review nit, 2026-06-27): a bare PUBLIC TLD as a
                    // LAN suffix would route every lookup under it to the system /
                    // ISP resolver in plaintext — the exact leak DoH prevents.
                    // Refuse single-label public TLDs; legit private suffixes
                    // ("corp", "lan") and specific multi-label internal domains
                    // ("corp.example.com") are still allowed.
                    if (!t!.Contains('.') && PublicTldDenyList.Contains(t))
                        continue;
                    if (!lanSuffixes.Contains(t, StringComparer.OrdinalIgnoreCase))
                        lanSuffixes.Add(t);
                }
            }

            // LAN rule precedes the per-process rules (so a LAN name beats them);
            // adblock/reject rules may still Insert(0) ahead — correct, reject wins.
            dns.Rules.Add(new DnsRule
            {
                DomainSuffix = lanSuffixes,
                Action       = "route",
                Server       = "dns-system"
            });
        }

        if (isFullTunnel)
        {
            // Full tunnel: all DNS goes through vpn-dns (via Final above).
            // No per-process rules needed.
            // T4 (opt-in): resolve game domains via the real-NIC DoH (local-dns) instead
            // of the congested proxy detour, so a stalled proxy DoH doesn't hang Roblox
            // joins. DoH is encrypted -> not RU-poisoned. The connection still goes proxy.
            if (settings.App.ResolveGameDnsOffProxy)
            {
                dns.Rules.Add(new DnsRule
                {
                    DomainSuffix = RealtimeGameDnsSuffixes,
                    Action       = "route",
                    Server       = "local-dns"
                });
            }
        }
        else if (isExcludeMode)
        {
            // Exclude mode: listed apps must resolve their queries via
            // the local resolver so the lookups don't leak into the
            // tunnel they're explicitly bypassing. profile.DnsMode is
            // irrelevant here (it's a property of the legacy profile
            // system); we mirror routing intent on the DNS layer.
            if (processes.Count > 0)
            {
                dns.Rules.Add(new DnsRule
                {
                    ProcessName = processes.ToList(),
                    Action      = "route",
                    Server      = "local-dns"
                });
            }
        }
        else
        {
            // Include split tunnel: the targeted processes are routed through the
            // proxy, so their DNS MUST resolve through the tunnel too — otherwise
            // their lookups fall through to dns.final = local-dns (real NIC) and the
            // resolver sees the user's real IP for exactly the app they routed for
            // privacy. v2.40.0-r9 (#1 core-audit HIGH): dns_mode="direct" previously
            // SKIPPED this rule entirely (`profile.DnsMode != "direct"` guard) →
            // silent DNS leak, one-click reachable via the shipped Privacy_Shell
            // profile. Now a routed process ALWAYS gets a per-process DNS rule:
            //   smart   → local-dns (the explicit "tunnel traffic, local DoH for
            //             geo-CDN nearness" opt-in; an encrypted-DoH tradeoff),
            //   vpn_only / direct / anything else → vpn-dns (tunnel the DNS).
            if (processes.Count > 0)
            {
                var dnsServer = profile.DnsMode == "smart" ? "local-dns" : "vpn-dns";
                dns.Rules.Add(new DnsRule
                {
                    ProcessName = processes.ToList(),
                    Action      = "route",
                    Server      = dnsServer
                });
            }
        }

        return dns;
    }

    // ─── Inbounds ─────────────────────────────────────────────────────────────

    private static List<SingBoxInbound> BuildInbounds(AppSettings settings, bool proxyIsUdpNative = false)
    {
        // Effective = persisted user list + freshly auto-detected WG/AWG subnets
        // (deduped). The auto subnets are runtime-only and never persisted; see
        // TunSettings.GetEffectiveRouteExcludeAddress / StartupPipeline step 4.5.
        var routeExcludes = settings.Tun.GetEffectiveRouteExcludeAddress();
        return new List<SingBoxInbound>
        {
            new()
            {
                Type                    = "tun",
                Tag                     = "tun-in",
                InterfaceName           = OperatingSystem.IsMacOS() ? "utun99" : settings.Tun.InterfaceName,
                Address                 = new List<string> { settings.Tun.Ipv4Address },
                // A UDP-native (AmneziaWG) endpoint carries a fixed 1280 inner MTU
                // and cannot MSS-clamp; a larger TUN MTU blackholes oversized
                // segments (diag 20260701-122336). Cap the TUN to the endpoint MTU.
                Mtu                     = proxyIsUdpNative
                                            ? Math.Min(NormalizeTunMtu(settings.Tun.Mtu), AwgEndpointMtu)
                                            : NormalizeTunMtu(settings.Tun.Mtu),
                AutoRoute               = settings.Tun.AutoRoute,
                StrictRoute             = false, // Always false — avoid dual stack errors
                RouteExcludeAddress     = routeExcludes.Count > 0
                                            ? routeExcludes
                                            : null,
                EndpointIndependentNat  = false,
                Stack                   = "system"
                // sniff + sniff_override_destination removed — deprecated since 1.11
                // Sniffing now handled by route rule: action="sniff"
            }
        };
    }

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
        Func<VlessServerEntry, bool>? isServerAlive = null)
    {
        endpoints = null; // default: no endpoints -> official-sing-box-compatible config
        var servers = settings.Vless.GetActiveServers();

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
        if (udpSibling != null && tcpNaiveServers.Count > 0)
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

    /// <summary>
    /// r5: when the active server is NaiveProxy (UDP-incapable), find a
    /// co-located UDP-capable sibling so UDP (Discord voice, games) can route
    /// through it. Pairing key, in priority order:
    /// <list type="number">
    /// <item><see cref="VlessServerEntry.PairGroup"/> — the subscription's
    /// <c>pair=</c> tag (bulletproof; the backend marks naive + its same-node
    /// HY2 with the same value).</item>
    /// <item>Base-name match — strip the protocol token and compare the
    /// remainder (transition fallback before a refresh ships the tag).</item>
    /// </list>
    /// Returns the sibling (preferring Hysteria2/TUIC for best UDP), or null
    /// when the active server isn't naive or no UDP sibling exists (caller then
    /// falls back to the standard flow/no-flow logic).
    /// </summary>
    private static VlessServerEntry? FindNaiveUdpSibling(
        List<VlessServerEntry> activeServers, List<VlessServerEntry> pool,
        Func<VlessServerEntry, bool>? isServerAlive = null)
    {
        // r8 #6: pairing logic lives in NaivePairing so config-gen and the UI
        // ("naive + hy2" label) share ONE source of truth — the label can never
        // claim a pairing the generator wouldn't make.
        // RB1: pass the liveness probe so a dead UDP sibling is never selected.
        var naive = activeServers.FirstOrDefault(NaivePairing.IsNaive);
        return naive == null ? null : NaivePairing.FindUdpSibling(naive, pool, isServerAlive);
    }

    /// <summary>
    /// Add a group of VLESS outbounds. Single server → direct outbound.
    /// Multiple servers → individual outbounds + urltest wrapper.
    /// </summary>
    private static void AddOutboundGroup(List<SingBoxOutbound> outbounds,
        List<VlessServerEntry> servers, string groupTag, string childPrefix)
    {
        if (servers.Count == 1)
        {
            outbounds.Add(BuildVlessOutbound(servers[0], groupTag));
        }
        else if (servers.Count > 1)
        {
            var childTags = new List<string>();
            var usedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < servers.Count; i++)
            {
                var baseTag = !string.IsNullOrEmpty(servers[i].Name)
                    ? $"{childPrefix}-{servers[i].Name}"
                    : $"{childPrefix}-{i}";

                var tag = baseTag;
                var suffix = 2;
                while (!usedTags.Add(tag))
                    tag = $"{baseTag}-{suffix++}";

                childTags.Add(tag);
                outbounds.Add(BuildVlessOutbound(servers[i], tag));
            }

            outbounds.Add(new SingBoxOutbound
            {
                Type      = "urltest",
                Tag       = groupTag,
                Outbounds = childTags,
                Url       = "http://www.gstatic.com/generate_204",
                Interval  = "3m",
                Tolerance = 150,
                InterruptExistConnections = false
            });
        }
    }

    /// <summary>
    /// Build a single proxy outbound from a server entry. v2.30.1-r3
    /// dispatches on <see cref="VlessServerEntry.Protocol"/> to support
    /// VLESS+Reality / Hysteria2 / TUIC v5 / Shadowsocks 2022 (with
    /// optional ShadowTLS plugin) from a single entry-point. Existing
    /// callers keep working — VLESS remains the default protocol when
    /// the discriminator is empty or unset.
    /// </summary>
    private static SingBoxOutbound BuildVlessOutbound(VlessServerEntry entry, string tag)
    {
        var protocol = (entry.Protocol ?? "vless").ToLowerInvariant();
        return protocol switch
        {
            "hysteria2"   => BuildHysteria2Outbound(entry, tag),
            "hy2"         => BuildHysteria2Outbound(entry, tag),   // r10 (Codex #2): hy2 alias parity with VlessDeepVerifier
            "tuic"        => BuildTuicOutbound(entry, tag),
            "shadowsocks" => BuildShadowsocksOutbound(entry, tag),
            "ss"          => BuildShadowsocksOutbound(entry, tag),
            "naive"       => BuildNaiveOutbound(entry, tag),
            "dns-tunnel"  => BuildDnsTunnelOutbound(entry, tag),
            _             => BuildVlessOutboundCore(entry, tag),
        };
    }

    /// <summary>
    /// DNS-tunnel (slipstream) outbound. The VLESS traffic rides over the local
    /// slipstream-client front (started separately by SlipstreamManager /
    /// VpnEngine), so the outbound targets <c>127.0.0.1:&lt;localPort&gt;</c> with
    /// the uuid set and <b>no TLS / Reality / flow / transport</b> — the tunnel
    /// provides its own QUIC-TLS. The real server domain + resolvers + leaf cert
    /// live in the dns-tunnel profile and are consumed by SlipstreamManager, not
    /// here. No domain_resolver: the server is a literal loopback IP.
    /// </summary>
    private static SingBoxOutbound BuildDnsTunnelOutbound(VlessServerEntry entry, string tag)
    {
        return new SingBoxOutbound
        {
            Type       = "vless",
            Tag        = tag,
            Server     = "127.0.0.1",
            ServerPort = SlipstreamManager.DefaultLocalPort,
            Uuid       = entry.Uuid,
        };
    }

    /// <summary>The slipstream-client executable basename that sing-box matches
    /// in process_name rules (platform-correct: "slipstream-client.exe" on
    /// Windows, "slipstream-client" elsewhere — dns-tunnel is Windows/Linux only).
    /// Used by <see cref="BuildRoute"/> to keep the slipstream front's own
    /// upstream traffic OUT of the tunnel.</summary>
    private static string SlipstreamProcessName => Path.GetFileName(AppPaths.SlipstreamExePath);

    /// <summary>
    /// Extract literal IP addresses from dns-tunnel resolver strings
    /// (<c>"1.2.3.4:53"</c>, <c>"[2001:db8::1]:53"</c>, <c>"9.9.9.9"</c>),
    /// skipping hostnames (those are covered by the process_name exclusion).
    /// Returns bare IPs suitable for a sing-box <c>ip_cidr</c> rule (a bare IP
    /// is treated as /32 or /128). Order-preserving, de-duplicated.
    /// </summary>
    private static List<string> ExtractResolverIps(IEnumerable<string>? resolvers)
    {
        var ips = new List<string>();
        if (resolvers == null) return ips;
        foreach (var raw in resolvers)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var s = raw.Trim();
            string host;
            if (s.StartsWith("[", StringComparison.Ordinal))          // [ipv6]:port
            {
                var end = s.IndexOf(']');
                if (end <= 1) continue;
                host = s.Substring(1, end - 1);
            }
            else
            {
                var firstColon = s.IndexOf(':');
                var lastColon  = s.LastIndexOf(':');
                // Strip a trailing :port only for the unambiguous ipv4:port shape
                // (exactly one colon). A bare IPv6 literal has multiple colons and
                // no brackets — keep it whole.
                host = (firstColon >= 0 && firstColon == lastColon)
                    ? s.Substring(0, lastColon)
                    : s;
            }
            if (System.Net.IPAddress.TryParse(host, out _) && !ips.Contains(host))
                ips.Add(host);
        }
        return ips;
    }

    /// <summary>VLESS+Reality outbound (the original implementation).</summary>
    private static SingBoxOutbound BuildVlessOutboundCore(VlessServerEntry entry, string tag)
    {
        // Null-safe: YamlDotNet may leave nested objects null if YAML has empty keys
        var transport = entry.Transport ?? new VlessTransportConfig();
        var transportType = transport.Type ?? "tcp";

        return new SingBoxOutbound
        {
            Type       = "vless",
            Tag        = tag,
            Server     = entry.Server,
            ServerPort = entry.Port,
            Uuid       = entry.Uuid,
            // XHTTP is incompatible with XTLS-Vision (protocol limitation) — drop the flow
            // even if a stray one is present, so a VLESS+XHTTP+Reality config is valid.
            Flow       = (string.IsNullOrEmpty(entry.Flow)
                          || transportType.Equals("xhttp", StringComparison.OrdinalIgnoreCase))
                ? null : entry.Flow,
            Tls        = BuildTlsConfig(entry),
            Transport  = transportType.Equals("tcp", StringComparison.OrdinalIgnoreCase)
                ? null
                : BuildTransportConfig(transportType, transport),
            DomainResolver = "local-dns",
            // v2.36 F4 fix (EOStārāTheia 2026-05-23 — Android ~5 min
            // auto-disconnect). sing-box 1.13's default tcp_keep_alive
            // initial period is 5m, which doesn't beat ISP/NAT idle
            // timeouts on mobile (typically 30-180s). Forces the
            // connection to drop silently right at the 5-min mark.
            // Setting both fields to 30s makes OS-level keepalive
            // probes fire BEFORE NAT mappings expire. Cross-platform
            // (also helps desktop on flaky home routers / corporate
            // NATs). See plans/android-disconnect-investigation-v2.36.md.
            TcpKeepAlive         = "30s",
            TcpKeepAliveInterval = "30s",
        };
    }

    /// <summary>
    /// Hysteria2 outbound. ALPN defaults to <c>["h3"]</c> per Hysteria2
    /// spec (it's QUIC-only). When <see cref="VlessServerEntry.ObfsType"/>
    /// is "salamander", emits the obfs block.
    /// </summary>
    private static SingBoxOutbound BuildHysteria2Outbound(VlessServerEntry entry, string tag)
    {
        var tls = new TlsConfig
        {
            Enabled    = true,
            ServerName = string.IsNullOrEmpty(entry.Tls?.ServerName) ? entry.Server : entry.Tls.ServerName,
            Insecure   = entry.Tls?.Insecure ?? false,
            Alpn       = new List<string> { "h3" },
        };

        var ob = new SingBoxOutbound
        {
            Type           = "hysteria2",
            Tag            = tag,
            Server         = entry.Server,
            ServerPort     = entry.Port,
            Password       = entry.Password,
            Tls            = tls,
            // 2026-06-08 (scout #2 #6): Hysteria2 dials its server over QUIC/UDP.
            // In the naive+HY2 pairing it carries ALL the UDP, so on an IPv6-less
            // host it hits the SAME "address not valid in its context" failure the
            // naive fix targets. prefer_ipv4 = IPv4-first server resolution.
            DomainResolver = new DomainResolverValue("local-dns", "prefer_ipv4"),
        };

        if (!string.IsNullOrEmpty(entry.ObfsType))
        {
            ob.Obfs = new Hysteria2Obfs
            {
                Type     = entry.ObfsType,
                Password = entry.ObfsPassword,
            };
        }

        // T2 (2026-06-27): Brutal CC calibration. When both up/down are set (>0), engage
        // Brutal — it ignores loss and paces to the declared ceiling, masking the access-leg
        // loss/jitter that times RakNet out (Roblox 277) on a TSPU-throttled RU path. Both
        // required (sing-box wants the pair); 0/unset -> omit -> BBR (prior behaviour). The
        // value MUST be ~70-80% of measured goodput — over-declaring self-induces loss.
        if (entry.HysteriaUpMbps > 0 && entry.HysteriaDownMbps > 0)
        {
            ob.UpMbps   = entry.HysteriaUpMbps;
            ob.DownMbps = entry.HysteriaDownMbps;
        }

        return ob;
    }

    /// <summary>
    /// AmneziaWG (AWG2) endpoint for a sing-box-lx (with_awg) client. The schema —
    /// a <c>wireguard</c> endpoint with promoted obfuscation fields + peer with
    /// <c>persistent_keepalive_interval</c> — was verified against <c>sing-box-lx check</c>
    /// (2026-06-27). Server/Port are the peer endpoint; obfuscation params must match the
    /// server. See plans/amneziawg-fork-implementation-plan-2026-06-27.md.
    /// </summary>
    internal static SingBoxEndpoint BuildAmneziaWgEndpoint(VlessServerEntry entry, string tag)
    {
        var awg = entry.Awg ?? new AwgConfig();
        static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
        return new SingBoxEndpoint
        {
            Type       = "wireguard",
            Tag        = tag,
            System     = false,
            Mtu        = AwgEndpointMtu,
            Address    = awg.Address.Count > 0 ? new List<string>(awg.Address) : new List<string> { "10.13.13.2/32" },
            PrivateKey = awg.PrivateKey,
            Jc = awg.Jc, Jmin = awg.Jmin, Jmax = awg.Jmax,
            S1 = awg.S1, S2 = awg.S2, S3 = awg.S3, S4 = awg.S4,
            H1 = NullIfEmpty(awg.H1), H2 = NullIfEmpty(awg.H2), H3 = NullIfEmpty(awg.H3), H4 = NullIfEmpty(awg.H4),
            I1 = NullIfEmpty(awg.I1), I2 = NullIfEmpty(awg.I2), I3 = NullIfEmpty(awg.I3),
            I4 = NullIfEmpty(awg.I4), I5 = NullIfEmpty(awg.I5),
            Peers = new List<WireGuardPeer>
            {
                new()
                {
                    Address                     = entry.Server,
                    Port                        = entry.Port,
                    PublicKey                   = awg.PeerPublicKey,
                    PreSharedKey                = NullIfEmpty(awg.PresharedKey),
                    AllowedIps                  = new List<string> { "0.0.0.0/0" },
                    PersistentKeepaliveInterval = awg.Keepalive > 0 ? awg.Keepalive : 25,
                }
            }
        };
    }

    /// <summary>
    /// TUIC v5 outbound. ALPN defaults to <c>["h3"]</c> per TUIC spec.
    /// </summary>
    private static SingBoxOutbound BuildTuicOutbound(VlessServerEntry entry, string tag)
    {
        var tls = new TlsConfig
        {
            Enabled    = true,
            ServerName = string.IsNullOrEmpty(entry.Tls?.ServerName) ? entry.Server : entry.Tls.ServerName,
            Insecure   = entry.Tls?.Insecure ?? false,
            Alpn       = ParseAlpnList(entry.Tls?.Alpn) ?? new List<string> { "h3" },
        };

        return new SingBoxOutbound
        {
            Type              = "tuic",
            Tag               = tag,
            Server            = entry.Server,
            ServerPort        = entry.Port,
            Uuid              = entry.Uuid,
            Password          = entry.Password,
            CongestionControl = string.IsNullOrEmpty(entry.CongestionControl) ? "bbr" : entry.CongestionControl,
            UdpRelayMode      = string.IsNullOrEmpty(entry.UdpRelayMode) ? "native" : entry.UdpRelayMode,
            Tls               = tls,
            // 2026-06-08 (scout #2 #6): TUIC dials its server over QUIC/UDP — same
            // IPv6-less-host hazard as Hysteria2/naive. prefer_ipv4 server resolution.
            DomainResolver    = new DomainResolverValue("local-dns", "prefer_ipv4"),
        };
    }

    /// <summary>
    /// Shadowsocks outbound. Supports SS 2022 ciphers natively via
    /// <see cref="VlessServerEntry.Method"/>. When
    /// <see cref="VlessServerEntry.Plugin"/> is "shadow-tls" (or any
    /// other plugin name sing-box recognises), emits the plugin /
    /// plugin_opts pair and lets sing-box wire it up.
    /// </summary>
    private static SingBoxOutbound BuildShadowsocksOutbound(VlessServerEntry entry, string tag)
    {
        return new SingBoxOutbound
        {
            Type           = "shadowsocks",
            Tag            = tag,
            Server         = entry.Server,
            ServerPort     = entry.Port,
            Method         = entry.Method,
            Password       = entry.Password,
            Plugin         = string.IsNullOrEmpty(entry.Plugin) ? null : entry.Plugin,
            PluginOpts     = string.IsNullOrEmpty(entry.PluginOpts) ? null : entry.PluginOpts,
            DomainResolver = "local-dns",
        };
    }

    /// <summary>
    /// NaiveProxy outbound. sing-box 1.13's naive outbound is deliberately
    /// minimal — username/password basic auth + a plain TLS block. It does
    /// NOT accept <c>tls.insecure=true</c>, uTLS, or <c>alpn</c> (sing-box
    /// rejects them at outbound init), so the TLS here is just
    /// <c>{enabled, server_name}</c> (insecure defaults to false, which IS
    /// accepted). Requires <c>libcronet.{dll,so}</c> next to the sing-box
    /// binary → Windows + Linux only (SagerNet ships no macOS Cronet, on any
    /// version). macOS naive servers are filtered out before generation so we
    /// never emit a config that FATALs at sing-box start.
    /// </summary>
    private static SingBoxOutbound BuildNaiveOutbound(VlessServerEntry entry, string tag)
    {
        return new SingBoxOutbound
        {
            Type           = "naive",
            Tag            = tag,
            Server         = entry.Server,
            ServerPort     = entry.Port,
            Username       = entry.Username,
            Password       = entry.Password,
            Quic           = entry.NaiveQuic ? true : (bool?)null, // r7 #1: HTTP/3 over QUIC
            Tls            = new TlsConfig
            {
                Enabled    = true,
                ServerName = string.IsNullOrEmpty(entry.Tls?.ServerName) ? entry.Server : entry.Tls.ServerName,
            },
            // 2026-06-08 (Pavel "Latvia NAIVE" run): force IPv4-first server
            // resolution via the 1.13 domain_resolver object form. naive_quic
            // dials the server over UDP/QUIC; on an IPv6-less host sing-box was
            // picking the server's AAAA and failing with "open UDP connection to
            // [2001:...]: address not valid in its context" (17x). prefer_ipv4
            // tries the A record first, falling back to IPv6 only if there's no
            // A — safe for IPv6-only servers too. (The legacy top-level
            // domain_strategy outbound option is FATAL in sing-box 1.13.)
            DomainResolver = new DomainResolverValue("local-dns", "prefer_ipv4"),
        };
    }

    private static List<string>? ParseAlpnList(string? alpn)
    {
        if (string.IsNullOrWhiteSpace(alpn)) return null;
        return alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    // ─── Transport ────────────────────────────────────────────────────────────

    private static TransportConfig BuildTransportConfig(string type, VlessTransportConfig source)
    {
        var isGrpc = type.Equals("grpc", StringComparison.OrdinalIgnoreCase);

        // XHTTP (sing-box-lx with_xhttp): VLESS over plain HTTP/2, composes with Reality,
        // incompatible with XTLS-Vision. host is a TOP-LEVEL field (not in headers). Schema
        // verified vs `sing-box-lx check`. See plans/amneziawg-fork-implementation-plan-2026-06-27.md.
        if (type.Equals("xhttp", StringComparison.OrdinalIgnoreCase))
        {
            return new TransportConfig
            {
                Type          = "xhttp",
                Mode          = string.IsNullOrEmpty(source.Mode) ? "auto" : source.Mode,
                Path          = string.IsNullOrEmpty(source.Path) ? "/" : source.Path,
                Host          = string.IsNullOrEmpty(source.Host) ? null : source.Host,
                XPaddingBytes = string.IsNullOrEmpty(source.XPaddingBytes) ? null : source.XPaddingBytes,
                NoGrpcHeader  = source.NoGrpcHeader,
                Headers       = source.Headers?.Count > 0 ? source.Headers : null,
            };
        }

        return new TransportConfig
        {
            Type        = type,
            // gRPC: service_name (no path, no headers)
            // WS: path + headers
            Path        = isGrpc ? null : source.Path,
            ServiceName = isGrpc ? source.Path : null,
            Headers     = isGrpc ? null : (source.Headers?.Count > 0 ? source.Headers : null)
        };
    }

    // ─── TLS / Reality ────────────────────────────────────────────────────────

    private static TlsConfig BuildTlsConfig(VlessServerEntry entry)
    {
        var security = entry.Security ?? "reality";
        var isReality = security.Equals("reality", StringComparison.OrdinalIgnoreCase);

        if (isReality)
        {
            var reality = entry.Reality ?? new VlessRealityConfig();
            return new TlsConfig
            {
                Enabled    = true,
                ServerName = reality.ServerName,
                Insecure   = false,
                Utls = new UtlsConfig
                {
                    Enabled     = true,
                    Fingerprint = reality.Fingerprint
                },
                Reality = new RealityConfig
                {
                    Enabled   = true,
                    PublicKey = reality.PublicKey,
                    // v2.40.0-r9 (#2 core-audit): drop a structurally-invalid short_id.
                    // sing-box's hex.Decode PANICS (index out of range) on a Reality
                    // short_id > 8 bytes (16 hex chars) — a 10/20-hex sid from a
                    // copy-paste/generator bug would crash sing-box at config load AND
                    // crash-loop the HealthMonitor Advisory reload (→ routed traffic
                    // falls direct). An empty short_id is valid, so degrade to "" → a
                    // clean handshake attempt instead of a panic.
                    ShortId   = VlessUriParser.IsValidRealityShortId(reality.ShortId)
                                    ? reality.ShortId : string.Empty
                },
                // TLS record fragmentation: splits ClientHello across multiple TLS
                // records to bypass DPI that inspects the first record for SNI.
                // Available since sing-box 1.12.0. Falls back to normal handshake
                // if fragmented attempt doesn't complete within 500ms.
                RecordFragment = true,
                FragmentFallbackDelay = "500ms"
            };
        }

        // Plain TLS (e.g. VLESS+WS+TLS via CDN)
        var tls = entry.Tls ?? new VlessTlsConfig();
        var tlsConfig = new TlsConfig
        {
            Enabled    = tls.Enabled,
            ServerName = tls.ServerName,
            Insecure   = tls.Insecure
        };

        // uTLS fingerprint (critical for Cloudflare CDN — without it, handshake fails)
        if (!string.IsNullOrEmpty(tls.Fingerprint))
        {
            tlsConfig.Utls = new UtlsConfig
            {
                Enabled = true,
                Fingerprint = tls.Fingerprint
            };
        }

        // ALPN (e.g. "http/1.1" for WebSocket, "h2" for gRPC)
        if (!string.IsNullOrEmpty(tls.Alpn))
        {
            tlsConfig.Alpn = tls.Alpn
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        return tlsConfig;
    }

    // ─── Route (sing-box 1.12+ action-based format) ──────────────────────────

    private static SingBoxRoute BuildRoute(Profile profile, List<string> processes,
        string routingMode = "split", bool hasUdpProxy = false, bool isExcludeMode = false,
        bool blockQuicOnTcpProxy = true, bool isDnsTunnel = false,
        List<string>? dnsTunnelResolverIps = null, bool proxyIsUdpNative = false)
    {
        var isFullTunnel = (routingMode ?? "split").Equals("full", StringComparison.OrdinalIgnoreCase);

        var rules = new List<RouteRule>
        {
            // Protocol sniffing: detect HTTP/TLS/QUIC and override destination with sniffed domain.
            // Replaces deprecated inbound-level sniff + sniff_override_destination (removed in 1.13).
            new() { Action = "sniff", Timeout = "300ms" },
        };

        // DNS-tunnel (slipstream) self-exclusion — MUST precede hijack-dns AND the
        // proxy final. The slipstream-client front (127.0.0.1:7001) carries the
        // VLESS stream, but its OWN upstream packets to the DNS resolvers would
        // otherwise be (a) hijacked by the DNS module (they're DNS on :53) or
        // (b) routed to final=proxy=127.0.0.1:7001 = itself → deadlock
        // ("dial tcp 127.0.0.1:7001 i/o timeout", all DNS hangs, no internet).
        // Android excludes the whole app via VpnService; on Windows/Linux
        // slipstream is a SEPARATE process, so exclude it here by resolver-IP
        // (destination-based, reliable even before sniff) AND process_name
        // (covers DoH/DoT resolvers on non-:53 ports). IsInfrastructure keeps
        // FindCustomRulesInsertionPoint treating these as the leading block.
        if (isDnsTunnel)
        {
            if (dnsTunnelResolverIps is { Count: > 0 })
                rules.Add(new RouteRule
                {
                    IpCidr           = dnsTunnelResolverIps,
                    Action           = "route",
                    Outbound         = "direct",
                    IsInfrastructure = true,
                });
            rules.Add(new RouteRule
            {
                ProcessName      = new List<string> { SlipstreamProcessName },
                Action           = "route",
                Outbound         = "direct",
                IsInfrastructure = true,
            });
        }

        // DNS traffic: hijack and resolve through DNS module (replaces "dns" outbound)
        rules.Add(new RouteRule { Protocol = "dns", Action = "hijack-dns" });

        // Private IPs always direct — MUST be before process/default rules so that
        // traffic to local/VPN subnets (WireGuard, AmneziaWG, LAN) is never
        // sent through the remote proxy, in both split and full tunnel modes.
        rules.Add(new RouteRule
        {
            IpIsPrivate = true,
            Action      = "route",
            Outbound    = "direct"
        });

        // YouTube / QUIC fix: when the proxy is TCP-only (VLESS+Reality+Vision
        // with no UDP-capable TUIC/Hysteria2 sibling), QUIC (HTTP/3 over UDP/443)
        // tunneled over the reliable VLESS-over-TCP stream suffers head-of-line
        // blocking ("TCP-over-TCP meltdown") → YouTube/google-video stalls and
        // buffering. Because QUIC is slow-not-rejected, the browser keeps
        // retrying it instead of falling back. A clean reject forces the
        // fallback to HTTP/2-over-TCP, which rides VLESS cleanly. The sniff rule
        // above identifies QUIC; private-IP traffic is already routed direct, so
        // LAN QUIC is untouched. Skipped when a UDP-capable outbound exists
        // (proxy-udp) — there we honour the user's deliberate UDP routing. Also
        // skipped for a UDP-native tunnel (AmneziaWG / WireGuard endpoint): it
        // carries QUIC over real UDP, so there is no TCP-over-TCP meltdown to
        // pre-empt — rejecting QUIC would needlessly force HTTP/3 apps to TCP.
        if (blockQuicOnTcpProxy && !hasUdpProxy && !proxyIsUdpNative)
        {
            if (isFullTunnel || isExcludeMode)
            {
                // final = "proxy": (almost) all traffic rides the TCP-only proxy.
                rules.Add(new RouteRule { Protocol = "quic", Action = "reject" });
            }
            else if (processes.Count > 0)
            {
                // Split include: only the listed apps ride the TCP-only proxy,
                // so scope the QUIC reject to them — other apps keep QUIC direct.
                rules.Add(new RouteRule
                {
                    ProcessName = processes.ToList(),
                    Protocol    = "quic",
                    Action      = "reject"
                });
            }
        }

        // AM-1: when the user is in exclude mode under split tunnel, the
        // listed processes get routed to "direct" (kept OUT of the
        // tunnel) and route.final flips to "proxy" so everything else
        // goes through the VPN. Otherwise we keep the legacy semantics:
        // include mode in split tunnel routes the listed processes
        // through proxy + final=direct; full tunnel routes everything
        // through proxy and ignores the per-app list.
        if (!isFullTunnel && processes.Count > 0)
        {
            var perAppOutbound = isExcludeMode ? "direct" : "proxy";
            if (hasUdpProxy && !isExcludeMode)
            {
                // Dual outbound only matters when sending through proxy;
                // for the exclude path the destination is always
                // "direct" so the TCP/UDP split is meaningless.
                rules.Add(new RouteRule
                {
                    ProcessName = processes.ToList(),
                    Network     = "udp",
                    Action      = "route",
                    Outbound    = "proxy-udp"
                });
                rules.Add(new RouteRule
                {
                    ProcessName = processes.ToList(),
                    Network     = "tcp",
                    Action      = "route",
                    Outbound    = "proxy"
                });
            }
            else
            {
                // Single outbound: listed traffic → proxy (include) or → direct (exclude)
                rules.Add(new RouteRule
                {
                    ProcessName = processes.ToList(),
                    Action      = "route",
                    Outbound    = perAppOutbound
                });
            }
        }
        else if (isFullTunnel && hasUdpProxy)
        {
            // Full tunnel with UDP split: UDP → proxy-udp, TCP handled by Final
            rules.Add(new RouteRule
            {
                Network  = "udp",
                Action   = "route",
                Outbound = "proxy-udp"
            });
        }
        // Full tunnel without UDP split: no process-specific rules — Final = "proxy" handles everything

        // route.final defaults to "direct" in include mode (split), to
        // "proxy" in full tunnel OR exclude mode. In exclude split mode
        // the per-app rules above pin the user's exclude list to
        // direct, and the final rule sends everything else through the
        // VPN. Full tunnel always lands on proxy regardless of
        // isExcludeMode (no per-app filtering when everything is
        // tunnelled).
        string finalOutbound;
        if (isFullTunnel)
            finalOutbound = "proxy";
        else if (isExcludeMode)
            finalOutbound = "proxy";
        else
            finalOutbound = "direct";

        return new SingBoxRoute
        {
            Rules                   = rules,
            Final                   = finalOutbound,
            AutoDetectInterface     = true,
            // Required since sing-box 1.12, mandatory in 1.14
            DefaultDomainResolver   = "local-dns"
        };
    }
    // ─── vpn-dns resolver (tunnelled; Detour="proxy") ────────────────────────────

    /// <summary>
    /// The resolver whose queries ride the proxy tunnel. For a TCP tunnel
    /// (VLESS/Reality) we use DoH — extra privacy from the exit, and TCP MSS
    /// auto-clamps so the TLS handshake survives the path. For a UDP-native tunnel
    /// (AmneziaWG/WireGuard) the DoH TLS handshake's large ServerHello flight
    /// blackholes on the fixed 1280 endpoint MTU (diag 20260701-122336: cold DoH
    /// exchanges 12-56s -> Dota region pings time out), so we resolve via PLAIN UDP
    /// inside the already-encrypted tunnel: one small packet each way, no handshake,
    /// no DoH-hostname bootstrap, leak-safe (never leaves the tunnel). AdGuard's
    /// plain-DNS IP keeps ad-blocking when BlockAds is on.
    /// </summary>
    private static DnsServer BuildVpnDnsServer(AppSettings settings, bool proxyIsUdpNative)
    {
        if (proxyIsUdpNative)
        {
            return new DnsServer
            {
                Tag    = "vpn-dns",
                Type   = "udp",
                // AdGuard "Default" plain-DNS (ad + tracker + malware blocking) when
                // BlockAds is on; else the user's VPN DNS reduced to a literal IP.
                Server = settings.App.BlockAds ? "94.140.14.14" : ToPlainDnsIp(settings.Dns.VpnDns),
                Detour = "proxy"
            };
        }

        // Remote DoH server routed through VPN proxy.
        // When BlockAds is on, use AdGuard DNS (blocks ads + trackers + malware).
        // Otherwise use user-configured VPN DNS.
        return new DnsServer
        {
            Tag        = "vpn-dns",
            Type       = "https",
            Server     = settings.App.BlockAds ? "dns.adguard-dns.com" : ParseDohHost(settings.Dns.VpnDns),
            ServerPort = settings.App.BlockAds ? 443 : ParseDohPort(settings.Dns.VpnDns),
            Path       = settings.App.BlockAds ? "/dns-query" : ParseDohPath(settings.Dns.VpnDns),
            Detour     = "proxy",
            // Bootstrap the DoH hostname without asking vpn-dns to resolve
            // itself. The DoH exchange still rides the proxy via Detour.
            DomainResolver = "local-dns"
        };
    }

    /// <summary>
    /// Reduce a DoH URL to a literal IP for plain-UDP DNS (which cannot bootstrap
    /// a hostname over the tunnel without a loop). Falls back to Cloudflare 1.1.1.1
    /// when the configured VPN DNS is a hostname rather than an IP literal.
    /// </summary>
    private static string ToPlainDnsIp(string dohUrl)
    {
        var host = ParseDohHost(dohUrl);
        return System.Net.IPAddress.TryParse(host, out _) ? host : "1.1.1.1";
    }

    // ─── DoH URL parsing helpers ──────────────────────────────────────────────────

    /// <summary>Extract hostname from a DoH URL like https://1.1.1.1/dns-query</summary>
    private static string ParseDohHost(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.Host;
        return url; // fallback: return as-is
    }

    /// <summary>Extract port from a DoH URL (default 443 for https)</summary>
    private static int? ParseDohPort(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            if (uri.Port > 0 && !uri.IsDefaultPort)
                return uri.Port;
            return null; // let sing-box use default
        }
        return null;
    }

    /// <summary>Extract path from a DoH URL (e.g. /dns-query)</summary>
    private static string ParseDohPath(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return string.IsNullOrEmpty(uri.AbsolutePath) ? "/dns-query" : uri.AbsolutePath;
        return "/dns-query";
    }

}
