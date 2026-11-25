using System.Text.Json.Nodes;

namespace VPNRouter.Core.Services;

public static partial class CustomConfigInjector
{
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

    private static readonly string[] AwgOnlyEndpointFields =
        { "jc", "jmin", "jmax", "s1", "s2", "s3", "s4",
          "h1", "h2", "h3", "h4", "i1", "i2", "i3", "i4", "i5" };

    internal static List<string> CheckForkFeatureSupport(JsonObject config)
    {
        var errors = new List<string>();
        var (needsAwg, needsXhttp, needsTurnRelay) = DetectForkOnlyFeatures(config);
        if (needsAwg && !SingBoxFeatures.AwgAvailable)
            errors.Add("Config uses AmneziaWG obfuscation fields (jc/jmin/jmax/s1-s4/h1-h4) in 'endpoints', "
                     + "but this sing-box build lacks with_awg — it would fail to start. "
                     + "Use a VPNRouter build bundling the lx core, or remove the AWG fields.");
        if (needsXhttp && !SingBoxFeatures.XhttpAvailable)
            errors.Add("Config uses the 'xhttp' transport, but this sing-box build lacks with_xhttp — "
                     + "it would fail to start. Use a VPNRouter build bundling the lx core, or switch the transport.");
        if (needsTurnRelay && !SingBoxFeatures.TurnRelayAvailable)
            errors.Add("Config uses a 'turnrelay' outbound, but this sing-box build lacks with_turnrelay — "
                     + "it would fail to start. Use a VPNRouter build bundling the turnrelay-capable core.");
        return errors;
    }

    public static string CopyToProgramData(string sourcePath, string configName = "custom")
    {
        var destination = GetProgramDataPath(configName);
        WriteImportCopy(destination, ReadImport(sourcePath));
        return destination;
    }

    internal static void WriteImportCopy(string destination, string normalizedJson)
    {
        Directory.CreateDirectory(AppPaths.ConfigDir);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = AppPaths.CreatePrivateFile(temporary, FileMode.CreateNew))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(normalizedJson);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal static string ReadImport(string sourcePath)
    {
        var raw = File.ReadAllText(sourcePath);
        const string invalid = "Custom config validation failed. Check the selected JSON configuration.";
        JsonObject config;
        try
        {
            config = JsonNode.Parse(raw) as JsonObject ?? throw new InvalidOperationException(invalid);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new InvalidOperationException(invalid);
        }
        if (config["route"] is not JsonObject route || route["rule_set"] is not JsonArray sets) return raw;

        var directory = Path.GetDirectoryName(Path.GetFullPath(sourcePath))!;
        bool changed = false;
        foreach (var set in sets.OfType<JsonObject>())
        {
            if ((string?)set["type"] != "local" || (string?)set["path"] is not { Length: > 0 } path ||
                Path.IsPathFullyQualified(path))
                continue;
            set["path"] = Path.GetFullPath(path, directory);
            changed = true;
        }
        return changed ? config.ToJsonString(InjectorOutputOptions) : raw;
    }

    internal static string GetAcceptedImportPath(string registeredPath, string? configName)
    {
        // A service may recover the same registration with another account or working directory.
        var path = Path.IsPathFullyQualified(registeredPath) ? Path.GetFullPath(registeredPath) :
            "relative:" + Path.GetFullPath(registeredPath, Path.GetFullPath(AppPaths.ConfigDir));
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        var key = (configName == null ? "legacy" : "named:" + configName) + "\0" + path;
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        return Path.Combine(AppPaths.ConfigDir, $"accepted-import-{Convert.ToHexStringLower(digest)}.json");
    }

    internal static string GetRegisteredImportPath(Models.CustomConfigEntry entry) =>
        string.IsNullOrWhiteSpace(entry.Path) ? GetProgramDataPath(entry.Name) : entry.Path;

    internal static void ForgetAcceptedImport(Models.CustomConfigEntry removed, IEnumerable<Models.CustomConfigEntry> remaining)
    {
        var accepted = GetAcceptedImportPath(GetRegisteredImportPath(removed), removed.Name);
        if (!File.Exists(accepted)) return;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var entry in remaining)
        {
            var registered = GetRegisteredImportPath(entry);
            var source = Path.GetFullPath(Environment.ExpandEnvironmentVariables(registered));
            if (source.Equals(accepted, comparison) || GetAcceptedImportPath(registered, entry.Name) == accepted) return;
        }
        File.Delete(accepted);
    }

    public static string GetProgramDataPath(string configName)
    {
        var dir = AppPaths.ConfigDir;
        var safeName = string.Join("_", configName.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(dir, $"custom-{safeName}.json");
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

        var globalV4 = ReadOptionalCidr(fakeIpObj, "inet4_range", isIpv6: false,
            "Malformed 'dns.fakeip.inet4_range': must be a valid IPv4 CIDR string.");
        var globalV6 = ReadOptionalCidr(fakeIpObj, "inet6_range", isIpv6: true,
            "Malformed 'dns.fakeip.inet6_range': must be a valid IPv6 CIDR string.");

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
                var typedV4 = ReadOptionalCidr(typed, "inet4_range", isIpv6: false,
                    "Malformed typed fakeip server 'inet4_range': must be a valid IPv4 CIDR string.");
                var typedV6 = ReadOptionalCidr(typed, "inet6_range", isIpv6: true,
                    "Malformed typed fakeip server 'inet6_range': must be a valid IPv6 CIDR string.");

                MergeGlobalRange(typed, "inet4_range", globalV4, typedV4,
                    "Conflicting fakeip IPv4 range between server and 'dns.fakeip'.");
                MergeGlobalRange(typed, "inet6_range", globalV6, typedV6,
                    "Conflicting fakeip IPv6 range between server and 'dns.fakeip'.");
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

    private static string? ReadOptionalCidr(JsonObject obj, string key, bool isIpv6, string malformedMessage)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node == null)
            return null;
        if (node is not JsonValue val || !val.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text) || !IsValidCidr(text, isIpv6))
            throw new InvalidOperationException(malformedMessage);
        return text;
    }

    private static void MergeGlobalRange(JsonObject typed, string key, string? globalRange, string? typedRange, string conflictMessage)
    {
        if (globalRange == null)
            return;
        if (typedRange == null)
        {
            typed[key] = globalRange;
            return;
        }
        if (!string.Equals(globalRange, typedRange, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(conflictMessage);
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

    private static void StripUnsupportedFeatures(JsonObject config, List<string>? excludeAddresses, bool forceIpv4Only, bool strictDns, bool ipv6Enabled, string? configuredLocalDns = null)
    {
        MigrateFakeIp(config);

        var dnsServers = StjNodeHelpers.SelectToken(config, "dns.servers") as JsonArray;
        MigrateLegacyDnsServers(dnsServers);
        ReplaceLocalDnsServers(dnsServers, configuredLocalDns);
        RouteLocalDnsDetoursThroughDirect(config, dnsServers);
        NormalizeDnsStrategyAndFinal(config, dnsServers, forceIpv4Only, strictDns, ipv6Enabled);
        RemoveLegacyDnsRules(config);
        var removedTags = RemoveBlockAndDnsOutbounds(config);
        RewriteRouteRules(config, removedTags);
        var hadInboundSniff = NormalizeInbounds(config, excludeAddresses);
        AddSniffRuleIfNeeded(config, hadInboundSniff);
        EnsureLogOutput(config);
    }

    private static void MigrateLegacyDnsServers(JsonArray? dnsServers)
    {
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
    }

    private static void ReplaceLocalDnsServers(JsonArray? dnsServers, string? configuredLocalDns = null)
    {
        if (dnsServers == null)
            return;

        var explicitOverride = ConfigGenerator.IsExplicitLocalDnsOverride(configuredLocalDns);
        var needBootstrapTargets = new List<JsonObject>();

        foreach (var server in dnsServers)
        {
            var obj = server as JsonObject;
            if (obj == null) continue;
            var type = StjNodeHelpers.AsString(obj["type"]);
            if (type != "local" && type != "dhcp") continue;

            if (!explicitOverride)
            {
                obj["type"] = "https";
                if (obj["server"] == null)
                    obj["server"] = ConfigGenerator.DefaultDirectDohServer;
                if (obj["path"] == null)
                    obj["path"] = ConfigGenerator.DefaultDirectDohPath;
                obj.Remove("interface");
                obj.Remove("prefer_go");
                obj.Remove("detour");
                continue;
            }

            var resolved = ConfigGenerator.BuildLocalDnsServer(configuredLocalDns, out var bootstrap);
            StampLocalDnsObject(obj, resolved, includeDetour: false);
            if (bootstrap != null)
                needBootstrapTargets.Add(obj);
        }

        if (needBootstrapTargets.Count > 0)
        {
            var bootstrapTag = EnsureLocalBootstrapTag(dnsServers);
            foreach (var target in needBootstrapTargets)
                target["domain_resolver"] = bootstrapTag;
        }
    }

    internal static void StampLocalDnsObject(JsonObject obj, Models.DnsServer resolved, bool includeDetour)
    {
        obj.Remove("address");
        obj.Remove("server");
        obj.Remove("server_port");
        obj.Remove("path");
        obj.Remove("domain_resolver");
        obj.Remove("detour");
        obj.Remove("interface");
        obj.Remove("prefer_go");

        obj["type"] = resolved.Type;
        if (resolved.Server != null)
            obj["server"] = resolved.Server;
        if (resolved.ServerPort.HasValue)
            obj["server_port"] = resolved.ServerPort.Value;
        if (resolved.Path != null)
            obj["path"] = resolved.Path;
        if (resolved.DomainResolver != null)
            obj["domain_resolver"] = resolved.DomainResolver.Server;
        if (includeDetour && resolved.Detour != null)
            obj["detour"] = resolved.Detour;
    }

    internal static bool IsSynthesizedLocalBootstrapTag(string? tag)
    {
        if (tag == ConfigGenerator.LocalBootstrapDnsTag)
            return true;
        var prefix = ConfigGenerator.LocalBootstrapDnsTag + "-";
        if (tag == null || !tag.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var suffix = tag[prefix.Length..];
        return int.TryParse(suffix, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
            && n >= 2
            && string.Equals(suffix, n.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    internal static string EnsureLocalBootstrapTag(JsonArray dnsServers)
    {
        var candidate = ConfigGenerator.LocalBootstrapDnsTag;
        var existingByTag = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var s in dnsServers.OfType<JsonObject>())
        {
            var tag = StjNodeHelpers.AsString(s["tag"]);
            if (!string.IsNullOrEmpty(tag))
                existingByTag.TryAdd(tag, s);
        }

        for (var n = 2; existingByTag.TryGetValue(candidate, out var existing); n++)
        {
            if (StjNodeHelpers.AsString(existing["type"]) == "local"
                && string.IsNullOrEmpty(StjNodeHelpers.AsString(existing["detour"])))
            {
                return candidate;
            }
            candidate = $"{ConfigGenerator.LocalBootstrapDnsTag}-{n}";
        }

        dnsServers.Add((JsonNode?)new JsonObject
        {
            ["tag"] = candidate,
            ["type"] = "local"
        });
        return candidate;
    }

    private static void RouteLocalDnsDetoursThroughDirect(JsonObject config, JsonArray? dnsServers)
    {
        if (dnsServers != null)
        {
            var sbOutbounds = config["outbounds"] as JsonArray;
            var sbEndpoints = config["endpoints"] as JsonArray;
            bool needsDnsDirect = false;
            foreach (var server in dnsServers)
            {
                var obj = server as JsonObject;
                if (obj == null) continue;
                var type = StjNodeHelpers.AsString(obj["type"]);
                if (type == "fakeip" || type == "local") continue;
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
    }

    private static void NormalizeDnsStrategyAndFinal(JsonObject config, JsonArray? dnsServers, bool forceIpv4Only, bool strictDns, bool ipv6Enabled)
    {
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
                    var sType = StjNodeHelpers.AsString(sObj["type"]);
                    if (sType == "fakeip") continue;
                    var sTag = StjNodeHelpers.AsString(sObj["tag"]);
                    if (sType == "local" && IsSynthesizedLocalBootstrapTag(sTag)) continue;
                    var d = StjNodeHelpers.AsString(sObj["detour"]);
                    if ((d == "dns-direct" || d == "direct" || (string.IsNullOrEmpty(d) && sType == "local")) && localTag == null)
                    {
                        localTag = sTag;
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
    }

    private static void RemoveLegacyDnsRules(JsonObject config)
    {
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
    }

    private static Dictionary<string, string> RemoveBlockAndDnsOutbounds(JsonObject config)
    {
        var outbounds = config["outbounds"] as JsonArray;
        var removedTags = new Dictionary<string, string>(StringComparer.Ordinal);
        if (outbounds != null)
        {
            for (int i = outbounds.Count - 1; i >= 0; i--)
            {
                if (outbounds[i] is not JsonObject obItem) continue;
                var type = StjNodeHelpers.AsString(obItem["type"]);
                if (type == "block" || type == "dns")
                {
                    removedTags[StjNodeHelpers.AsString(obItem["tag"]) ?? ""] = type == "block" ? "reject" : "hijack-dns";
                    outbounds.RemoveAt(i);
                }
            }
        }
        return removedTags;
    }

    private static void RewriteRouteRules(JsonObject config, Dictionary<string, string> removedTags)
    {
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
                if (outbound != null && removedTags.TryGetValue(outbound, out var action))
                {
                    rule.Remove("outbound");
                    rule["action"] = action;
                }
            }
        }
    }

    private static bool NormalizeInbounds(JsonObject config, List<string>? excludeAddresses)
    {
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
                        obj["stack"] = ConfigGenerator.PlatformTunStack();

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
        return hadInboundSniff;
    }

    private static void AddSniffRuleIfNeeded(JsonObject config, bool hadInboundSniff)
    {
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
    }

    private static void EnsureLogOutput(JsonObject config)
    {
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
}
