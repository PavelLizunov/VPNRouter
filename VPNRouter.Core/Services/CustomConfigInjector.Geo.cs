using System.Text.Json.Nodes;

namespace VPNRouter.Core.Services;

public static partial class CustomConfigInjector
{
    private const string GeoIpRuleSetTag = "vpnrouter-geoip-ru";
    private const string GeoSiteRuleSetTag = "vpnrouter-geosite-ru";
    private const string LegacyDirectDnsRuTag = "vpnrouter-dns-ru";

    private static void InjectGeoBypassRules(JsonObject config, bool isActionBased, string proxyTag)
    {
        InjectGeoRuleSets(config);
        InjectGeoDnsRule(config, isActionBased, proxyTag);
        InjectGeoRouteRules(config, isActionBased);
    }

    private static void InjectGeoRuleSets(JsonObject config)
    {
        var route = config["route"] as JsonObject;
        if (route == null)
        {
            route = new JsonObject { ["rules"] = new JsonArray(), ["final"] = "direct" };
            config["route"] = route;
        }

        var ruleSet = route["rule_set"] as JsonArray;
        if (ruleSet == null)
        {
            ruleSet = new JsonArray();
            route["rule_set"] = ruleSet;
        }

        for (int i = ruleSet.Count - 1; i >= 0; i--)
        {
            var tag = StjNodeHelpers.AsString((ruleSet[i] as JsonObject)?["tag"]);
            if (tag == GeoIpRuleSetTag || tag == GeoSiteRuleSetTag)
                ruleSet.RemoveAt(i);
        }

        var geoIpPath = AppPaths.GeoIpRuPath.Replace('\\', '/');
        var geoSitePath = AppPaths.GeoSiteRuPath.Replace('\\', '/');

        ruleSet.Add((JsonNode?)new JsonObject
        {
            ["type"] = "local",
            ["tag"] = GeoIpRuleSetTag,
            ["format"] = "binary",
            ["path"] = geoIpPath
        });

        ruleSet.Add((JsonNode?)new JsonObject
        {
            ["type"] = "local",
            ["tag"] = GeoSiteRuleSetTag,
            ["format"] = "binary",
            ["path"] = geoSitePath
        });
    }

    private static void RemoveLegacyGeoDns(JsonObject config)
    {
        var dns = config["dns"] as JsonObject;
        if (dns == null) return;

        if (dns["servers"] is JsonArray servers)
        {
            for (int i = servers.Count - 1; i >= 0; i--)
            {
                if (StjNodeHelpers.AsString((servers[i] as JsonObject)?["tag"]) == LegacyDirectDnsRuTag)
                    servers.RemoveAt(i);
            }
        }

        if (dns["rules"] is JsonArray rules)
        {
            for (int i = rules.Count - 1; i >= 0; i--)
            {
                if (StjNodeHelpers.AsString((rules[i] as JsonObject)?["server"]) == LegacyDirectDnsRuTag)
                    rules.RemoveAt(i);
            }
        }

        if (StjNodeHelpers.AsString(dns["final"]) == LegacyDirectDnsRuTag)
            dns.Remove("final");
    }

    private static string EnsureGeoProxyDns(JsonObject config, string proxyTag)
    {
        var dns = config["dns"] as JsonObject;
        if (dns == null)
        {
            dns = new JsonObject();
            config["dns"] = dns;
        }

        var servers = dns["servers"] as JsonArray;
        if (servers == null)
        {
            servers = new JsonArray();
            dns["servers"] = servers;
        }

        return FindRemoteDnsTag(servers, config["outbounds"] as JsonArray, config["endpoints"] as JsonArray)
               ?? EnsureSynthesizedRemoteDns(servers, proxyTag);
    }

    private static void InjectGeoDnsRule(JsonObject config, bool isActionBased, string proxyTag)
    {
        var targetTag = EnsureGeoProxyDns(config, proxyTag);
        var dns = (JsonObject)config["dns"]!;
        var rules = dns["rules"] as JsonArray;
        if (rules == null)
        {
            rules = new JsonArray();
            dns["rules"] = rules;
        }

        for (int i = rules.Count - 1; i >= 0; i--)
        {
            var ruleSet = (rules[i] as JsonObject)?["rule_set"] as JsonArray;
            if (ruleSet?.Any(rs => StjNodeHelpers.AsString(rs) == GeoSiteRuleSetTag) == true)
                rules.RemoveAt(i);
        }

        var dnsRule = new JsonObject
        {
            ["rule_set"] = new JsonArray { (JsonNode?)JsonValue.Create(GeoSiteRuleSetTag) },
            ["server"] = targetTag
        };
        if (isActionBased)
            dnsRule["action"] = "route";

        rules.Insert(0, dnsRule);
    }

    private static void InjectGeoRouteRules(JsonObject config, bool isActionBased)
    {
        var route = config["route"] as JsonObject;
        if (route == null) return;

        var rules = route["rules"] as JsonArray;
        if (rules == null)
        {
            rules = new JsonArray();
            route["rules"] = rules;
        }

        for (int i = rules.Count - 1; i >= 0; i--)
        {
            var rule = rules[i] as JsonObject;
            if (rule == null) continue;

            var ruleSet = rule["rule_set"] as JsonArray;
            if (ruleSet == null) continue;

            bool isOurs = ruleSet.Any(rs =>
            {
                var s = StjNodeHelpers.AsString(rs);
                return s == GeoIpRuleSetTag || s == GeoSiteRuleSetTag;
            });

            if (isOurs)
                rules.RemoveAt(i);
        }

        int insertAt = FindGeoInsertIndex(rules, isActionBased);

        var geoRule = new JsonObject
        {
            ["rule_set"] = new JsonArray
            {
                (JsonNode?)JsonValue.Create(GeoSiteRuleSetTag),
                (JsonNode?)JsonValue.Create(GeoIpRuleSetTag),
            },
            ["outbound"] = "direct"
        };
        if (isActionBased)
            geoRule["action"] = "route";

        rules.Insert(insertAt, geoRule);
    }

    private static int FindGeoInsertIndex(JsonArray rules, bool isActionBased)
    {
        int index = 0;
        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i] as JsonObject;
            if (rule == null) continue;

            if (isActionBased)
            {
                var action = StjNodeHelpers.AsString(rule["action"]);
                if (action == "sniff" || action == "hijack-dns")
                {
                    index = i + 1;
                    continue;
                }
            }
            else
            {
                if (StjNodeHelpers.AsString(rule["protocol"]) == "dns")
                {
                    index = i + 1;
                    continue;
                }
            }

            if (StjNodeHelpers.AsBool(rule["ip_is_private"]) == true)
            {
                index = i + 1;
                continue;
            }

            if (rule["clash_mode"] != null)
            {
                index = i + 1;
                continue;
            }

            break;
        }

        return index;
    }
}
