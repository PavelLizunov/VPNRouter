using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Takes a user-provided sing-box JSON config and injects process-based routing rules.
/// Preserves all existing config (outbounds, DNS servers, TLS, etc.) —
/// only adds process_name route/DNS rules and Clash API for hot-reload.
///
/// Supports both legacy (outbound-based) and 1.12+ (action-based) config formats.
/// Auto-detects format from existing route rules.
/// </summary>
public static class CustomConfigInjector
{
    /// <summary>
    /// Inject process routing into a raw sing-box JSON config.
    /// Returns the modified JSON string ready for sing-box.
    /// </summary>
    public static string Inject(string rawJson, IEnumerable<string> processNames, AppSettings settings)
    {
        var config = JObject.Parse(rawJson);

        // Filter wildcards — sing-box process_name doesn't support globs.
        // Preserve original case — sing-box matching is case-sensitive.
        var processes = processNames
            .Where(p => !p.Contains('*') && !p.Contains('?'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (processes.Count > 0)
        {
            var proxyTag = FindProxyOutboundTag(config);
            var isActionBased = DetectActionFormat(config);

            // Auto-detect TCP/UDP split: if selector has both VLESS and QUIC-based
            // (TUIC/Hysteria2) outbounds, route TCP→VLESS, UDP→QUIC for optimal performance
            var (tcpTag, udpTag) = DetectTcpUdpSplit(config, proxyTag);

            InjectRouteRules(config, processes, tcpTag, udpTag, isActionBased);
            InjectDnsRules(config, processes, isActionBased);
        }

        // Migrate legacy features to sing-box 1.13+ format
        StripUnsupportedFeatures(config, settings.Tun.RouteExcludeAddress);

        // Align route.final with routing_mode setting
        var isSplitTunnel = !(settings.App.RoutingMode ?? "split")
            .Equals("full", StringComparison.OrdinalIgnoreCase);
        if (isSplitTunnel)
        {
            // Split tunnel: only matched processes go through VPN, everything else direct.
            // User config may have "final":"proxy" (full tunnel) — override to "direct".
            var route = config["route"] as JObject;
            if (route != null)
                route["final"] = "direct";
        }

        EnsureDefaultDomainResolver(config);
        EnsureClashApi(config, settings.SingBox.ClashApi);

        return config.ToString(Formatting.Indented);
    }

    /// <summary>
    /// Validates a custom config has the minimum required structure.
    /// Returns (isValid, errors).
    /// </summary>
    public static (bool IsValid, List<string> Errors) Validate(string rawJson)
    {
        var errors = new List<string>();

        JObject config;
        try
        {
            config = JObject.Parse(rawJson);
        }
        catch (JsonException ex)
        {
            errors.Add($"Invalid JSON: {ex.Message}");
            return (false, errors);
        }

        // Must have outbounds
        var outbounds = config["outbounds"] as JArray;
        if (outbounds == null || outbounds.Count == 0)
        {
            errors.Add("No 'outbounds' array in config");
            return (false, errors);
        }

        // Must have at least one proxy-like outbound (not just direct/block/dns)
        var hasProxy = outbounds.Any(o =>
        {
            var type = o["type"]?.ToString();
            return type != "direct" && type != "block" && type != "dns";
        });
        if (!hasProxy)
            errors.Add("No proxy outbound found (all outbounds are direct/block/dns)");

        // Route section is optional — InjectRouteRules creates one if missing

        return (errors.Count == 0, errors);
    }

    /// <summary>
    /// Copies a custom config to ProgramData with a named filename.
    /// Returns the destination path. Subsequent reads use the copy.
    /// </summary>
    public static string CopyToProgramData(string sourcePath, string configName = "custom")
    {
        var dir = Environment.ExpandEnvironmentVariables(@"%ProgramData%\VPNRouter\config");
        Directory.CreateDirectory(dir);

        // Sanitize name for filesystem
        var safeName = string.Join("_", configName.Split(Path.GetInvalidFileNameChars()));
        var destPath = Path.Combine(dir, $"custom-{safeName}.json");
        File.Copy(sourcePath, destPath, overwrite: true);
        return destPath;
    }

    /// <summary>Returns the ProgramData path for a named custom config.</summary>
    public static string GetProgramDataPath(string configName)
    {
        var dir = Environment.ExpandEnvironmentVariables(@"%ProgramData%\VPNRouter\config");
        var safeName = string.Join("_", configName.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(dir, $"custom-{safeName}.json");
    }

    /// <summary>
    /// Parses a sing-box JSON config and returns display info for the ListView.
    /// Returns (protocols, serverAddress).
    /// </summary>
    public static (string protocols, string server) ParseConfigInfo(string rawJson)
    {
        try
        {
            var config = JObject.Parse(rawJson);
            var outbounds = config["outbounds"] as JArray;
            if (outbounds == null) return ("?", "?");

            var protocols = new HashSet<string>();
            string? server = null;

            foreach (var ob in outbounds)
            {
                var type = ob["type"]?.ToString();
                if (type == "direct" || type == "block" || type == "dns" || type == "selector" || type == "urltest")
                    continue;

                if (type != null)
                    protocols.Add(type.ToUpperInvariant());

                if (server == null)
                    server = ob["server"]?.ToString();
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

    // ─── Private: Find proxy outbound ────────────────────────────────────────

    /// <summary>
    /// Finds the primary proxy outbound tag. Priority:
    /// 1. "selector" type (manual switching between protocols)
    /// 2. "urltest" type (auto-failover)
    /// 3. First non-direct/block/dns outbound (vless, hysteria2, tuic, etc.)
    /// </summary>
    private static string FindProxyOutboundTag(JObject config)
    {
        var outbounds = config["outbounds"] as JArray;
        if (outbounds == null) return "proxy";

        // 1. Selector (user-switchable)
        foreach (var ob in outbounds)
        {
            if (ob["type"]?.ToString() == "selector")
                return ob["tag"]?.ToString() ?? "proxy";
        }

        // 2. URLTest (auto-failover)
        foreach (var ob in outbounds)
        {
            if (ob["type"]?.ToString() == "urltest")
                return ob["tag"]?.ToString() ?? "proxy";
        }

        // 3. First proxy-like outbound
        foreach (var ob in outbounds)
        {
            var type = ob["type"]?.ToString();
            if (type != "direct" && type != "block" && type != "dns")
                return ob["tag"]?.ToString() ?? "proxy";
        }

        return "proxy";
    }

    // ─── Private: Detect config format ───────────────────────────────────────

    /// <summary>
    /// Detects whether the config uses 1.12+ action-based format or legacy outbound-based.
    /// If any route rule has an "action" field → action-based.
    /// </summary>
    private static bool DetectActionFormat(JObject config)
    {
        var rules = config.SelectToken("route.rules") as JArray;
        if (rules == null) return true; // no rules yet → default to modern format

        foreach (var rule in rules)
        {
            if (rule["action"] != null)
                return true;
        }

        return false; // legacy format
    }

    // ─── Private: TCP/UDP split detection ────────────────────────────────────

    /// <summary>
    /// Detects if TCP/UDP split is possible. If a selector/urltest outbound contains
    /// both VLESS and QUIC-based (TUIC/Hysteria2) children, returns separate tags.
    /// VLESS (with flow/xtls) is optimal for TCP, QUIC protocols for UDP.
    /// Returns (tcpTag, udpTag) — both equal proxyTag if no split detected.
    /// </summary>
    private static (string tcpTag, string udpTag) DetectTcpUdpSplit(JObject config, string proxyTag)
    {
        var outbounds = config["outbounds"] as JArray;
        if (outbounds == null) return (proxyTag, proxyTag);

        // Find the proxy outbound (selector/urltest)
        var proxyOutbound = outbounds.FirstOrDefault(o => o["tag"]?.ToString() == proxyTag);
        if (proxyOutbound == null) return (proxyTag, proxyTag);

        var proxyType = proxyOutbound["type"]?.ToString();
        if (proxyType != "selector" && proxyType != "urltest") return (proxyTag, proxyTag);

        var childTags = proxyOutbound["outbounds"] as JArray;
        if (childTags == null || childTags.Count < 2) return (proxyTag, proxyTag);

        // Categorize children by protocol
        string? vlessTag = null;
        string? quicTag = null; // tuic, hysteria, hysteria2

        foreach (var childTagToken in childTags)
        {
            var childTag = childTagToken.ToString();
            var child = outbounds.FirstOrDefault(o => o["tag"]?.ToString() == childTag);
            if (child == null) continue;

            var childType = child["type"]?.ToString();
            if (childType == "vless" && vlessTag == null)
                vlessTag = childTag;
            else if ((childType == "tuic" || childType == "hysteria2" || childType == "hysteria")
                     && quicTag == null)
                quicTag = childTag;
        }

        // Both found → split TCP/UDP
        if (vlessTag != null && quicTag != null)
            return (vlessTag, quicTag);

        return (proxyTag, proxyTag);
    }

    // ─── Private: Inject route rules ─────────────────────────────────────────

    private static void InjectRouteRules(JObject config, List<string> processes,
        string tcpTag, string? udpTag, bool isActionBased)
    {
        var route = config["route"] as JObject;
        if (route == null)
        {
            route = new JObject { ["rules"] = new JArray(), ["final"] = "direct" };
            config["route"] = route;
        }

        var rules = route["rules"] as JArray;
        if (rules == null)
        {
            rules = new JArray();
            route["rules"] = rules;
        }

        // Remove any previously injected process_name rules (idempotent re-injection)
        RemoveInjectedProcessRules(rules);

        var processArray = new JArray(processes.Cast<object>().ToArray());
        var insertIndex = FindRouteInsertIndex(rules, isActionBased);
        bool hasSplit = udpTag != null && udpTag != tcpTag;

        if (hasSplit)
        {
            // TCP/UDP split: UDP → QUIC protocol (tuic/hysteria2), TCP → VLESS
            var udpRule = new JObject
            {
                ["process_name"] = processArray.DeepClone(),
                ["network"] = "udp",
                ["outbound"] = udpTag
            };
            var tcpRule = new JObject
            {
                ["process_name"] = processArray.DeepClone(),
                ["network"] = "tcp",
                ["outbound"] = tcpTag
            };
            if (isActionBased)
            {
                udpRule["action"] = "route";
                tcpRule["action"] = "route";
            }
            // UDP first (higher priority for voice/video), then TCP
            rules.Insert(insertIndex, tcpRule);
            rules.Insert(insertIndex, udpRule);
        }
        else
        {
            // Single outbound — all traffic through proxy
            var processRule = new JObject
            {
                ["process_name"] = processArray,
                ["outbound"] = tcpTag
            };
            if (isActionBased)
                processRule["action"] = "route";

            rules.Insert(insertIndex, processRule);
        }
    }

    /// <summary>
    /// Finds the position to insert process rules: after sniff/dns/private-ip rules,
    /// before geo/domain/catch-all rules.
    /// </summary>
    private static int FindRouteInsertIndex(JArray rules, bool isActionBased)
    {
        int index = 0;

        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i] as JObject;
            if (rule == null) continue;

            if (isActionBased)
            {
                var action = rule["action"]?.ToString();
                if (action == "sniff" || action == "hijack-dns")
                {
                    index = i + 1;
                    continue;
                }
            }
            else
            {
                // Legacy: dns-out rule
                if (rule["protocol"]?.ToString() == "dns")
                {
                    index = i + 1;
                    continue;
                }
            }

            // ip_is_private always before process rules
            if (rule["ip_is_private"]?.Value<bool>() == true)
            {
                index = i + 1;
                continue;
            }

            // clash_mode rules before process rules
            if (rule["clash_mode"] != null)
            {
                index = i + 1;
                continue;
            }

            break;
        }

        return index;
    }

    // ─── Private: Inject DNS rules ───────────────────────────────────────────

    private static void InjectDnsRules(JObject config, List<string> processes, bool isActionBased)
    {
        var dns = config["dns"] as JObject;
        if (dns == null) return; // no DNS config → user handles DNS externally

        var servers = dns["servers"] as JArray;
        if (servers == null || servers.Count == 0) return;

        // Find the remote DNS server tag: first server with a proxy detour
        string? remoteTag = null;
        foreach (var server in servers)
        {
            var detour = server["detour"]?.ToString();
            if (!string.IsNullOrEmpty(detour) && detour != "direct")
            {
                remoteTag = server["tag"]?.ToString();
                break;
            }
        }

        // Fallback: first server
        if (string.IsNullOrEmpty(remoteTag))
            remoteTag = servers[0]["tag"]?.ToString();

        if (string.IsNullOrEmpty(remoteTag)) return;

        var rules = dns["rules"] as JArray;
        if (rules == null)
        {
            rules = new JArray();
            dns["rules"] = rules;
        }

        // Remove any previously injected process_name DNS rules
        for (int i = rules.Count - 1; i >= 0; i--)
        {
            if (rules[i]["process_name"] != null)
                rules.RemoveAt(i);
        }

        // Inject process DNS rule (high priority — at beginning)
        var processArray = new JArray(processes.Cast<object>().ToArray());

        JObject dnsRule;
        if (isActionBased)
        {
            dnsRule = new JObject
            {
                ["process_name"] = processArray,
                ["action"] = "route",
                ["server"] = remoteTag
            };
        }
        else
        {
            dnsRule = new JObject
            {
                ["process_name"] = processArray,
                ["server"] = remoteTag
            };
        }

        rules.Insert(0, dnsRule);
    }

    // ─── Private: Ensure required fields ────────────────────────────────────

    /// <summary>
    /// Ensures route.default_domain_resolver is set (required in sing-box 1.13+).
    /// Uses the first DNS server with a "direct" detour, or the first server.
    /// </summary>
    private static void EnsureDefaultDomainResolver(JObject config)
    {
        var route = config["route"] as JObject;
        if (route == null) return;

        // Find a local DNS server tag (no proxy detour)
        var servers = config.SelectToken("dns.servers") as JArray;
        if (servers == null || servers.Count == 0) return;

        string? localTag = null;
        foreach (var server in servers)
        {
            var detour = server["detour"]?.ToString();
            var type = server["type"]?.ToString();
            if (detour == "direct" || string.IsNullOrEmpty(detour) &&
                (type == "local" || type == "udp" || type == "dhcp"))
            {
                localTag = server["tag"]?.ToString();
                break;
            }
        }

        // Fallback: first server
        if (string.IsNullOrEmpty(localTag))
            localTag = servers[0]["tag"]?.ToString();

        // Always set to local tag — using proxy DNS as domain resolver adds latency
        if (!string.IsNullOrEmpty(localTag))
            route["default_domain_resolver"] = localTag;
    }

    // ─── Private: Ensure Clash API ───────────────────────────────────────────

    private static void EnsureClashApi(JObject config, string clashApiAddr)
    {
        var experimental = config["experimental"] as JObject;
        if (experimental == null)
        {
            experimental = new JObject();
            config["experimental"] = experimental;
        }

        var clashApi = experimental["clash_api"] as JObject;
        if (clashApi == null)
        {
            clashApi = new JObject();
            experimental["clash_api"] = clashApi;
        }

        // Don't override if user already set it
        if (clashApi["external_controller"] == null)
            clashApi["external_controller"] = clashApiAddr;
    }

    // ─── Private: Migrate legacy config to 1.13+ ──────────────────────────

    /// <summary>
    /// Migrates legacy config features to sing-box 1.13+ format:
    /// 1. Legacy DNS servers ("address": "tls://...") → type-based format (FATAL in 1.13.3)
    /// 2. Legacy DNS rules with "outbound" field → removed (FATAL in 1.13.3)
    /// 3. geosite/geoip rules → removed (require .db files not bundled)
    /// 4. "block"/"dns" outbound types → removed + route rules converted to actions
    /// 5. Legacy inbound sniff fields → removed (moved to route actions)
    /// </summary>
    private static void StripUnsupportedFeatures(JObject config, List<string>? excludeAddresses = null)
    {
        // 1. Convert legacy DNS server format to type-based
        var dnsServers = config.SelectToken("dns.servers") as JArray;
        if (dnsServers != null)
        {
            foreach (var server in dnsServers)
            {
                var obj = server as JObject;
                if (obj == null) continue;

                // Remove "detour":"direct" — sing-box 1.13 FATAL: "detour to empty direct makes no sense"
                // Local DNS servers go direct by default, explicit detour is redundant and breaks 1.13.
                if (obj["detour"]?.ToString() == "direct")
                    obj.Remove("detour");

                var address = obj["address"]?.ToString();
                if (address == null || obj["type"] != null) continue; // already new format

                obj.Remove("address");

                // Convert "address_resolver" → "domain_resolver"
                var addrResolver = obj["address_resolver"]?.ToString();
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
                    var uri = new Uri(address);
                    var scheme = uri.Scheme;

                    // Upgrade DoT (tls, port 853) → DoH (https, port 443) for better performance.
                    // DoT is often slower/blocked; DoH uses HTTP/2 multiplexing and port 443.
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

        // 1b. Optimize DNS — prevent IPv6 delays, ensure local DNS final
        var dns = config["dns"] as JObject;
        if (dns != null)
        {
            // Force ipv4_only — anything else causes IPv6 AAAA timeout (+100-300ms per query)
            var strategy = dns["strategy"]?.ToString();
            if (strategy != "ipv4_only")
                dns["strategy"] = "ipv4_only";

            // Find local DNS server tag (no detour, or type=udp/local/dhcp)
            string? localTag = null;
            if (dnsServers != null)
            {
                foreach (var s in dnsServers)
                {
                    var t = s["type"]?.ToString();
                    var d = s["detour"]?.ToString();
                    if (string.IsNullOrEmpty(d) && (t == "udp" || t == "local" || t == "dhcp"))
                    {
                        localTag = s["tag"]?.ToString();
                        break;
                    }
                }
            }

            // Force DNS final to local — routing ALL DNS through VPN adds 100-400ms per query.
            // Only targeted process DNS should go through VPN (handled by injected DNS rules).
            if (localTag != null)
            {
                var finalTag = dns["final"]?.ToString();
                if (finalTag != localTag)
                    dns["final"] = localTag;
            }
        }

        // 2. Remove deprecated DNS rules ("outbound" field is FATAL in 1.13.3, geosite/geoip need .db)
        var dnsRules = config.SelectToken("dns.rules") as JArray;
        if (dnsRules != null)
        {
            for (int i = dnsRules.Count - 1; i >= 0; i--)
            {
                var rule = dnsRules[i] as JObject;
                if (rule == null) continue;

                if (rule["geosite"] != null || rule["geoip"] != null ||
                    rule["outbound"] != null)
                    dnsRules.RemoveAt(i);
            }
        }

        // 3. Remove "block" and "dns" outbound types (removed in sing-box 1.13)
        var outbounds = config["outbounds"] as JArray;
        var removedTags = new HashSet<string>();
        if (outbounds != null)
        {
            for (int i = outbounds.Count - 1; i >= 0; i--)
            {
                var type = outbounds[i]["type"]?.ToString();
                if (type == "block" || type == "dns")
                {
                    removedTags.Add(outbounds[i]["tag"]?.ToString() ?? "");
                    outbounds.RemoveAt(i);
                }
            }
        }

        // 4. Convert route rules that reference removed outbounds + remove geosite/geoip
        var routeRules = config.SelectToken("route.rules") as JArray;
        if (routeRules != null)
        {
            for (int i = routeRules.Count - 1; i >= 0; i--)
            {
                var rule = routeRules[i] as JObject;
                if (rule == null) continue;

                // Remove geosite/geoip rules (no databases)
                if (rule["geosite"] != null || rule["geoip"] != null)
                {
                    routeRules.RemoveAt(i);
                    continue;
                }

                // Convert rules pointing to removed outbounds
                var outbound = rule["outbound"]?.ToString();
                if (outbound != null && removedTags.Contains(outbound))
                {
                    rule.Remove("outbound");
                    // "dns-out" → hijack-dns, "block" → reject
                    rule["action"] = rule["protocol"]?.ToString() == "dns"
                        ? "hijack-dns"
                        : "reject";
                }
            }
        }

        // 5. Normalize inbounds: remove deprecated fields, fix TUN settings
        var inbounds = config["inbounds"] as JArray;
        if (inbounds != null)
        {
            foreach (var inbound in inbounds)
            {
                var obj = inbound as JObject;
                if (obj == null) continue;

                // Remove deprecated sniff fields (moved to route actions in 1.12+)
                obj.Remove("sniff");
                obj.Remove("sniff_override_destination");
                obj.Remove("sniff_timeout");
                obj.Remove("domain_strategy");

                // TUN-specific fixes
                if (obj["type"]?.ToString() == "tun")
                {
                    // Force strict_route=false — true causes dual-stack errors on Windows
                    obj["strict_route"] = false;
                    // Set stack to "system" (default for Windows, avoids gVisor dependency)
                    if (obj["stack"] == null)
                        obj["stack"] = "system";

                    // Inject route_exclude_address from settings (WireGuard/AmneziaWG subnets)
                    // VpnEngine auto-detects these but they only get into settings.Tun,
                    // not into the custom config's TUN inbound.
                    if (excludeAddresses != null && excludeAddresses.Count > 0)
                    {
                        var existing = obj["route_exclude_address"] as JArray ?? new JArray();
                        var merged = new HashSet<string>(
                            existing.Select(t => t.ToString()),
                            StringComparer.OrdinalIgnoreCase);
                        foreach (var addr in excludeAddresses)
                            merged.Add(addr);
                        obj["route_exclude_address"] = new JArray(merged.ToArray());
                    }
                }
            }
        }

        // 6. Ensure log output goes to our log file (so we can debug startup failures)
        var log = config["log"] as JObject;
        if (log == null)
        {
            log = new JObject();
            config["log"] = log;
        }
        var logPath = Environment.ExpandEnvironmentVariables(
            @"%ProgramData%\VPNRouter\logs\singbox.log");
        log["output"] = logPath;
        log["timestamp"] = true;
    }

    // ─── Private: Cleanup helpers ────────────────────────────────────────────

    /// <summary>
    /// Removes any route rules that have process_name (our injected rules).
    /// This makes re-injection idempotent — safe to call multiple times.
    /// </summary>
    private static void RemoveInjectedProcessRules(JArray rules)
    {
        for (int i = rules.Count - 1; i >= 0; i--)
        {
            if (rules[i]["process_name"] != null)
                rules.RemoveAt(i);
        }
    }
}
