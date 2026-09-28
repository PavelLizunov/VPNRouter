using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static partial class ConfigGenerator
{
    private const string AdBlockRuleSetTag = "vpnrouter-adblock";
    private const string AdBlockRuleSetUrl =
        "https://raw.githubusercontent.com/REIJI007/AdBlock_Rule_For_Sing-box/main/adblock_reject.srs";
    private const string AdBlockRuleSetFilename = "adblock_reject.srs";

    private static void ApplyAdBlock(SingBoxConfig config)
    {
        var localPath = RuleSetCacheManager.EnsureLocal(
            AdBlockRuleSetUrl,
            AdBlockRuleSetFilename);
        if (string.IsNullOrEmpty(localPath))
        {
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

    internal static void ApplyCustomRules(SingBoxConfig config, List<CustomRule> rules)
    {
        int insertAt = FindCustomRulesInsertionPoint(config);

        for (int idx = rules.Count - 1; idx >= 0; idx--)
        {
            var rule = rules[idx];
            if (!rule.Enabled) continue;

            var isGeoType =
                rule.Type.Equals("geosite", StringComparison.OrdinalIgnoreCase) ||
                rule.Type.Equals("geoip", StringComparison.OrdinalIgnoreCase);
            if (isGeoType)
            {
                var registered = EnsureCustomRuleSetEntry(config, rule.Type, rule.Value);
                if (registered.Count == 0)
                {
                    continue;
                }
            }

            var built = BuildCustomRouteRule(rule);
            if (built == null) continue;
            config.Route.Rules.Insert(insertAt, built);

            if (rule.Action.Equals("block", StringComparison.OrdinalIgnoreCase)
                && IsDomainTypeForDns(rule.Type))
            {
                var dnsReject = BuildCustomDnsRejectRule(rule);
                if (dnsReject != null)
                    config.Dns.Rules.Insert(0, dnsReject);
            }
        }
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
        if (string.IsNullOrWhiteSpace(rule.Value)) return null;
        var values = rule.Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(v => v.Length > 0)
            .ToList();
        if (values.Count == 0) return null;

        var route = new RouteRule();

        switch ((rule.Action ?? "direct").ToLowerInvariant())
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
                var rangePorts = new List<int>();
                foreach (var v in values)
                {
                    if (TryParsePortRange(v, out var min, out var max))
                    {
                        var step = Math.Max(1, (max - min) / 50);
                        for (int p = min; p <= max; p += step) rangePorts.Add(p);
                        if (!rangePorts.Contains(max)) rangePorts.Add(max);
                    }
                }
                if (rangePorts.Count == 0) return null;
                route.Port = rangePorts.Distinct().Take(64).ToList();
                break;
            case "network":
                route.Network = values[0].ToLowerInvariant();
                break;
            case "process_name":
                route.ProcessName = values;
                break;
            case "geosite":
            case "geoip":
                var tagPrefix = rule.Type.Equals("geosite", StringComparison.OrdinalIgnoreCase)
                    ? "user-geosite-" : "user-geoip-";
                route.RuleSet = values.Select(v => tagPrefix + v).ToList();
                break;
            default:
                return null;
        }
        return route;
    }

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
            RuleSet = new List<string> { GeoSiteRuleSetTag },
            Action = "route",
            Outbound = "direct"
        });
    }

}
