using System.Text.Json.Nodes;

namespace VPNRouter.Core.Services;

public static partial class CustomConfigInjector
{
    private static void InjectDnsRules(JsonObject config, List<string> processes, bool isActionBased, bool useRemoteDns, string proxyTag)
    {
        var dns = config["dns"] as JsonObject;
        if (dns == null) return;

        var servers = dns["servers"] as JsonArray;
        if (servers == null || servers.Count == 0) return;

        var targetTag = useRemoteDns
            ? FindRemoteDnsTag(servers, config["outbounds"] as JsonArray, config["endpoints"] as JsonArray)
            : FindLocalDnsTag(servers);

        if (useRemoteDns && string.IsNullOrEmpty(targetTag) && !string.IsNullOrEmpty(proxyTag))
            targetTag = EnsureSynthesizedRemoteDns(servers, proxyTag);

        if (string.IsNullOrEmpty(targetTag))
            targetTag = servers.OfType<JsonObject>()
                .Where(s => StjNodeHelpers.AsString(s["type"]) != "fakeip" && StjNodeHelpers.AsString(s["address"]) != "fakeip")
                .Select(s => StjNodeHelpers.AsString(s["tag"]))
                .FirstOrDefault();

        if (string.IsNullOrEmpty(targetTag)) return;

        var rules = dns["rules"] as JsonArray;
        if (rules == null)
        {
            rules = new JsonArray();
            dns["rules"] = rules;
        }

        for (int i = rules.Count - 1; i >= 0; i--)
        {
            if (rules[i] is JsonObject rj && rj["process_name"] != null)
                rules.RemoveAt(i);
        }

        JsonObject dnsRule;
        if (isActionBased)
        {
            dnsRule = new JsonObject
            {
                ["process_name"] = BuildProcessNameArray(processes),
                ["action"] = "route",
                ["server"] = targetTag
            };
        }
        else
        {
            dnsRule = new JsonObject
            {
                ["process_name"] = BuildProcessNameArray(processes),
                ["server"] = targetTag
            };
        }

        rules.Insert(0, dnsRule);
    }

    private static string? FindTypeByTag(JsonArray? items, string? tag)
    {
        if (items == null || string.IsNullOrEmpty(tag)) return null;
        foreach (var item in items)
            if (item is JsonObject obj && StjNodeHelpers.AsString(obj["tag"]) == tag)
                return StjNodeHelpers.AsString(obj["type"]);
        return null;
    }

    internal static bool IsLocalDetour(JsonArray? outbounds, JsonArray? endpoints, string? detour)
    {
        if (string.IsNullOrEmpty(detour)) return true;
        if (detour == "direct" || detour == "dns-direct") return true;

        var outboundType = FindTypeByTag(outbounds, detour);
        if (outboundType != null)
        {
            return outboundType == "direct" || outboundType == "block" || outboundType == "dns";
        }

        var endpointType = FindTypeByTag(endpoints, detour);
        if (endpointType != null)
        {
            return endpointType != "wireguard";
        }

        return true;
    }

    private static string? FindRemoteDnsTag(JsonArray servers, JsonArray? outbounds, JsonArray? endpoints)
    {
        foreach (var server in servers)
        {
            if (server is not JsonObject sObj) continue;
            if (StjNodeHelpers.AsString(sObj["type"]) == "fakeip" || StjNodeHelpers.AsString(sObj["address"]) == "fakeip") continue;
            if (!IsLocalDetour(outbounds, endpoints, StjNodeHelpers.AsString(sObj["detour"])))
                return StjNodeHelpers.AsString(sObj["tag"]);
        }
        return null;
    }

    private static string EnsureSynthesizedRemoteDns(JsonArray servers, string proxyTag)
    {
        const string synthTag = "vpnrouter-vpn-dns";

        foreach (var server in servers)
        {
            if (server is not JsonObject so || StjNodeHelpers.AsString(so["tag"]) != synthTag)
                continue;

            if (StjNodeHelpers.AsString(so["type"]) == "fakeip" || StjNodeHelpers.AsString(so["address"]) == "fakeip")
                throw new InvalidOperationException($"DNS server tag '{synthTag}' collides with reserved synthesis tag. Choose another FakeIP server tag.");

            if (StjNodeHelpers.AsString(so["detour"]) == proxyTag)
                return synthTag;

            var oldDetour = StjNodeHelpers.AsString(so["detour"]) ?? "(none)";
            StampCloudflareDohViaProxy(so, proxyTag);
            Serilog.Log.Logger.Information(
                "Custom Config Mode: re-pointed reserved DNS '{Tag}' to Cloudflare DoH via '{Proxy}' " +
                "(detour was '{Old}', a local resolver) — closes a DNS leak in full/exclude/strict mode.",
                synthTag, proxyTag, oldDetour);
            return synthTag;
        }

        var synth = new JsonObject { ["tag"] = synthTag };
        StampCloudflareDohViaProxy(synth, proxyTag);
        servers.Add(synth);
        Serilog.Log.Logger.Information(
            "Custom Config Mode: synthesized remote DNS '{Tag}' (Cloudflare DoH via '{Proxy}') for " +
            "full/exclude/strict mode — closes the DNS leak when the config has no proxy-detour DNS server.",
            synthTag, proxyTag);
        return synthTag;
    }

    private static void StampCloudflareDohViaProxy(JsonObject server, string proxyTag)
    {
        server.Remove("address");
        server.Remove("server_port");
        server["type"] = "https";
        server["server"] = "8.8.8.8";
        server["path"] = "/dns-query";
        server["detour"] = proxyTag;
    }

    private static string? FindLocalDnsTag(JsonArray servers)
    {
        foreach (var server in servers)
        {
            if (server is not JsonObject sObj) continue;
            var detour = StjNodeHelpers.AsString(sObj["detour"]);
            var type = StjNodeHelpers.AsString(sObj["type"]);
            var address = StjNodeHelpers.AsString(sObj["address"]);
            if (type == "fakeip" || address == "fakeip") continue;
            if (detour == "direct" || detour == "dns-direct" ||
                string.IsNullOrEmpty(detour) && (type == "local" || type == "udp" || type == "dhcp"))
                return StjNodeHelpers.AsString(sObj["tag"]);
        }
        return null;
    }

    private static void EnsureDefaultDomainResolver(JsonObject config)
    {
        var route = config["route"] as JsonObject;
        if (route == null) return;

        var servers = StjNodeHelpers.SelectToken(config, "dns.servers") as JsonArray;
        if (servers == null || servers.Count == 0) return;

        var localTag = FindLocalDnsTag(servers);

        if (string.IsNullOrEmpty(localTag))
            localTag = EnsureLocalBootstrapDns(config, servers);

        if (!string.IsNullOrEmpty(localTag))
            route["default_domain_resolver"] = localTag;
    }

    private static string EnsureLocalBootstrapDns(JsonObject config, JsonArray servers)
    {
        const string tag = "vpnrouter-dns-direct";
        foreach (var s in servers)
            if (s is JsonObject so && StjNodeHelpers.AsString(so["tag"]) == tag)
            {
                if (StjNodeHelpers.AsString(so["type"]) == "fakeip" || StjNodeHelpers.AsString(so["address"]) == "fakeip")
                    throw new InvalidOperationException($"DNS server tag '{tag}' collides with reserved bootstrap DNS tag. Choose another FakeIP server tag.");
                return tag;
            }
        servers.Add((JsonNode?)new JsonObject
        {
            ["tag"] = tag,
            ["type"] = "https",
            ["server"] = "8.8.8.8",
            ["path"] = "/dns-query",
            ["detour"] = "dns-direct",
        });
        EnsureDnsDirectOutbound(config);
        return tag;
    }

    private static void EnsureDnsDirectOutbound(JsonObject config)
    {
        var outbounds = config["outbounds"] as JsonArray;
        if (outbounds == null) return;
        if (outbounds.Any(o => StjNodeHelpers.AsString(o?["tag"]) == "dns-direct")) return;
        outbounds.Add((JsonNode?)new JsonObject
        {
            ["type"] = "direct",
            ["tag"] = "dns-direct",
            ["udp_fragment"] = true,
        });
    }

    // Native route, bypass and direct options belong to the action. Their remaining
    // fields are the original match expression, including invert and nested logical rules.
    private static readonly HashSet<string> ForwardActionFields = new(StringComparer.Ordinal)
    {
        "action", "outbound", "override_address", "override_port", "network_strategy", "fallback_delay",
        "udp_disable_domain_unmapping", "udp_connect", "udp_timeout", "tls_fragment",
        "tls_fragment_fallback_delay", "tls_record_fragment", "tls_spoof", "tls_spoof_method",
        "bind_interface", "inet4_bind_address", "inet6_bind_address", "bind_address_no_port",
        "protect_path", "routing_mark", "reuse_addr", "netns", "connect_timeout", "tcp_fast_open",
        "tcp_multi_path", "disable_tcp_keep_alive", "tcp_keep_alive", "tcp_keep_alive_interval",
        "udp_fragment", "domain_resolver", "fallback_network_type", "domain_strategy"
    };

    private static JsonObject ForwardMatch(JsonObject? forwarding)
    {
        var match = new JsonObject();
        if (forwarding == null) return match;
        bool logical = (string?)forwarding["type"] == "logical";
        foreach (var (key, value) in forwarding)
        {
            // network_type is a match on a default rule and a dial option on logical direct.
            if (ForwardActionFields.Contains(key) || (logical && key == "network_type")) continue;
            if (key == "type" && !logical) continue;
            match.Add(key, value?.DeepClone());
        }
        // A native default rule without match items is unconditional even with invert set.
        if (match.Count == 1) match.Remove("invert");
        return match;
    }

    private static JsonObject GuardForward(JsonObject match, JsonObject condition, string action)
    {
        var rule = match.Count == 0 ? condition.DeepClone().AsObject() : new JsonObject
        {
            ["type"] = "logical", ["mode"] = "and",
            ["rules"] = new JsonArray(match.DeepClone(), condition.DeepClone())
        };
        rule["action"] = action;
        return rule;
    }

    private static void EnsureDnsHijackRule(JsonObject config, bool isActionBased)
    {
        var route = config["route"] as JsonObject;
        if (route == null)
        {
            route = new JsonObject { ["rules"] = new JsonArray() };
            config["route"] = route;
        }

        var rules = route["rules"] as JsonArray;
        if (rules == null)
        {
            rules = new JsonArray();
            route["rules"] = rules;
        }

        bool hasDnsRule = rules.OfType<JsonObject>().Any(r =>
            string.Equals(StjNodeHelpers.AsString(r["action"]), "hijack-dns", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(StjNodeHelpers.AsString(r["protocol"]), "dns", StringComparison.OrdinalIgnoreCase));

        if (!hasDnsRule)
        {
            int insertIndex = 0;
            for (int i = 0; i < rules.Count; i++)
            {
                if (rules[i] is JsonObject r && string.Equals(StjNodeHelpers.AsString(r["action"]), "sniff", StringComparison.OrdinalIgnoreCase))
                {
                    insertIndex = i + 1;
                    break;
                }
            }

            var hijackRule = new JsonObject { ["protocol"] = "dns" };
            if (isActionBased)
                hijackRule["action"] = "hijack-dns";
            else
                hijackRule["outbound"] = "dns-out";

            rules.Insert(insertIndex, hijackRule);
        }
    }
}
