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

            InjectRouteRules(config, processes, proxyTag, isActionBased);
            InjectDnsRules(config, processes, isActionBased);
        }

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

    // ─── Private: Inject route rules ─────────────────────────────────────────

    private static void InjectRouteRules(JObject config, List<string> processes,
        string proxyTag, bool isActionBased)
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

        // Build the process route rule
        var processArray = new JArray(processes.Cast<object>().ToArray());

        JObject processRule;
        if (isActionBased)
        {
            processRule = new JObject
            {
                ["process_name"] = processArray,
                ["action"] = "route",
                ["outbound"] = proxyTag
            };
        }
        else
        {
            processRule = new JObject
            {
                ["process_name"] = processArray,
                ["outbound"] = proxyTag
            };
        }

        // Insert after system rules (sniff, hijack-dns, ip_is_private) but before
        // geo rules and catch-all. This ensures process rules have correct priority.
        var insertIndex = FindRouteInsertIndex(rules, isActionBased);
        rules.Insert(insertIndex, processRule);
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
