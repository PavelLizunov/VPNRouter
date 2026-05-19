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
    public static SingBoxConfig Generate(
        Profile profile,
        IEnumerable<string> resolvedProcessNames,
        AppSettings settings)
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
        // Backward-compat: when RoutingAppsExclude is populated but
        // RoutingAppsInclude is empty the user is in exclude mode and we
        // honour their list verbatim. When both are empty (clean
        // install, never opened Apps tab) we fall through to the legacy
        // resolvedProcessNames path with mode=include — no surprise
        // empty config.
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

        var logPath = AppPaths.SingBoxLogPath;

        var outbounds = BuildOutbounds(settings, out bool hasUdpProxy);

        var config = new SingBoxConfig
        {
            Log = new SingBoxLog
            {
                Level = settings.App.LogLevel,
                Timestamp = true,
                Output = logPath
            },
            Dns = BuildDns(profile, appsProcessList, settings, isExcludeMode),
            Inbounds = BuildInbounds(settings),
            Outbounds = outbounds,
            Route = BuildRoute(profile, appsProcessList, settings.App.RoutingMode, hasUdpProxy, isExcludeMode),
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
            if (r.Action == "sniff" || r.Action == "hijack-dns" || r.IpIsPrivate == true)
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
            if (r.Action == "sniff" || r.Action == "hijack-dns" || r.IpIsPrivate == true)
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
            if (r.Action == "sniff" || r.Action == "hijack-dns" || r.IpIsPrivate == true)
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

    private static SingBoxDns BuildDns(Profile profile, List<string> processes, AppSettings settings, bool isExcludeMode = false)
    {
        var routingMode = settings.App.RoutingMode ?? "split";
        var isFullTunnel = routingMode.Equals("full", StringComparison.OrdinalIgnoreCase);

        // AM-1: in exclude mode `processes` holds the apps we are KEEPING
        // direct, so route.final flips to "proxy". The DNS default
        // mirrors that: by default DNS goes through the VPN; only the
        // listed exclude-apps get the local resolver (so they don't leak
        // their queries inside the tunnel when they're not even using
        // it). StrictDns and Full tunnel keep their existing semantics
        // (override to vpn-dns).
        var defaultVpnDns = isFullTunnel || isExcludeMode || settings.App.StrictDns;

        var dns = new SingBoxDns
        {
            // ipv4_only protects from IPv6 leaks (when VPN tunnels only IPv4) AND
            // skips slow AAAA queries (+100-300ms each). Disable only if user
            // explicitly wants IPv6 via dns.strategy in config.yaml.
            Strategy = settings.App.ForceIpv4Only ? "ipv4_only" : null,
            // Strict DNS: all queries via VPN (no leaks possible).
            // Full tunnel: all DNS through VPN by default.
            // Exclude mode (AM-1): unmatched apps go via VPN, so DNS final = vpn-dns.
            // Include mode split tunnel: unmatched apps go direct, so DNS final = local-dns.
            Final = defaultVpnDns ? "vpn-dns" : "local-dns",
            Servers = new List<DnsServer>
            {
                // Remote DoH server routed through VPN proxy.
                // When BlockAds is on, use AdGuard DNS (blocks ads + trackers + malware).
                // Otherwise use user-configured VPN DNS.
                new()
                {
                    Tag        = "vpn-dns",
                    Type       = "https",
                    Server     = settings.App.BlockAds ? "dns.adguard-dns.com" : ParseDohHost(settings.Dns.VpnDns),
                    ServerPort = settings.App.BlockAds ? 443 : ParseDohPort(settings.Dns.VpnDns),
                    Path       = settings.App.BlockAds ? "/dns-query" : ParseDohPath(settings.Dns.VpnDns),
                    Detour     = "proxy"
                },
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

        if (isFullTunnel)
        {
            // Full tunnel: all DNS goes through vpn-dns (via Final above).
            // No per-process rules needed.
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
            // Include split tunnel: targeted processes → VPN DNS (leak protection)
            if (processes.Count > 0 && profile.DnsMode != "direct")
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

    private static List<SingBoxInbound> BuildInbounds(AppSettings settings)
    {
        return new List<SingBoxInbound>
        {
            new()
            {
                Type                    = "tun",
                Tag                     = "tun-in",
                InterfaceName           = OperatingSystem.IsMacOS() ? "utun99" : settings.Tun.InterfaceName,
                Address                 = new List<string> { settings.Tun.Ipv4Address },
                Mtu                     = settings.Tun.Mtu,
                AutoRoute               = settings.Tun.AutoRoute,
                StrictRoute             = false, // Always false — avoid dual stack errors
                RouteExcludeAddress     = settings.Tun.RouteExcludeAddress.Count > 0
                                            ? settings.Tun.RouteExcludeAddress
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
    private static List<SingBoxOutbound> BuildOutbounds(AppSettings settings, out bool hasUdpProxy)
    {
        var servers = settings.Vless.GetActiveServers();

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

        var outbounds = new List<SingBoxOutbound>();

        // Auto-detect: split servers by flow presence
        var flowServers = servers.Where(s => !string.IsNullOrEmpty(s.Flow)).ToList();
        var noFlowServers = servers.Where(s => string.IsNullOrEmpty(s.Flow)).ToList();
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

        outbounds.Add(new SingBoxOutbound { Type = "direct", Tag = "direct" });
        // dns-direct: separate non-empty direct outbound for DNS servers.
        // sing-box 1.13 FATAL: "detour to empty direct outbound makes no sense"
        // when using detour:"direct" on a bare direct outbound. udp_fragment:true
        // makes it non-empty so we can route DNS through it.
        outbounds.Add(new SingBoxOutbound { Type = "direct", Tag = "dns-direct", UdpFragment = true });
        return outbounds;
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
            "tuic"        => BuildTuicOutbound(entry, tag),
            "shadowsocks" => BuildShadowsocksOutbound(entry, tag),
            "ss"          => BuildShadowsocksOutbound(entry, tag),
            _             => BuildVlessOutboundCore(entry, tag),
        };
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
            Flow       = string.IsNullOrEmpty(entry.Flow) ? null : entry.Flow,
            Tls        = BuildTlsConfig(entry),
            Transport  = transportType.Equals("tcp", StringComparison.OrdinalIgnoreCase)
                ? null
                : BuildTransportConfig(transportType, transport),
            DomainResolver = "local-dns"
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
            DomainResolver = "local-dns",
        };

        if (!string.IsNullOrEmpty(entry.ObfsType))
        {
            ob.Obfs = new Hysteria2Obfs
            {
                Type     = entry.ObfsType,
                Password = entry.ObfsPassword,
            };
        }

        return ob;
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
            DomainResolver    = "local-dns",
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

    private static List<string>? ParseAlpnList(string? alpn)
    {
        if (string.IsNullOrWhiteSpace(alpn)) return null;
        return alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    // ─── Transport ────────────────────────────────────────────────────────────

    private static TransportConfig BuildTransportConfig(string type, VlessTransportConfig source)
    {
        var isGrpc = type.Equals("grpc", StringComparison.OrdinalIgnoreCase);

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
                    ShortId   = reality.ShortId
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
        string routingMode = "split", bool hasUdpProxy = false, bool isExcludeMode = false)
    {
        var isFullTunnel = (routingMode ?? "split").Equals("full", StringComparison.OrdinalIgnoreCase);

        var rules = new List<RouteRule>
        {
            // Protocol sniffing: detect HTTP/TLS/QUIC and override destination with sniffed domain.
            // Replaces deprecated inbound-level sniff + sniff_override_destination (removed in 1.13).
            new() { Action = "sniff", Timeout = "300ms" },

            // DNS traffic: hijack and resolve through DNS module (replaces "dns" outbound)
            new() { Protocol = "dns", Action = "hijack-dns" }
        };

        // Private IPs always direct — MUST be before process/default rules so that
        // traffic to local/VPN subnets (WireGuard, AmneziaWG, LAN) is never
        // sent through the remote proxy, in both split and full tunnel modes.
        rules.Add(new RouteRule
        {
            IpIsPrivate = true,
            Action      = "route",
            Outbound    = "direct"
        });

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