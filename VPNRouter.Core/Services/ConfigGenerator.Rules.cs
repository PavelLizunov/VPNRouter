using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static partial class ConfigGenerator
{
    internal const string AdBlockRuleSetTag = "vpnrouter-adblock";
    private const string AdBlockRuleSetUrl =
        "https://raw.githubusercontent.com/REIJI007/AdBlock_Rule_For_Sing-box/main/adblock_reject.srs";
    private const string AdBlockRuleSetFilename = "adblock_reject.srs";

    internal static void RejectMissingRuleSet(string reason) => throw new InvalidOperationException(reason);

    internal static string? ResolveAdBlock(Func<string, string, string?> resolveRuleSet,
        Action<string>? onMissingRuleSet)
    {
        var localPath = resolveRuleSet(AdBlockRuleSetUrl, AdBlockRuleSetFilename);
        if (string.IsNullOrWhiteSpace(localPath))
        {
            onMissingRuleSet?.Invoke("Ad filtering requires a readable ad-block rule-set.");
            Serilog.Log.Logger.Warning(
                "[ConfigGenerator] AdBlock rule-set unavailable (offline + no cache); generating config WITHOUT ad blocking");
            return null;
        }
        return localPath;
    }

    private static void ApplyAdBlock(SingBoxConfig config, Func<string, string, string?> resolveRuleSet,
        Action<string>? onMissingRuleSet)
    {
        var localPath = ResolveAdBlock(resolveRuleSet, onMissingRuleSet);
        if (localPath == null) return;

        config.Route.RuleSet ??= new List<RuleSetEntry>();
        config.Route.RuleSet.Add(new RuleSetEntry
        {
            Type = "local",
            Tag = AdBlockRuleSetTag,
            Format = "binary",
            Path = localPath,
        });

        config.Dns.Rules.Insert(0, new DnsRule
        {
            RuleSet = new List<string> { AdBlockRuleSetTag },
            Action = "reject"
        });

        config.Route.Rules.Insert(FindCustomRulesInsertionPoint(config), new RouteRule
        {
            RuleSet = new List<string> { AdBlockRuleSetTag },
            Action = "reject"
        });
    }

    internal static bool RequiresLocalRuleSets(CustomRule rule) =>
        rule.Enabled &&
        (string.Equals(rule.Type, "geosite", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(rule.Type, "geoip", StringComparison.OrdinalIgnoreCase)) &&
        (string.Equals(rule.Action, "block", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(rule.Action, "proxy", StringComparison.OrdinalIgnoreCase));

    internal static void ApplyCustomRules(SingBoxConfig config, List<CustomRule> rules,
        Func<string, string, string?>? resolveRuleSet = null)
    {
        resolveRuleSet ??= (url, file) => RuleSetCacheManager.EnsureLocal(url, file);
        var entries = new List<RuleSetEntry>();
        var routes = new List<RouteRule>();
        var dnsRules = new List<DnsRule>();
        var available = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var existing in config.Route.RuleSet ?? [])
            available[existing.Tag] = true;

        bool Register(string type, string tag)
        {
            if (available.TryGetValue(tag, out var found))
                return found;

            var filename = tag + ".srs";
            var upstreamFile = filename["user-".Length..];
            var url = $"https://raw.githubusercontent.com/SagerNet/sing-{type.ToLowerInvariant()}/rule-set/{upstreamFile}";
            var path = resolveRuleSet(url, filename);
            found = !string.IsNullOrEmpty(path);
            available.Add(tag, found);
            if (found)
                entries.Add(new RuleSetEntry { Tag = tag, Path = path });
            return found;
        }

        foreach (var rule in rules)
        {
            if (!rule.Enabled) continue;
            var route = BuildCustomRouteRule(rule);
            if (route == null) throw new InvalidOperationException("Invalid enabled custom routing rule.");

            if (route.RuleSet is { } requested)
            {
                var registered = new List<string>();
                foreach (var tag in requested)
                {
                    if (Register(rule.Type, tag))
                        registered.Add(tag);
                    else if (RequiresLocalRuleSets(rule))
                        throw new InvalidOperationException($"Custom {rule.Action} rule requires unavailable rule-set '{tag}'.");
                    else
                        Serilog.Log.Logger.Warning("[ConfigGenerator] Skipping unavailable direct rule-set {Tag}", tag);
                }
                if (registered.Count == 0) continue;
                route.RuleSet = registered;
            }

            routes.Add(route);
            if ((route.Action == "reject" && IsDomainTypeForDns(rule.Type)) ||
                (route.Outbound == "proxy" && route.RuleSet != null && IsDomainTypeForDns(rule.Type)))
            {
                dnsRules.Add(new DnsRule
                {
                    Action = route.Action, Server = route.Outbound == "proxy" ? "vpn-dns" : null,
                    Domain = route.Domain, DomainSuffix = route.DomainSuffix,
                    DomainKeyword = route.DomainKeyword, RuleSet = route.RuleSet,
                });
            }
        }

        if (entries.Count > 0)
            (config.Route.RuleSet ??= []).AddRange(entries);
        config.Route.Rules.InsertRange(FindCustomRulesInsertionPoint(config), routes);
        config.Dns.Rules.InsertRange(0, dnsRules);
    }

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

    internal static RouteRule? BuildCustomRouteRule(CustomRule rule)
    {
        var normalized = new CustomRule { Action = rule.Action, Type = rule.Type, Value = rule.Value };
        if (CustomRulesParser.Normalize(normalized) != null) return null;
        var values = normalized.Value.Split(',', StringSplitOptions.TrimEntries).ToList();

        var route = new RouteRule();

        switch (normalized.Action)
        {
            case "direct":
                route.Action = "route";
                route.Outbound = "direct";
                break;
            case "proxy":
                route.Action = "route";
                route.Outbound = "proxy";
                break;
            case "block":
                route.Action = "reject";
                break;
            default:
                return null;
        }

        switch (normalized.Type)
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
                route.Port = values.Select(v => int.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToList();
                break;
            case "port_range":
                route.PortRange = values.Select(v => CustomRulesParser.ToSingBoxPortRange(v)!).ToList();
                break;
            case "network":
                route.Network = string.Join(",", values.Select(v => v.ToLowerInvariant()).Distinct());
                break;
            case "process_name":
                route.ProcessName = values;
                break;
            case "geosite":
            case "geoip":
                var tagPrefix = normalized.Type == "geosite"
                    ? "user-geosite-" : "user-geoip-";
                route.RuleSet = values.Select(v => tagPrefix + v).Distinct(StringComparer.Ordinal).ToList();
                break;
            default:
                return null;
        }
        return route;
    }

    private static bool IsDomainTypeForDns(string? type) => (type ?? "domain_suffix").ToLowerInvariant() switch
    {
        "domain" or "domain_suffix" or "domain_keyword" or "geosite" => true,
        _ => false,
    };

    private static readonly string[] MacHelperSuffixes =
    {
        " Helper",
        " Helper (GPU)",
        " Helper (Renderer)",
        " Helper (Plugin)",
    };

    private static readonly Dictionary<string, string[]> MacKnownIoProcesses =
        new(StringComparer.Ordinal)
        {
            ["Safari"] = new[]
            {
                "com.apple.WebKit.Networking",
                "com.apple.WebKit.WebContent",
                "com.apple.WebKit.GPU",
                "com.apple.Safari.SearchHelper",
            },
        };

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

            if (name.Contains(" Helper", StringComparison.Ordinal)) continue;

            if (MacKnownIoProcesses.TryGetValue(name, out var ioNames))
            {
                foreach (var io in ioNames) Add(io);
                continue;
            }

            foreach (var suffix in MacHelperSuffixes)
                Add(name + suffix);
        }
        return result;
    }

    private const string GeoIpRuleSetTag = "vpnrouter-geoip-ru";
    private const string GeoSiteRuleSetTag = "vpnrouter-geosite-ru";

    private static void ApplyGeoBypass(SingBoxConfig config)
    {
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

        config.Dns.Rules.Insert(0, new DnsRule
        {
            RuleSet = new List<string> { GeoSiteRuleSetTag },
            Action = "route",
            Server = "vpn-dns"
        });

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

        config.Route.Rules.Insert(insertAt, new RouteRule
        {
            RuleSet = new List<string> { GeoSiteRuleSetTag, GeoIpRuleSetTag },
            Action = "route",
            Outbound = "direct"
        });
    }

}
