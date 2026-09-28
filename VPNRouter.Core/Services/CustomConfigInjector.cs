using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static class CustomConfigInjector
{
    internal static readonly JsonSerializerOptions InjectorOutputOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = JsonTypeInfoResolver.Combine(
            Json.AppJsonContext.Default,
            new DefaultJsonTypeInfoResolver()),
    };
    public static string Inject(string rawJson, IEnumerable<string> processNames, AppSettings settings)
    {
        var config = JsonNode.Parse(rawJson) as JsonObject
            ?? throw new JsonException("Custom sing-box config root is not an object");

        var forkErrors = CheckForkFeatureSupport(config);
        if (forkErrors.Count > 0)
            throw new NotSupportedException(forkErrors[0]);

        var outboundsForGate = config["outbounds"] as JsonArray;
        if (outboundsForGate != null && outboundsForGate.Count > 0)
        {
            var proxyForGate = ConfigSanityCheck.FindFirstProxyOutbound(outboundsForGate);
            if (proxyForGate != null)
            {
                var offendingField = ConfigSanityCheck.InspectOutbound(proxyForGate);
                if (offendingField != null)
                {
                    var reality = proxyForGate["tls"]?["reality"] as JsonObject;
                    var offendingValue = offendingField switch
                    {
                        "reality.public_key" => StjNodeHelpers.AsString(reality?["public_key"]) ?? "",
                        "reality.short_id" => StjNodeHelpers.AsString(reality?["short_id"]) ?? "",
                        "server" => StjNodeHelpers.AsString(proxyForGate["server"]) ?? "",
                        _ => "",
                    };
                    throw new PlaceholderConfigException(offendingField, offendingValue);
                }
            }
        }

        var routingAppsMode = (settings.App.RoutingAppsMode ?? "include")
            .ToLowerInvariant();
        var isExcludeMode = routingAppsMode == "exclude";
        var isFullTunnel = (settings.App.RoutingMode ?? "split")
            .Equals("full", StringComparison.OrdinalIgnoreCase);

        var processes = ConfigGenerator.ResolveEffectiveAppProcesses(processNames, settings);

        bool isActionBased = DetectActionFormat(config);

        var proxyTag = FindProxyOutboundTag(config);

        RemoveLegacyGeoDns(config);

        var hasGeoBypass = settings.App.BypassRussianTraffic
            && GeoDataDownloader.AreGeoFilesAvailable();
        if (hasGeoBypass)
            _ = EnsureGeoProxyDns(config, proxyTag);

        EnsureDnsHijackRule(config, isActionBased);

        if (!isFullTunnel && processes.Count > 0)
        {
            if (isExcludeMode)
            {
                InjectRouteRules(config, processes, "direct", null, isActionBased);
                InjectDnsRules(config, processes, isActionBased, useRemoteDns: settings.App.StrictDns, proxyTag);
            }
            else
            {
                var (tcpTag, udpTag) = DetectTcpUdpSplit(config, proxyTag);
                InjectRouteRules(config, processes, tcpTag, udpTag, isActionBased);
                InjectDnsRules(config, processes, isActionBased, useRemoteDns: true, proxyTag);
            }
        }

        if (hasGeoBypass)
        {
            InjectGeoBypassRules(config, isActionBased, proxyTag);
        }

        StripUnsupportedFeatures(config, settings.Tun.GetEffectiveRouteExcludeAddress(), settings.App.ForceIpv4Only, settings.App.StrictDns, settings.Tun.Ipv6Enabled);

        var route = config["route"] as JsonObject;
        if (route == null)
        {
            route = new JsonObject { ["rules"] = new JsonArray() };
            config["route"] = route;
        }
        route["final"] = (isFullTunnel || isExcludeMode) ? proxyTag : "direct";
        if (isFullTunnel)
            SanitizeFullTunnelDirectRules(config);

        var wantRemoteDns = isFullTunnel || isExcludeMode || settings.App.StrictDns;
        var dnsForFinal = config["dns"] as JsonObject;
        var dnsServersForFinal = dnsForFinal?["servers"] as JsonArray;
        if (wantRemoteDns)
        {
            if (dnsForFinal == null)
            {
                dnsForFinal = new JsonObject();
                config["dns"] = dnsForFinal;
            }
            if (dnsServersForFinal == null)
            {
                dnsServersForFinal = new JsonArray();
                dnsForFinal["servers"] = dnsServersForFinal;
            }
            if (isFullTunnel || isExcludeMode)
                dnsForFinal["strategy"] = "ipv4_only";
            var remoteTag = FindRemoteDnsTag(dnsServersForFinal, config["outbounds"] as JsonArray, config["endpoints"] as JsonArray)
                            ?? EnsureSynthesizedRemoteDns(dnsServersForFinal, proxyTag);
            if (!string.IsNullOrEmpty(remoteTag))
                dnsForFinal["final"] = remoteTag;
        }
        else if (dnsServersForFinal != null && dnsServersForFinal.Count > 0)
        {
            var localTag = FindLocalDnsTag(dnsServersForFinal);
            if (!string.IsNullOrEmpty(localTag))
                dnsForFinal["final"] = localTag;
        }

        EnsureDefaultDomainResolver(config);
        EnsureClashApi(config, settings.SingBox.ClashApi, settings.SingBox.ClashApiSecret);
        EnsureUrltest(config);

        return config.ToJsonString(InjectorOutputOptions);
    }

    private static void EnsureUrltest(JsonObject config)
    {
        var outbounds = config["outbounds"] as JsonArray;
        if (outbounds == null) return;

        JsonObject? selector = null;
        foreach (var ob in outbounds)
        {
            if (ob is JsonObject obObj && StjNodeHelpers.AsString(obObj["type"]) == "selector")
            {
                selector = obObj;
                break;
            }
        }
        if (selector == null) return;

        var children = selector["outbounds"] as JsonArray;
        if (children == null || children.Count == 0) return;

        foreach (var ob in outbounds)
        {
            if (ob is JsonObject obObj && StjNodeHelpers.AsString(obObj["type"]) == "urltest")
                return;
        }

        var existingTags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ob in outbounds)
        {
            if (ob is JsonObject obObj)
            {
                var tag = StjNodeHelpers.AsString(obObj["tag"]);
                if (!string.IsNullOrEmpty(tag))
                    existingTags.Add(tag);
            }
        }
        var urltestTag = "auto";
        for (var n = 2; existingTags.Contains(urltestTag); n++)
            urltestTag = $"auto-{n}";

        var childTagsArray = new JsonArray();
        foreach (var c in children)
        {
            childTagsArray.Add((JsonNode?)JsonValue.Create(StjNodeHelpers.AsString(c) ?? ""));
        }
        var urltest = new JsonObject
        {
            ["type"] = "urltest",
            ["tag"] = urltestTag,
            ["outbounds"] = childTagsArray,
            ["url"] = "https://www.gstatic.com/generate_204",
            ["interval"] = "5m"
        };

        var selectorIdx = outbounds.IndexOf(selector);
        outbounds.Insert(selectorIdx, urltest);

        children.Insert(0, urltestTag);
    }

    public static (bool IsValid, List<string> Errors) Validate(string rawJson)
    {
        var errors = new List<string>();

        JsonObject? config;
        try
        {
            config = JsonNode.Parse(rawJson) as JsonObject;
            if (config == null)
            {
                errors.Add("Invalid JSON: root must be a JSON object");
                return (false, errors);
            }
        }
        catch (JsonException ex)
        {
            errors.Add($"Invalid JSON: {ex.Message}");
            return (false, errors);
        }

        errors.AddRange(CheckForkFeatureSupport(config));

        var hasWireGuardEndpoint = config["endpoints"] is JsonArray eps
            && eps.OfType<JsonObject>().Any(e => StjNodeHelpers.AsString(e["type"]) == "wireguard");

        var outbounds = config["outbounds"] as JsonArray;
        if (outbounds == null || outbounds.Count == 0)
        {
            if (!hasWireGuardEndpoint)
                errors.Add("No 'outbounds' array in config");
            return (errors.Count == 0, errors);
        }

        var hasProxy = outbounds.Any(o =>
        {
            var type = StjNodeHelpers.AsString(o?["type"]);
            return type != "direct" && type != "block" && type != "dns";
        });
        if (!hasProxy && !hasWireGuardEndpoint)
            errors.Add("No proxy outbound found (all outbounds are direct/block/dns)");

        var tagCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var o in outbounds)
        {
            var tag = StjNodeHelpers.AsString(o?["tag"]);
            if (string.IsNullOrEmpty(tag)) continue;
            tagCounts[tag] = tagCounts.TryGetValue(tag, out var count) ? count + 1 : 1;
        }
        foreach (var (tag, count) in tagCounts)
        {
            if (count > 1)
                errors.Add($"Duplicate outbound tag '{tag}' ({count} outbounds) — sing-box rejects duplicate outbound tags");
        }

        return (errors.Count == 0, errors);
    }

    private static readonly string[] AwgOnlyEndpointFields =
        { "jc", "jmin", "jmax", "s1", "s2", "s3", "s4",
          "h1", "h2", "h3", "h4", "i1", "i2", "i3", "i4", "i5" };

    internal static (bool NeedsAwg, bool NeedsXhttp) DetectForkOnlyFeatures(JsonObject config)
    {
        var needsAwg = false;
        var needsXhttp = false;

        if (config["endpoints"] is JsonArray endpoints)
            foreach (var ep in endpoints.OfType<JsonObject>())
                if (AwgOnlyEndpointFields.Any(ep.ContainsKey))
                    needsAwg = true;

        if (config["outbounds"] is JsonArray outbounds)
            foreach (var ob in outbounds.OfType<JsonObject>())
                if (StjNodeHelpers.AsString(ob["transport"]?["type"]) == "xhttp")
                    needsXhttp = true;

        return (needsAwg, needsXhttp);
    }

    internal static List<string> CheckForkFeatureSupport(JsonObject config)
    {
        var errors = new List<string>();
        var (needsAwg, needsXhttp) = DetectForkOnlyFeatures(config);
        if (needsAwg && !SingBoxFeatures.AwgAvailable)
            errors.Add("Config uses AmneziaWG obfuscation fields (jc/jmin/jmax/s1-s4/h1-h4) in 'endpoints', "
                     + "but this sing-box build lacks with_awg — it would fail to start. "
                     + "Use a VPNRouter build bundling the lx core, or remove the AWG fields.");
        if (needsXhttp && !SingBoxFeatures.XhttpAvailable)
            errors.Add("Config uses the 'xhttp' transport, but this sing-box build lacks with_xhttp — "
                     + "it would fail to start. Use a VPNRouter build bundling the lx core, or switch the transport.");
        return errors;
    }

    public static string CopyToProgramData(string sourcePath, string configName = "custom")
    {
        var dir = AppPaths.ConfigDir;
        Directory.CreateDirectory(dir);

        var safeName = string.Join("_", configName.Split(Path.GetInvalidFileNameChars()));
        var destPath = Path.Combine(dir, $"custom-{safeName}.json");
        File.Copy(sourcePath, destPath, overwrite: true);
        return destPath;
    }

    public static string GetProgramDataPath(string configName)
    {
        var dir = AppPaths.ConfigDir;
        var safeName = string.Join("_", configName.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(dir, $"custom-{safeName}.json");
    }

    public static (string protocols, string server) ParseConfigInfo(string rawJson)
    {
        try
        {
            var config = JsonNode.Parse(rawJson) as JsonObject;
            if (config == null) return ("?", "?");
            var outbounds = config["outbounds"] as JsonArray;
            if (outbounds == null) return ("?", "?");

            var protocols = new HashSet<string>();
            string? server = null;

            foreach (var ob in outbounds)
            {
                if (ob is not JsonObject obObj) continue;
                var type = StjNodeHelpers.AsString(obObj["type"]);
                if (type == "direct" || type == "block" || type == "dns" || type == "selector" || type == "urltest")
                    continue;

                if (type != null)
                    protocols.Add(type.ToUpperInvariant());

                if (server == null)
                    server = StjNodeHelpers.AsString(obObj["server"]);
            }

            return (
                protocols.Count > 0 ? string.Join("+", protocols) : "?",
                server ?? "?"
            );
        }
        catch
        {
            return ("?", "?");
        }
    }

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

    private static (string tcpTag, string udpTag) DetectTcpUdpSplit(JsonObject config, string proxyTag)
    {
        var outbounds = config["outbounds"] as JsonArray;
        if (outbounds == null) return (proxyTag, proxyTag);

        var proxyOutbound = outbounds.FirstOrDefault(o => StjNodeHelpers.AsString(o?["tag"]) == proxyTag);
        if (proxyOutbound == null) return (proxyTag, proxyTag);

        var proxyType = StjNodeHelpers.AsString(proxyOutbound["type"]);
        if (proxyType != "selector" && proxyType != "urltest") return (proxyTag, proxyTag);

        var childTags = proxyOutbound["outbounds"] as JsonArray;
        if (childTags == null || childTags.Count < 2) return (proxyTag, proxyTag);

        string? vlessTag = null;
        string? quicTag = null;

        foreach (var childTagToken in childTags)
        {
            var childTag = StjNodeHelpers.AsString(childTagToken);
            if (childTag == null) continue;
            var child = outbounds.FirstOrDefault(o => StjNodeHelpers.AsString(o?["tag"]) == childTag);
            if (child == null) continue;

            var childType = StjNodeHelpers.AsString(child["type"]);
            if (childType == "vless" && vlessTag == null)
                vlessTag = childTag;
            else if ((childType == "tuic" || childType == "hysteria2" || childType == "hysteria")
                     && quicTag == null)
                quicTag = childTag;
        }

        if (vlessTag != null && quicTag != null)
            return (vlessTag, quicTag);

        return (proxyTag, proxyTag);
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

    private static void EnsureClashApi(JsonObject config, string clashApiAddr, string? clashApiSecret)
    {
        var experimental = config["experimental"] as JsonObject;
        if (experimental == null)
        {
            experimental = new JsonObject();
            config["experimental"] = experimental;
        }

        var clashApi = experimental["clash_api"] as JsonObject;
        if (clashApi == null)
        {
            clashApi = new JsonObject();
            experimental["clash_api"] = clashApi;
        }

        if (clashApi["external_controller"] == null)
        {
            clashApi["external_controller"] = clashApiAddr;
            if (!string.IsNullOrEmpty(clashApiSecret) && clashApi["secret"] == null)
                clashApi["secret"] = clashApiSecret;
        }
    }

    private static void MigrateFakeIp(JsonObject config)
    {
        if (config["dns"] is not JsonObject dns)
            return;

        var servers = dns["servers"] as JsonArray;
        JsonObject? legacyServer = null;
        var typedServers = new List<JsonObject>();
        int legacyCount = 0;

        if (servers != null)
        {
            foreach (var s in servers)
            {
                if (s is not JsonObject sObj) continue;
                var addr = StjNodeHelpers.AsString(sObj["address"]);
                var type = StjNodeHelpers.AsString(sObj["type"]);
                if (addr == "fakeip" && (string.IsNullOrEmpty(type) || type == "legacy"))
                {
                    legacyServer = sObj;
                    legacyCount++;
                }
                else if (type == "fakeip")
                {
                    typedServers.Add(sObj);
                }
            }
        }

        if (legacyCount > 1)
            throw new InvalidOperationException("Multiple legacy fakeip DNS servers found in 'dns.servers'.");

        if (!dns.TryGetPropertyValue("fakeip", out var fakeIpNode))
        {
            if (legacyCount > 0)
                throw new InvalidOperationException("Legacy DNS server with address 'fakeip' requires global 'dns.fakeip' configuration, but 'dns.fakeip' is absent.");
            return;
        }

        if (fakeIpNode == null)
        {
            if (legacyCount > 0)
                throw new InvalidOperationException("Legacy DNS server with address 'fakeip' requires global 'dns.fakeip' configuration, but 'dns.fakeip' is null.");
            dns.Remove("fakeip");
            return;
        }

        if (fakeIpNode is not JsonObject fakeIpObj)
            throw new InvalidOperationException("Malformed 'dns.fakeip' configuration: expected a JSON object.");

        bool enabled = false;
        if (fakeIpObj.TryGetPropertyValue("enabled", out var enabledNode) && enabledNode != null)
        {
            if (enabledNode is not JsonValue ev || !ev.TryGetValue<bool>(out enabled))
                throw new InvalidOperationException("Malformed 'dns.fakeip.enabled': must be a boolean.");
        }
        else if (fakeIpObj.ContainsKey("enabled"))
        {
            throw new InvalidOperationException("Malformed 'dns.fakeip.enabled': must be a boolean.");
        }

        string? globalV4 = null;
        if (fakeIpObj.TryGetPropertyValue("inet4_range", out var v4Node) && v4Node != null)
        {
            if (v4Node is not JsonValue v4Val || !v4Val.TryGetValue<string>(out var v4Str) || string.IsNullOrWhiteSpace(v4Str) || !IsValidCidr(v4Str, isIpv6: false))
                throw new InvalidOperationException("Malformed 'dns.fakeip.inet4_range': must be a valid IPv4 CIDR string.");
            globalV4 = v4Str;
        }

        string? globalV6 = null;
        if (fakeIpObj.TryGetPropertyValue("inet6_range", out var v6Node) && v6Node != null)
        {
            if (v6Node is not JsonValue v6Val || !v6Val.TryGetValue<string>(out var v6Str) || string.IsNullOrWhiteSpace(v6Str) || !IsValidCidr(v6Str, isIpv6: true))
                throw new InvalidOperationException("Malformed 'dns.fakeip.inet6_range': must be a valid IPv6 CIDR string.");
            globalV6 = v6Str;
        }

        if (!enabled)
        {
            if (legacyCount > 0)
                throw new InvalidOperationException("Cannot migrate legacy fakeip DNS server when 'dns.fakeip.enabled' is false.");

            dns.Remove("fakeip");
            return;
        }

        if (legacyCount == 0 && typedServers.Count == 0)
            throw new InvalidOperationException("'dns.fakeip' is enabled, but no fakeip DNS server was found in 'dns.servers'.");

        if (legacyCount > 0 && typedServers.Count > 0)
            throw new InvalidOperationException("Cannot migrate configuration with mixed legacy ('address': 'fakeip') and typed ('type': 'fakeip') DNS servers.");

        if (typedServers.Count > 0)
        {
            foreach (var typed in typedServers)
            {
                string? typedV4 = null;
                if (typed.TryGetPropertyValue("inet4_range", out var t4Node) && t4Node != null)
                {
                    if (t4Node is not JsonValue v4Val || !v4Val.TryGetValue<string>(out var v4Str) || string.IsNullOrWhiteSpace(v4Str) || !IsValidCidr(v4Str, isIpv6: false))
                        throw new InvalidOperationException("Malformed typed fakeip server 'inet4_range': must be a valid IPv4 CIDR string.");
                    typedV4 = v4Str;
                }

                string? typedV6 = null;
                if (typed.TryGetPropertyValue("inet6_range", out var t6Node) && t6Node != null)
                {
                    if (t6Node is not JsonValue v6Val || !v6Val.TryGetValue<string>(out var v6Str) || string.IsNullOrWhiteSpace(v6Str) || !IsValidCidr(v6Str, isIpv6: true))
                        throw new InvalidOperationException("Malformed typed fakeip server 'inet6_range': must be a valid IPv6 CIDR string.");
                    typedV6 = v6Str;
                }

                if (globalV4 != null)
                {
                    if (typedV4 != null)
                    {
                        if (!string.Equals(globalV4, typedV4, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Conflicting fakeip IPv4 range between server and 'dns.fakeip'.");
                    }
                    else
                    {
                        typed["inet4_range"] = globalV4;
                    }
                }

                if (globalV6 != null)
                {
                    if (typedV6 != null)
                    {
                        if (!string.Equals(globalV6, typedV6, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Conflicting fakeip IPv6 range between server and 'dns.fakeip'.");
                    }
                    else
                    {
                        typed["inet6_range"] = globalV6;
                    }
                }
            }

            dns.Remove("fakeip");
            return;
        }

        if (globalV4 == null && globalV6 == null)
            throw new InvalidOperationException("Legacy fakeip configuration requires at least one of 'inet4_range' or 'inet6_range'.");

        var unsupported = new[] { "strategy", "address_resolver", "address_strategy", "client_subnet" }
            .Where(f => legacyServer![f] != null).ToList();
        if (unsupported.Count > 0)
            throw new InvalidOperationException($"Legacy FakeIP DNS server uses options requiring manual migration: {string.Join(", ", unsupported)}.");

        legacyServer!.Remove("strategy");
        legacyServer.Remove("address_resolver");
        legacyServer.Remove("address_strategy");
        legacyServer.Remove("client_subnet");

        legacyServer!.Remove("address");
        legacyServer["type"] = "fakeip";
        if (globalV4 != null)
            legacyServer["inet4_range"] = globalV4;
        if (globalV6 != null)
            legacyServer["inet6_range"] = globalV6;
        legacyServer.Remove("detour");

        dns.Remove("fakeip");
    }

    private static bool IsValidCidr(string cidr, bool isIpv6)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2) return false;
        if (!System.Net.IPAddress.TryParse(parts[0], out var ip)) return false;
        if (isIpv6 && ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return false;
        if (!isIpv6 && ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        if (!int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var prefix)) return false;
        return isIpv6 ? prefix >= 0 && prefix <= 128 : prefix >= 0 && prefix <= 32;
    }

    private static void StripUnsupportedFeatures(JsonObject config, List<string>? excludeAddresses, bool forceIpv4Only, bool strictDns, bool ipv6Enabled)
    {
        MigrateFakeIp(config);

        var dnsServers = StjNodeHelpers.SelectToken(config, "dns.servers") as JsonArray;
        if (dnsServers != null)
        {
            foreach (var server in dnsServers)
            {
                var obj = server as JsonObject;
                if (obj == null) continue;

                var address = StjNodeHelpers.AsString(obj["address"]);
                if (address == null || obj["type"] != null) continue;

                obj.Remove("address");

                var addrResolver = StjNodeHelpers.AsString(obj["address_resolver"]);
                if (addrResolver != null)
                {
                    obj.Remove("address_resolver");
                    obj["domain_resolver"] = addrResolver;
                }

                if (address == "local" || address == "dhcp://auto")
                {
                    obj["type"] = address == "local" ? "local" : "dhcp";
                }
                else if (address.Contains("://"))
                {
                    if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
                    {
                        obj["type"] = "udp";
                        obj["server"] = address;
                        continue;
                    }
                    var scheme = uri.Scheme;

                    if (scheme == "tls")
                    {
                        scheme = "https";
                        obj["path"] = "/dns-query";
                    }

                    obj["type"] = scheme;
                    obj["server"] = uri.Host;
                    if (uri.Port > 0 && uri.Port != 443 && uri.Port != 53)
                        obj["server_port"] = uri.Port;
                    if (scheme == "https" && obj["path"] == null)
                        obj["path"] = !string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/"
                            ? uri.AbsolutePath
                            : "/dns-query";
                }
                else
                {
                    obj["type"] = "udp";
                    obj["server"] = address;
                }
            }
        }

        if (dnsServers != null)
        {
            foreach (var server in dnsServers)
            {
                var obj = server as JsonObject;
                if (obj == null) continue;
                var type = StjNodeHelpers.AsString(obj["type"]);
                if (type == "local" || type == "dhcp")
                {
                    obj["type"] = "https";
                    if (obj["server"] == null)
                        obj["server"] = "8.8.8.8";
                    if (obj["path"] == null)
                        obj["path"] = "/dns-query";
                    obj.Remove("detour");
                }
            }
        }

        if (dnsServers != null)
        {
            var sbOutbounds = config["outbounds"] as JsonArray;
            var sbEndpoints = config["endpoints"] as JsonArray;
            bool needsDnsDirect = false;
            foreach (var server in dnsServers)
            {
                var obj = server as JsonObject;
                if (obj == null) continue;
                if (StjNodeHelpers.AsString(obj["type"]) == "fakeip") continue;
                var detour = StjNodeHelpers.AsString(obj["detour"]);
                if (detour != "dns-direct" && IsLocalDetour(sbOutbounds, sbEndpoints, detour))
                {
                    obj["detour"] = "dns-direct";
                    needsDnsDirect = true;
                }
            }

            if (needsDnsDirect)
            {
                var dnsOutbounds = config["outbounds"] as JsonArray;
                if (dnsOutbounds != null && !dnsOutbounds.Any(o => StjNodeHelpers.AsString(o?["tag"]) == "dns-direct"))
                {
                    dnsOutbounds.Add((JsonNode?)new JsonObject
                    {
                        ["type"] = "direct",
                        ["tag"] = "dns-direct",
                        ["udp_fragment"] = true
                    });
                }
            }
        }

        var dns = config["dns"] as JsonObject;
        if (dns != null)
        {
            if (forceIpv4Only)
            {
                var strategy = StjNodeHelpers.AsString(dns["strategy"]);
                if (strategy != "ipv4_only")
                    dns["strategy"] = "ipv4_only";
            }
            else if (!ipv6Enabled)
            {
                if (StjNodeHelpers.AsString(dns["strategy"]) is null)
                    dns["strategy"] = "ipv4_only";
            }

            string? localTag = null;
            string? proxyTag = dnsServers != null ? FindRemoteDnsTag(dnsServers, config["outbounds"] as JsonArray, config["endpoints"] as JsonArray) : null;
            if (dnsServers != null)
            {
                foreach (var s in dnsServers)
                {
                    if (s is not JsonObject sObj) continue;
                    if (StjNodeHelpers.AsString(sObj["type"]) == "fakeip") continue;
                    var d = StjNodeHelpers.AsString(sObj["detour"]);
                    if ((d == "dns-direct" || d == "direct") && localTag == null)
                    {
                        localTag = StjNodeHelpers.AsString(sObj["tag"]);
                    }
                }
            }

            if (strictDns && proxyTag != null)
            {
                var finalTag = StjNodeHelpers.AsString(dns["final"]);
                if (finalTag != proxyTag)
                    dns["final"] = proxyTag;
            }
            else if (localTag != null)
            {
                var finalTag = StjNodeHelpers.AsString(dns["final"]);
                if (finalTag != localTag)
                    dns["final"] = localTag;
            }
        }

        var dnsRules = StjNodeHelpers.SelectToken(config, "dns.rules") as JsonArray;
        if (dnsRules != null)
        {
            for (int i = dnsRules.Count - 1; i >= 0; i--)
            {
                var rule = dnsRules[i] as JsonObject;
                if (rule == null) continue;

                if (rule["geosite"] != null || rule["geoip"] != null ||
                    rule["outbound"] != null)
                    dnsRules.RemoveAt(i);
            }
        }

        var outbounds = config["outbounds"] as JsonArray;
        var removedTags = new HashSet<string>();
        if (outbounds != null)
        {
            for (int i = outbounds.Count - 1; i >= 0; i--)
            {
                if (outbounds[i] is not JsonObject obItem) continue;
                var type = StjNodeHelpers.AsString(obItem["type"]);
                if (type == "block" || type == "dns")
                {
                    removedTags.Add(StjNodeHelpers.AsString(obItem["tag"]) ?? "");
                    outbounds.RemoveAt(i);
                }
            }
        }

        var routeRules = StjNodeHelpers.SelectToken(config, "route.rules") as JsonArray;
        if (routeRules != null)
        {
            for (int i = routeRules.Count - 1; i >= 0; i--)
            {
                var rule = routeRules[i] as JsonObject;
                if (rule == null) continue;

                if (rule["geosite"] != null || rule["geoip"] != null)
                {
                    routeRules.RemoveAt(i);
                    continue;
                }

                var outbound = StjNodeHelpers.AsString(rule["outbound"]);
                if (outbound != null && removedTags.Contains(outbound))
                {
                    rule.Remove("outbound");
                    rule["action"] = StjNodeHelpers.AsString(rule["protocol"]) == "dns"
                        ? "hijack-dns"
                        : "reject";
                }
            }
        }

        bool hadInboundSniff = false;
        var inbounds = config["inbounds"] as JsonArray;
        if (inbounds != null)
        {
            foreach (var inbound in inbounds)
            {
                var obj = inbound as JsonObject;
                if (obj == null) continue;

                if (obj["sniff"] != null)
                    hadInboundSniff = true;
                obj.Remove("sniff");
                obj.Remove("sniff_override_destination");
                obj.Remove("sniff_timeout");
                obj.Remove("domain_strategy");

                if (StjNodeHelpers.AsString(obj["type"]) == "tun")
                {
                    if (obj["address"] == null)
                    {
                        var addrs = new JsonArray();
                        var inet4 = StjNodeHelpers.AsString(obj["inet4_address"]);
                        if (inet4 != null)
                        {
                            addrs.Add((JsonNode?)JsonValue.Create(inet4));
                            obj.Remove("inet4_address");
                        }
                        var inet6 = StjNodeHelpers.AsString(obj["inet6_address"]);
                        if (inet6 != null)
                        {
                            addrs.Add((JsonNode?)JsonValue.Create(inet6));
                            obj.Remove("inet6_address");
                        }
                        if (addrs.Count > 0)
                            obj["address"] = addrs;
                    }

                    obj["strict_route"] = false;
                    if (obj["stack"] == null)
                        obj["stack"] = "system";

                    if (excludeAddresses != null && excludeAddresses.Count > 0)
                    {
                        var existing = obj["route_exclude_address"] as JsonArray;
                        var merged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        if (existing != null)
                        {
                            foreach (var t in existing)
                            {
                                var s = StjNodeHelpers.AsString(t);
                                if (s != null) merged.Add(s);
                            }
                        }
                        foreach (var addr in excludeAddresses)
                            merged.Add(addr);
                        var mergedArray = new JsonArray();
                        foreach (var s in merged) mergedArray.Add((JsonNode?)JsonValue.Create(s));
                        obj["route_exclude_address"] = mergedArray;
                    }
                }
            }
        }

        if (hadInboundSniff)
        {
            var sniffRules = StjNodeHelpers.SelectToken(config, "route.rules") as JsonArray;
            if (sniffRules != null)
            {
                bool hasSniffRule = sniffRules.Any(r => StjNodeHelpers.AsString(r?["action"]) == "sniff");
                if (!hasSniffRule)
                {
                    sniffRules.Insert(0, new JsonObject
                    {
                        ["action"] = "sniff",
                        ["timeout"] = "300ms"
                    });
                }
            }
        }

        var log = config["log"] as JsonObject;
        if (log == null)
        {
            log = new JsonObject();
            config["log"] = log;
        }
        var logPath = AppPaths.SingBoxLogPath;
        log["output"] = logPath;
        log["timestamp"] = true;
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
