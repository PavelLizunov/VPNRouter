using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static partial class CustomConfigInjector
{
    private static string FindProxyOutboundTag(JsonObject config)
    {
        if (config["outbounds"] is JsonArray outbounds)
        {
            foreach (var ob in outbounds)
            {
                if (ob is JsonObject obObj && StjNodeHelpers.AsString(obObj["type"]) == "selector")
                    return ResolveOrAssignProxyTag(obObj);
            }

            foreach (var ob in outbounds)
            {
                if (ob is JsonObject obObj && StjNodeHelpers.AsString(obObj["type"]) == "urltest")
                    return ResolveOrAssignProxyTag(obObj);
            }

            foreach (var ob in outbounds)
            {
                if (ob is not JsonObject obObj) continue;
                var type = StjNodeHelpers.AsString(obObj["type"]);
                if (type != "direct" && type != "block" && type != "dns")
                    return ResolveOrAssignProxyTag(obObj);
            }
        }

        if (config["endpoints"] is JsonArray endpoints)
        {
            foreach (var ep in endpoints)
            {
                if (ep is JsonObject epObj && StjNodeHelpers.AsString(epObj["type"]) == "wireguard")
                    return ResolveOrAssignProxyTag(epObj);
            }
        }

        return "custom-proxy";
    }

    private static string ResolveOrAssignProxyTag(JsonObject outbound)
    {
        var tag = StjNodeHelpers.AsString(outbound["tag"]);
        if (!string.IsNullOrEmpty(tag))
            return tag;

        outbound["tag"] = "custom-proxy";

        Serilog.Log.Logger.Warning(
            "Custom Config Mode: outbound without tag - using 'custom-proxy'");

        return "custom-proxy";
    }

    private static bool DetectActionFormat(JsonObject config)
    {
        var rules = StjNodeHelpers.SelectToken(config, "route.rules") as JsonArray;
        if (rules == null) return true;

        foreach (var rule in rules)
        {
            if (rule is JsonObject rj && rj["action"] != null)
                return true;
        }

        return false;
    }

    private static void InjectRouteRules(JsonObject config, List<string> processes,
        string tcpTag, string? udpTag, bool isActionBased)
    {
        var route = config["route"] as JsonObject;
        if (route == null)
        {
            route = new JsonObject { ["rules"] = new JsonArray(), ["final"] = "direct" };
            config["route"] = route;
        }

        var rules = route["rules"] as JsonArray;
        if (rules == null)
        {
            rules = new JsonArray();
            route["rules"] = rules;
        }

        var replacedUserRules = RemoveInjectedProcessRules(rules);
        if (replacedUserRules > 0)
            Serilog.Log.Logger.Warning(
                "Custom Config Mode: replaced {Count} user-defined process_name route rule(s) — " +
                "VPNRouter manages per-app routing in custom mode.", replacedUserRules);

        var insertIndex = FindRouteInsertIndex(rules, isActionBased);
        bool hasSplit = udpTag != null && udpTag != tcpTag;

        if (hasSplit)
        {
            var udpRule = new JsonObject
            {
                ["process_name"] = BuildProcessNameArray(processes),
                ["network"] = "udp",
                ["outbound"] = udpTag
            };
            var tcpRule = new JsonObject
            {
                ["process_name"] = BuildProcessNameArray(processes),
                ["network"] = "tcp",
                ["outbound"] = tcpTag
            };
            if (isActionBased)
            {
                udpRule["action"] = "route";
                tcpRule["action"] = "route";
            }
            rules.Insert(insertIndex, tcpRule);
            rules.Insert(insertIndex, udpRule);
        }
        else
        {
            var processRule = new JsonObject
            {
                ["process_name"] = BuildProcessNameArray(processes),
                ["outbound"] = tcpTag
            };
            if (isActionBased)
                processRule["action"] = "route";

            rules.Insert(insertIndex, processRule);
        }
    }

    private static JsonArray BuildProcessNameArray(IEnumerable<string> processes)
    {
        var array = new JsonArray();
        foreach (var p in processes)
            array.Add((JsonNode?)JsonValue.Create(p));
        return array;
    }

    private static int FindRouteInsertIndex(JsonArray rules, bool isActionBased)
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

    internal static int RemoveInjectedProcessRules(JsonArray rules)
    {
        int removed = 0;
        for (int i = rules.Count - 1; i >= 0; i--)
        {
            if (rules[i] is JsonObject rj && rj["process_name"] != null)
            {
                rules.RemoveAt(i);
                removed++;
            }
        }
        return removed;
    }

    private static void SanitizeFullTunnelDirectRules(JsonObject config)
    {
        var route = config["route"] as JsonObject;
        if (route?["rules"] is not JsonArray rules)
            return;

        for (int i = rules.Count - 1; i >= 0; i--)
        {
            if (rules[i] is not JsonObject r) continue;
            var outbound = StjNodeHelpers.AsString(r["outbound"]);
            var action = StjNodeHelpers.AsString(r["action"]);

            bool isDirect = string.Equals(outbound, "direct", StringComparison.OrdinalIgnoreCase)
                || (string.Equals(action, "route", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(outbound, "direct", StringComparison.OrdinalIgnoreCase));

            if (isDirect)
            {
                if (StjNodeHelpers.AsBool(r["ip_is_private"]) == true)
                    continue;

                if (r["rule_set"] is JsonArray ruleSets)
                {
                    bool isGeoBypass = ruleSets.Any(rs =>
                    {
                        var tag = StjNodeHelpers.AsString(rs);
                        return tag is GeoSiteRuleSetTag or GeoIpRuleSetTag;
                    });
                    if (isGeoBypass)
                        continue;
                }

                rules.RemoveAt(i);
            }
        }
    }
}
