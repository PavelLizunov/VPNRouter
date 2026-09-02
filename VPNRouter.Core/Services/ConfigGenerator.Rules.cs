using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static partial class ConfigGenerator
{
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

    // ─── Russian geo bypass ───────────────────────────────────────────────────

    private const string GeoIpRuleSetTag = "vpnrouter-geoip-ru";
    private const string GeoSiteRuleSetTag = "vpnrouter-geosite-ru";

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

        // 2. Censorship-sensitive domains always resolve through the existing
        // proxy-detoured DNS. The traffic route can still be direct, but neither
        // the ISP nor a country-specific resolver controls the DNS answer.
        config.Dns.Rules.Insert(0, new DnsRule
        {
            RuleSet = new List<string> { GeoSiteRuleSetTag },
            Action = "route",
            Server = "vpn-dns"
        });

        // 3. Add route rule: RU sites/IPs go direct (BEFORE process_name rules)
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
        // and the DNS rule above resolves those names through the encrypted
        // proxy instead of a resolver controlled by the local authority.
        // Adding geoip-ru on top was over-matching.
        //
        // Pure-IP Russian traffic (a direct IP connection with no DNS) is rare
        // and acceptable to leave going through VPN — the trade-off beats
        // breaking YouTube for everyone.
        config.Route.Rules.Insert(insertAt, new RouteRule
        {
            RuleSet = new List<string> { GeoSiteRuleSetTag },
            Action = "route",
            Outbound = "direct"
        });
    }

}
