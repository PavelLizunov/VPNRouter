using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static partial class CustomConfigInjector
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
        else if (dnsForFinal != null && dnsServersForFinal != null && dnsServersForFinal.Count > 0)
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
}
