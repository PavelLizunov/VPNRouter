using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Takes a user-provided sing-box JSON config and injects process-based routing rules.
/// Preserves all existing config (outbounds, DNS servers, TLS, etc.) —
/// only adds process_name route/DNS rules and Clash API for hot-reload.
///
/// Supports both legacy (outbound-based) and 1.12+ (action-based) config formats.
/// Auto-detects format from existing route rules.
///
/// <para>Phase 4 (2026-05-18) — migrated from Newtonsoft
/// <c>JObject</c>/<c>JArray</c>/<c>JToken</c> to System.Text.Json
/// <c>JsonObject</c>/<c>JsonArray</c>/<c>JsonNode</c>. The injector
/// behaviour is byte-equivalent — same routing-rule shape, same
/// idempotency, same StripUnsupportedFeatures migration steps. The
/// emitted JSON uses <see cref="InjectorOutputOptions"/> with
/// <c>WriteIndented=true</c> to match the pre-migration
/// <c>Formatting.Indented</c> output exactly. sing-box check
/// integration tests pin the output shape.</para>
/// </summary>
public static class CustomConfigInjector
{
    /// <summary>
    /// STJ serialization options for the injector's output. Mirrors the
    /// pre-Phase-4 <c>Formatting.Indented</c> Newtonsoft behaviour
    /// byte-for-byte (2-space indent, LF newlines on Unix / CRLF
    /// preserved on Windows by the file-write layer). Nulls are
    /// preserved verbatim — the injector pulls/pushes user-authored
    /// fields that may legitimately carry null shapes.
    /// </summary>
    internal static readonly JsonSerializerOptions InjectorOutputOptions = new()
    {
        WriteIndented = true,
        // Phase 6 — .NET 10 ships with
        // JsonSerializerIsReflectionEnabledByDefault=false. Without a
        // TypeInfoResolver, JsonNode.ToJsonString(options) throws
        // "JsonSerializerOptions instance must specify a TypeInfoResolver"
        // when iterating JsonValueCustomized<string> entries (the
        // primitive leaves of the injected node tree). Combine the
        // source-gen context with the reflective fallback so the
        // injector's node tree (mix of registered DTOs + primitives)
        // serializes cleanly on both .NET 8 and .NET 10 runtimes.
        TypeInfoResolver = JsonTypeInfoResolver.Combine(
            Json.AppJsonContext.Default,
            new DefaultJsonTypeInfoResolver()),
    };
    /// <summary>
    /// Inject process routing into a raw sing-box JSON config.
    /// Returns the modified JSON string ready for sing-box.
    ///
    /// <para>v2.32.3-r1 (2026-05-17): throws <see cref="PlaceholderConfigException"/>
    /// when the first proxy-typed outbound in <paramref name="rawJson"/> carries
    /// a known placeholder fingerprint (Reality public_key, Reality short_id,
    /// or server IP — see <see cref="PlaceholderGuard"/>). This is the
    /// custom-config equivalent of F-A/B/D's input gates: we want users who
    /// paste a sing-box JSON containing the Android smoke-test placeholder
    /// (the <c>DnT9...</c> pubkey) to get an actionable error at paste time
    /// instead of letting sing-box launch and then F-E catching it.</para>
    /// </summary>
    public static string Inject(string rawJson, IEnumerable<string> processNames, AppSettings settings)
    {
        var config = JsonNode.Parse(rawJson) as JsonObject
            ?? throw new JsonException("Custom sing-box config root is not an object");

        // ── Phase 2c placeholder gate (v2.32.3-r1) ────────────────────────
        // Inspect the FIRST proxy-typed outbound (same heuristic
        // ConfigSanityCheck uses at runtime). The shared helper lives in
        // ConfigSanityCheck so the two layers stay in sync — single source
        // of truth on a parsed sing-box outbound.
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

        // Filter wildcards — sing-box process_name doesn't support globs.
        // Preserve original case — sing-box matching is case-sensitive.
        var scannerProcesses = processNames
            .Where(p => !p.Contains('*') && !p.Contains('?'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // v2.39.0 (apps/public-configs audit P0 #147): custom-JSON mode must
        // honour the SAME Apps Include/Exclude + Full-Tunnel policy as generated
        // mode (ConfigGenerator.BuildRoute). Before this, Inject ALWAYS routed
        // the scanner list THROUGH the proxy and never forced final=proxy for
        // full tunnel, so two leaks were possible:
        //   (a) EXCLUDE mode was inverted — the apps the user explicitly wanted
        //       KEPT OUT of the VPN were the only ones tunnelled, and everything
        //       else (which should have been tunnelled) fell to final=direct.
        //   (b) FULL tunnel leaked everything direct whenever the user's JSON
        //       carried final=direct (or omitted final).
        // The block below mirrors ConfigGenerator exactly.
        var routingAppsMode = (settings.App.RoutingAppsMode ?? "include")
            .ToLowerInvariant();
        var isExcludeMode = routingAppsMode == "exclude";
        var isFullTunnel = (settings.App.RoutingMode ?? "split")
            .Equals("full", StringComparison.OrdinalIgnoreCase);

        // Resolve the effective per-app list the SAME way ConfigGenerator does:
        // exclude → RoutingAppsExclude; include → explicit RoutingAppsInclude
        // when the user populated it, else the legacy scanner list (keeps users
        // who never opened the Apps tab byte-for-byte on their old behaviour).
        List<string> processes;
        if (isExcludeMode)
        {
            processes = (settings.App.RoutingAppsExclude ?? new List<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Where(p => !p.Contains('*') && !p.Contains('?'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else
        {
            var explicitInclude = (settings.App.RoutingAppsInclude ?? new List<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Where(p => !p.Contains('*') && !p.Contains('?'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            processes = explicitInclude.Count > 0 ? explicitInclude : scannerProcesses;
        }

        bool isActionBased = DetectActionFormat(config);

        // Resolve the proxy outbound tag ONCE — both the per-app route rules and
        // route.final reference it. A custom config keeps its OWN selector tag,
        // so we can't hard-code "proxy" the way ConfigGenerator (which builds the
        // outbounds itself) does.
        var proxyTag = FindProxyOutboundTag(config);

        // Per-app routing applies ONLY in split tunnel. Full tunnel sends every
        // process through route.final = proxy below, so no per-app rules needed.
        if (!isFullTunnel && processes.Count > 0)
        {
            if (isExcludeMode)
            {
                // Exclude: the listed apps BYPASS the VPN (→ direct). Their DNS
                // resolves locally too, matching their direct traffic path.
                InjectRouteRules(config, processes, "direct", null, isActionBased);
                InjectDnsRules(config, processes, isActionBased, useRemoteDns: false);
            }
            else
            {
                // Include: the listed apps go THROUGH the proxy. Auto-detect a
                // TCP/UDP split (VLESS for TCP, TUIC/Hysteria2 for UDP) when the
                // selector carries both, for optimal voice/video performance.
                var (tcpTag, udpTag) = DetectTcpUdpSplit(config, proxyTag);
                InjectRouteRules(config, processes, tcpTag, udpTag, isActionBased);
                InjectDnsRules(config, processes, isActionBased, useRemoteDns: true);
            }
        }

        // Inject Russian geo bypass — RU sites/IPs go direct (real IP),
        // protects VPN server from being blacklisted by RU services.
        // Only injected if both .srs files are present locally.
        if (settings.App.BypassRussianTraffic && GeoDataDownloader.AreGeoFilesAvailable())
        {
            InjectGeoBypassRules(config, isActionBased);
        }

        // Migrate legacy features to sing-box 1.13+ format
        StripUnsupportedFeatures(config, settings.Tun.RouteExcludeAddress, settings.App.ForceIpv4Only, settings.App.StrictDns);

        // Align route.final with the routing policy — mirrors
        // ConfigGenerator.BuildRoute's finalOutbound: full tunnel OR exclude
        // mode send everything-else through the proxy; include split sends
        // everything-else direct. This is the backstop that closes the
        // full-tunnel leak (unmatched traffic could otherwise fall to a
        // user-supplied final=direct) AND completes the exclude-mode flip
        // (listed apps pinned to direct above, everything else → proxy here).
        // Fail-closed: create the route section if the custom config omits it,
        // so a full-tunnel config without a route block still pins final=proxy
        // instead of leaking on sing-box's implicit default.
        var route = config["route"] as JsonObject;
        if (route == null)
        {
            route = new JsonObject { ["rules"] = new JsonArray() };
            config["route"] = route;
        }
        route["final"] = (isFullTunnel || isExcludeMode) ? proxyTag : "direct";

        EnsureDefaultDomainResolver(config);
        EnsureClashApi(config, settings.SingBox.ClashApi);
        EnsureUrltest(config);

        return config.ToJsonString(InjectorOutputOptions);
    }

    /// <summary>
    /// If the proxy outbound is a selector, wrap children in a urltest for auto health check.
    /// Without urltest, selector doesn't pre-establish connections → 12s cold start on first request.
    /// Adds a urltest only if one doesn't already exist.
    /// </summary>
    private static void EnsureUrltest(JsonObject config)
    {
        var outbounds = config["outbounds"] as JsonArray;
        if (outbounds == null) return;

        // Find the selector
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

        // Check if any child is already a urltest
        foreach (var ob in outbounds)
        {
            if (ob is JsonObject obObj && StjNodeHelpers.AsString(obObj["type"]) == "urltest")
                return; // already has urltest, don't add another
        }

        // Create urltest from selector's children.
        // Phase 6 — Wave 31b: cast to (JsonNode?) so the compiler picks
        // JsonArray.Add(JsonNode?) instead of Add<T>(T) (IL3050).
        var childTagsArray = new JsonArray();
        foreach (var c in children)
        {
            childTagsArray.Add((JsonNode?)JsonValue.Create(StjNodeHelpers.AsString(c) ?? ""));
        }
        var urltest = new JsonObject
        {
            ["type"] = "urltest",
            ["tag"] = "auto",
            ["outbounds"] = childTagsArray,
            ["url"] = "https://www.gstatic.com/generate_204",
            ["interval"] = "5m"
        };

        // Insert urltest before selector
        var selectorIdx = outbounds.IndexOf(selector);
        outbounds.Insert(selectorIdx, urltest);

        // Add "auto" to selector's children (first = default)
        children.Insert(0, "auto");
    }

    /// <summary>
    /// Validates a custom config has the minimum required structure.
    /// Returns (isValid, errors).
    /// </summary>
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

        // Must have outbounds
        var outbounds = config["outbounds"] as JsonArray;
        if (outbounds == null || outbounds.Count == 0)
        {
            errors.Add("No 'outbounds' array in config");
            return (false, errors);
        }

        // Must have at least one proxy-like outbound (not just direct/block/dns)
        var hasProxy = outbounds.Any(o =>
        {
            var type = StjNodeHelpers.AsString(o?["type"]);
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
        var dir = AppPaths.ConfigDir;
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
        var dir = AppPaths.ConfigDir;
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

    // ─── Private: Find proxy outbound ────────────────────────────────────────

    /// <summary>
    /// Finds the primary proxy outbound tag. Priority:
    /// 1. "selector" type (manual switching between protocols)
    /// 2. "urltest" type (auto-failover)
    /// 3. First non-direct/block/dns outbound (vless, hysteria2, tuic, etc.)
    ///
    /// <para>Bug-r9-F-DEFENSIVE (2026-05-11): if the matching outbound has no
    /// explicit <c>tag</c>, we assign <c>"custom-proxy"</c> instead of the
    /// historical <c>"proxy"</c> fallback. The old behaviour silently shadowed
    /// any subscription outbound that legitimately used the tag <c>proxy</c>,
    /// which produced a silent privacy leak when a user pasted a sing-box JSON
    /// containing a stale / placeholder server. See plans/
    /// vpnrouter-android-r9-user-bug-batch.md (stas log analysis).</para>
    /// </summary>
    private static string FindProxyOutboundTag(JsonObject config)
    {
        var outbounds = config["outbounds"] as JsonArray;
        if (outbounds == null) return "custom-proxy";

        // 1. Selector (user-switchable)
        foreach (var ob in outbounds)
        {
            if (ob is JsonObject obObj && StjNodeHelpers.AsString(obObj["type"]) == "selector")
                return ResolveOrAssignProxyTag(obObj);
        }

        // 2. URLTest (auto-failover)
        foreach (var ob in outbounds)
        {
            if (ob is JsonObject obObj && StjNodeHelpers.AsString(obObj["type"]) == "urltest")
                return ResolveOrAssignProxyTag(obObj);
        }

        // 3. First proxy-like outbound
        foreach (var ob in outbounds)
        {
            if (ob is not JsonObject obObj) continue;
            var type = StjNodeHelpers.AsString(obObj["type"]);
            if (type != "direct" && type != "block" && type != "dns")
                return ResolveOrAssignProxyTag(obObj);
        }

        return "custom-proxy";
    }

    /// <summary>
    /// Returns the outbound's existing tag, or assigns <c>"custom-proxy"</c>
    /// (mutating the outbound in place) and logs a WARN when the tag is empty.
    /// Mutating is required so the matching <c>outbound</c> referenced in our
    /// injected route rules actually exists in the sing-box config — otherwise
    /// sing-box rejects the rule at startup or silently falls through.
    /// </summary>
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

    // ─── Private: Detect config format ───────────────────────────────────────

    /// <summary>
    /// Detects whether the config uses 1.12+ action-based format or legacy outbound-based.
    /// If any route rule has an "action" field → action-based.
    /// </summary>
    private static bool DetectActionFormat(JsonObject config)
    {
        var rules = StjNodeHelpers.SelectToken(config, "route.rules") as JsonArray;
        if (rules == null) return true; // no rules yet → default to modern format

        foreach (var rule in rules)
        {
            if (rule is JsonObject rj && rj["action"] != null)
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
    private static (string tcpTag, string udpTag) DetectTcpUdpSplit(JsonObject config, string proxyTag)
    {
        var outbounds = config["outbounds"] as JsonArray;
        if (outbounds == null) return (proxyTag, proxyTag);

        // Find the proxy outbound (selector/urltest)
        var proxyOutbound = outbounds.FirstOrDefault(o => StjNodeHelpers.AsString(o?["tag"]) == proxyTag);
        if (proxyOutbound == null) return (proxyTag, proxyTag);

        var proxyType = StjNodeHelpers.AsString(proxyOutbound["type"]);
        if (proxyType != "selector" && proxyType != "urltest") return (proxyTag, proxyTag);

        var childTags = proxyOutbound["outbounds"] as JsonArray;
        if (childTags == null || childTags.Count < 2) return (proxyTag, proxyTag);

        // Categorize children by protocol
        string? vlessTag = null;
        string? quicTag = null; // tuic, hysteria, hysteria2

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

        // Both found → split TCP/UDP
        if (vlessTag != null && quicTag != null)
            return (vlessTag, quicTag);

        return (proxyTag, proxyTag);
    }

    // ─── Private: Inject route rules ─────────────────────────────────────────

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

        // Remove any pre-existing process_name rules. Inject() always starts
        // from the user's pristine JSON, so these are the user's OWN rules — in
        // custom mode VPNRouter manages per-app routing, so they're replaced by
        // our injected list. W1.4-a: warn so the override is never silent (a
        // power-user's hand-written process_name rule would otherwise vanish
        // without trace, and any app it covered that we don't would fall to
        // route.final).
        var replacedUserRules = RemoveInjectedProcessRules(rules);
        if (replacedUserRules > 0)
            Serilog.Log.Logger.Warning(
                "Custom Config Mode: replaced {Count} user-defined process_name route rule(s) — " +
                "VPNRouter manages per-app routing in custom mode.", replacedUserRules);

        var insertIndex = FindRouteInsertIndex(rules, isActionBased);
        bool hasSplit = udpTag != null && udpTag != tcpTag;

        if (hasSplit)
        {
            // TCP/UDP split: UDP → QUIC protocol (tuic/hysteria2), TCP → VLESS.
            // NOTE: STJ JsonNode disallows the same node being attached to two
            // parents (Newtonsoft tolerated it; STJ throws InvalidOperationException
            // "node already has parent"). Build a fresh JsonArray per rule.
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
            // UDP first (higher priority for voice/video), then TCP
            rules.Insert(insertIndex, tcpRule);
            rules.Insert(insertIndex, udpRule);
        }
        else
        {
            // Single outbound — all traffic through proxy
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

    /// <summary>
    /// Build a fresh JsonArray of process_name strings. STJ JsonNode
    /// requires a distinct array per use site (a single JsonArray
    /// cannot be a child of two parents — InvalidOperationException
    /// at attach time). Newtonsoft's DeepClone is the legacy idiom this
    /// helper replaces; building from the input list is cleaner and
    /// avoids a clone-then-reattach pattern.
    /// </summary>
    private static JsonArray BuildProcessNameArray(IEnumerable<string> processes)
    {
        // Phase 6 — Wave 31b: cast to (JsonNode?) so the compiler picks
        // JsonArray.Add(JsonNode?) instead of Add<T>(T) (IL3050).
        var array = new JsonArray();
        foreach (var p in processes)
            array.Add((JsonNode?)JsonValue.Create(p));
        return array;
    }

    /// <summary>
    /// Finds the position to insert process rules: after sniff/dns/private-ip rules,
    /// before geo/domain/catch-all rules.
    /// </summary>
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
                // Legacy: dns-out rule
                if (StjNodeHelpers.AsString(rule["protocol"]) == "dns")
                {
                    index = i + 1;
                    continue;
                }
            }

            // ip_is_private always before process rules
            if (StjNodeHelpers.AsBool(rule["ip_is_private"]) == true)
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

    private static void InjectDnsRules(JsonObject config, List<string> processes, bool isActionBased, bool useRemoteDns)
    {
        var dns = config["dns"] as JsonObject;
        if (dns == null) return; // no DNS config → user handles DNS externally

        var servers = dns["servers"] as JsonArray;
        if (servers == null || servers.Count == 0) return;

        // Pick the DNS server the listed processes resolve through.
        //   Include mode (useRemoteDns) → the remote/proxy DNS server so the
        //     tunnelled apps don't leak their DNS queries to the ISP.
        //   Exclude mode → a LOCAL server so the bypassed apps resolve direct,
        //     matching their direct traffic path (set by InjectRouteRules above).
        var targetTag = useRemoteDns
            ? FindRemoteDnsTag(servers)
            : FindLocalDnsTag(servers);

        // Fallback: first server
        if (string.IsNullOrEmpty(targetTag))
            targetTag = StjNodeHelpers.AsString((servers[0] as JsonObject)?["tag"]);

        if (string.IsNullOrEmpty(targetTag)) return;

        var rules = dns["rules"] as JsonArray;
        if (rules == null)
        {
            rules = new JsonArray();
            dns["rules"] = rules;
        }

        // Remove any previously injected process_name DNS rules
        for (int i = rules.Count - 1; i >= 0; i--)
        {
            if (rules[i] is JsonObject rj && rj["process_name"] != null)
                rules.RemoveAt(i);
        }

        // Inject process DNS rule (high priority — at beginning)
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

    /// <summary>First DNS server routed through the proxy (non-empty detour
    /// other than "direct"). Returns null when every server is local.</summary>
    private static string? FindRemoteDnsTag(JsonArray servers)
    {
        foreach (var server in servers)
        {
            if (server is not JsonObject sObj) continue;
            var detour = StjNodeHelpers.AsString(sObj["detour"]);
            if (!string.IsNullOrEmpty(detour) && detour != "direct")
                return StjNodeHelpers.AsString(sObj["tag"]);
        }
        return null;
    }

    /// <summary>First DNS server that resolves locally (direct/dns-direct detour,
    /// or a local-type server with no detour). Shared with
    /// <see cref="EnsureDefaultDomainResolver"/> so the two stay in sync.</summary>
    private static string? FindLocalDnsTag(JsonArray servers)
    {
        foreach (var server in servers)
        {
            if (server is not JsonObject sObj) continue;
            var detour = StjNodeHelpers.AsString(sObj["detour"]);
            var type = StjNodeHelpers.AsString(sObj["type"]);
            if (detour == "direct" || detour == "dns-direct" ||
                string.IsNullOrEmpty(detour) && (type == "local" || type == "udp" || type == "dhcp"))
                return StjNodeHelpers.AsString(sObj["tag"]);
        }
        return null;
    }

    // ─── Private: Inject Russian geo bypass ───────────────────────────────

    private const string GeoIpRuleSetTag = "vpnrouter-geoip-ru";
    private const string GeoSiteRuleSetTag = "vpnrouter-geosite-ru";
    private const string DirectDnsRuTag = "vpnrouter-dns-ru";

    /// <summary>
    /// Injects sing-box rule_set definitions and route/dns rules so that
    /// Russian sites and IPs go through "direct" outbound (real IP).
    /// This protects the VPN server from being detected and blacklisted
    /// by Russian services.
    ///
    /// Architecture:
    ///   1. rule_set blocks pointing to local .srs files (downloaded at runtime)
    ///   2. DNS server "vpnrouter-dns-ru" → Yandex 77.88.8.8 via dns-direct
    ///   3. DNS rule: geosite-ru → vpnrouter-dns-ru (RU domains use RU DNS)
    ///   4. Route rule: geosite-ru OR geoip-ru → outbound:direct
    ///
    /// Idempotent: removes previously injected rules before adding new ones.
    /// </summary>
    private static void InjectGeoBypassRules(JsonObject config, bool isActionBased)
    {
        InjectGeoRuleSets(config);
        InjectGeoDnsServer(config);
        InjectGeoDnsRule(config, isActionBased);
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

        // Remove any previously injected rule sets (idempotent)
        for (int i = ruleSet.Count - 1; i >= 0; i--)
        {
            var tag = StjNodeHelpers.AsString((ruleSet[i] as JsonObject)?["tag"]);
            if (tag == GeoIpRuleSetTag || tag == GeoSiteRuleSetTag)
                ruleSet.RemoveAt(i);
        }

        // Forward slashes work on both Windows and macOS in sing-box config
        var geoIpPath = AppPaths.GeoIpRuPath.Replace('\\', '/');
        var geoSitePath = AppPaths.GeoSiteRuPath.Replace('\\', '/');

        // Phase 6 — Wave 31b: cast each JsonObject to (JsonNode?) so the
        // compiler picks JsonArray.Add(JsonNode?) instead of Add<T>(T) (IL3050).
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

    private static void InjectGeoDnsServer(JsonObject config)
    {
        var dns = config["dns"] as JsonObject;
        if (dns == null) return;

        var servers = dns["servers"] as JsonArray;
        if (servers == null)
        {
            servers = new JsonArray();
            dns["servers"] = servers;
        }

        // Remove previously injected RU DNS server (idempotent)
        for (int i = servers.Count - 1; i >= 0; i--)
        {
            if (StjNodeHelpers.AsString((servers[i] as JsonObject)?["tag"]) == DirectDnsRuTag)
                servers.RemoveAt(i);
        }

        // Yandex DNS via dns-direct outbound (real NIC, no proxy, no loop).
        // Phase 6 — Wave 31b: cast to (JsonNode?) for AOT-clean Add (IL3050).
        servers.Add((JsonNode?)new JsonObject
        {
            ["type"] = "udp",
            ["tag"] = DirectDnsRuTag,
            ["server"] = "77.88.8.8",
            ["detour"] = "dns-direct"
        });
    }

    private static void InjectGeoDnsRule(JsonObject config, bool isActionBased)
    {
        var dns = config["dns"] as JsonObject;
        if (dns == null) return;

        var rules = dns["rules"] as JsonArray;
        if (rules == null)
        {
            rules = new JsonArray();
            dns["rules"] = rules;
        }

        // Remove previously injected geo DNS rule (idempotent)
        for (int i = rules.Count - 1; i >= 0; i--)
        {
            var rule = rules[i] as JsonObject;
            if (rule == null) continue;
            var server = StjNodeHelpers.AsString(rule["server"]);
            if (server == DirectDnsRuTag)
                rules.RemoveAt(i);
        }

        // RU domains → Russian DNS resolver (direct, not via VPN)
        // Insert after process_name rules but before any catch-all
        var dnsRule = new JsonObject
        {
            // Phase 6 — Wave 31b: cast literal to (JsonNode?) inside the
            // collection initializer so the desugared .Add call picks the
            // non-generic Add(JsonNode?) overload (IL3050).
            ["rule_set"] = new JsonArray { (JsonNode?)JsonValue.Create(GeoSiteRuleSetTag) },
            ["server"] = DirectDnsRuTag
        };
        if (isActionBased)
            dnsRule["action"] = "route";

        // Find insertion point: after process_name rules
        int insertAt = 0;
        for (int i = 0; i < rules.Count; i++)
        {
            if (rules[i] is JsonObject rj && rj["process_name"] != null)
                insertAt = i + 1;
            else
                break;
        }
        rules.Insert(insertAt, dnsRule);
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

        // Remove previously injected geo route rules (idempotent)
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

        // Insert geo bypass rules — geo wins over process_name routing
        // (RU services NEVER see VPN IP, even from VPN-routed processes).
        // Place before process_name rules but after sniff/dns/private-ip.
        int insertAt = FindGeoInsertIndex(rules, isActionBased);

        // Single rule with both rule_sets — sing-box matches if ANY in the array matches.
        // Phase 6 — Wave 31b: cast each literal to (JsonNode?) so the
        // desugared .Add calls pick the non-generic Add(JsonNode?) (IL3050).
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

    /// <summary>
    /// Finds the position to insert geo rules: after sniff/dns/private-ip,
    /// BEFORE process_name rules (geo wins over process routing).
    /// </summary>
    private static int FindGeoInsertIndex(JsonArray rules, bool isActionBased)
    {
        int index = 0;
        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i] as JsonObject;
            if (rule == null) continue;

            // Skip sniff/hijack-dns/dns-out rules
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

            // Skip ip_is_private
            if (StjNodeHelpers.AsBool(rule["ip_is_private"]) == true)
            {
                index = i + 1;
                continue;
            }

            // Skip clash_mode
            if (rule["clash_mode"] != null)
            {
                index = i + 1;
                continue;
            }

            break;
        }

        return index;
    }

    // ─── Private: Ensure required fields ────────────────────────────────────

    /// <summary>
    /// Ensures route.default_domain_resolver is set (required in sing-box 1.13+).
    /// Uses the first DNS server with a "direct" detour, or the first server.
    /// </summary>
    private static void EnsureDefaultDomainResolver(JsonObject config)
    {
        var route = config["route"] as JsonObject;
        if (route == null) return;

        // Find a local DNS server tag (no proxy detour)
        var servers = StjNodeHelpers.SelectToken(config, "dns.servers") as JsonArray;
        if (servers == null || servers.Count == 0) return;

        var localTag = FindLocalDnsTag(servers);

        // Fallback: first server
        if (string.IsNullOrEmpty(localTag))
            localTag = StjNodeHelpers.AsString((servers[0] as JsonObject)?["tag"]);

        // Always set to local tag — using proxy DNS as domain resolver adds latency
        if (!string.IsNullOrEmpty(localTag))
            route["default_domain_resolver"] = localTag;
    }

    // ─── Private: Ensure Clash API ───────────────────────────────────────────

    private static void EnsureClashApi(JsonObject config, string clashApiAddr)
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
    private static void StripUnsupportedFeatures(JsonObject config, List<string>? excludeAddresses = null, bool forceIpv4Only = true, bool strictDns = false)
    {
        // 1. Convert legacy DNS server format to type-based
        var dnsServers = StjNodeHelpers.SelectToken(config, "dns.servers") as JsonArray;
        if (dnsServers != null)
        {
            foreach (var server in dnsServers)
            {
                var obj = server as JsonObject;
                if (obj == null) continue;

                var address = StjNodeHelpers.AsString(obj["address"]);
                if (address == null || obj["type"] != null) continue; // already new format

                obj.Remove("address");

                // Convert "address_resolver" → "domain_resolver"
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
                    // v2.30.7 — Uri ctor throws UriFormatException on
                    // malformed user-supplied DNS server addresses
                    // (e.g. "://no-scheme" or schemes with chars Uri
                    // doesn't grok). Without this guard the exception
                    // propagates up through Inject → HealthMonitor and
                    // shows as an opaque "configuration error" mid-
                    // hot-reload. Now we skip the broken entry and let
                    // the rest of StripUnsupportedFeatures continue.
                    if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
                    {
                        // Fall through to plain UDP, treating the address
                        // string as a hostname/IP. sing-box will reject
                        // it later if truly malformed, but the user gets
                        // a more specific error from sing-box than from us.
                        obj["type"] = "udp";
                        obj["server"] = address;
                        continue;
                    }
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

        // 1b. Convert type:local/dhcp → type:udp.
        // type:local uses getaddrinfo() → system resolver → ISP DNS (LEAK).
        // type:udp 1.0.0.1 sends plain text DNS to ISP visibility (still leaky).
        // type:https 1.1.1.1 (DoH) hides queries from ISP.
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
                        obj["server"] = "1.1.1.1";
                    if (obj["path"] == null)
                        obj["path"] = "/dns-query";
                    obj.Remove("detour"); // re-added in step 1c below as dns-direct
                }
            }
        }

        // 1c. Ensure non-proxy DNS servers bypass hijack-dns routing loop.
        // Without detour, DNS queries go through routing → protocol:dns → hijack-dns →
        // DNS module → same server → LOOP → 12s timeout on first request.
        // Can't use detour:"direct" — sing-box 1.13 FATAL: "detour to empty direct makes no sense".
        // Solution: create a dedicated "dns-direct" outbound with udp_fragment:true (makes it non-empty),
        // then point DNS servers to it. This bypasses routing entirely → real NIC → no loop.
        if (dnsServers != null)
        {
            bool needsDnsDirect = false;
            foreach (var server in dnsServers)
            {
                var obj = server as JsonObject;
                if (obj == null) continue;
                var detour = StjNodeHelpers.AsString(obj["detour"]);
                if (string.IsNullOrEmpty(detour) || detour == "direct")
                {
                    obj["detour"] = "dns-direct";
                    needsDnsDirect = true;
                }
            }

            // Add the dns-direct outbound if any DNS server needs it
            if (needsDnsDirect)
            {
                var dnsOutbounds = config["outbounds"] as JsonArray;
                if (dnsOutbounds != null && !dnsOutbounds.Any(o => StjNodeHelpers.AsString(o?["tag"]) == "dns-direct"))
                {
                    // Phase 6 — Wave 31b: cast to (JsonNode?) for AOT-clean Add (IL3050).
                    dnsOutbounds.Add((JsonNode?)new JsonObject
                    {
                        ["type"] = "direct",
                        ["tag"] = "dns-direct",
                        ["udp_fragment"] = true
                    });
                }
            }
        }

        // 1d. Optimize DNS — prevent IPv6 delays, ensure local DNS final
        var dns = config["dns"] as JsonObject;
        if (dns != null)
        {
            // Force ipv4_only when ForceIpv4Only is enabled (default).
            // Without ipv4_only: AAAA queries timeout +100-300ms per request AND
            // IPv6 traffic can leak past TUN if the OS has v6 connectivity.
            if (forceIpv4Only)
            {
                var strategy = StjNodeHelpers.AsString(dns["strategy"]);
                if (strategy != "ipv4_only")
                    dns["strategy"] = "ipv4_only";
            }

            // Find local DNS server tag (detour:"dns-direct" or "direct" = local, no proxy)
            string? localTag = null;
            // Find proxy DNS server tag (detour pointing to a non-direct outbound)
            string? proxyTag = null;
            if (dnsServers != null)
            {
                foreach (var s in dnsServers)
                {
                    if (s is not JsonObject sObj) continue;
                    var d = StjNodeHelpers.AsString(sObj["detour"]);
                    if ((d == "dns-direct" || d == "direct") && localTag == null)
                    {
                        localTag = StjNodeHelpers.AsString(sObj["tag"]);
                    }
                    else if (!string.IsNullOrEmpty(d) && d != "dns-direct" && d != "direct" && proxyTag == null)
                    {
                        proxyTag = StjNodeHelpers.AsString(sObj["tag"]);
                    }
                }
            }

            // Strict DNS: force final → proxy DNS server (all queries via VPN, leak-proof)
            // Default: force final → local DNS server (faster, but only routed processes use VPN DNS)
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

        // 2. Remove deprecated DNS rules ("outbound" field is FATAL in 1.13.3, geosite/geoip need .db)
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

        // 3. Remove "block" and "dns" outbound types (removed in sing-box 1.13)
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

        // 4. Convert route rules that reference removed outbounds + remove geosite/geoip
        var routeRules = StjNodeHelpers.SelectToken(config, "route.rules") as JsonArray;
        if (routeRules != null)
        {
            for (int i = routeRules.Count - 1; i >= 0; i--)
            {
                var rule = routeRules[i] as JsonObject;
                if (rule == null) continue;

                // Remove geosite/geoip rules (no databases)
                if (rule["geosite"] != null || rule["geoip"] != null)
                {
                    routeRules.RemoveAt(i);
                    continue;
                }

                // Convert rules pointing to removed outbounds
                var outbound = StjNodeHelpers.AsString(rule["outbound"]);
                if (outbound != null && removedTags.Contains(outbound))
                {
                    rule.Remove("outbound");
                    // "dns-out" → hijack-dns, "block" → reject
                    rule["action"] = StjNodeHelpers.AsString(rule["protocol"]) == "dns"
                        ? "hijack-dns"
                        : "reject";
                }
            }
        }

        // 5. Normalize inbounds: remove deprecated fields, fix TUN settings
        bool hadInboundSniff = false;
        var inbounds = config["inbounds"] as JsonArray;
        if (inbounds != null)
        {
            foreach (var inbound in inbounds)
            {
                var obj = inbound as JsonObject;
                if (obj == null) continue;

                // Remove deprecated sniff fields (moved to route actions in 1.12+)
                if (obj["sniff"] != null)
                    hadInboundSniff = true;
                obj.Remove("sniff");
                obj.Remove("sniff_override_destination");
                obj.Remove("sniff_timeout");
                obj.Remove("domain_strategy");

                // TUN-specific fixes
                if (StjNodeHelpers.AsString(obj["type"]) == "tun")
                {
                    // Convert legacy inet4_address/inet6_address → address array (removed in 1.12).
                    // Phase 6 — Wave 31b: wrap strings in JsonValue.Create() +
                    // cast to (JsonNode?) so the .Add picks the non-generic
                    // overload (IL3050).
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
                        // Phase 6 — Wave 31b: wrap string in JsonValue.Create() +
                        // cast to (JsonNode?) for AOT-clean Add (IL3050).
                        var mergedArray = new JsonArray();
                        foreach (var s in merged) mergedArray.Add((JsonNode?)JsonValue.Create(s));
                        obj["route_exclude_address"] = mergedArray;
                    }
                }
            }
        }

        // 6. If we stripped inbound-level sniff, add route-level sniff rule (1.12+ replacement).
        // Without sniffing, sing-box can't detect TLS SNI for domain-based routing.
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

        // 7. Ensure log output goes to our log file (so we can debug startup failures)
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

    // ─── Private: Cleanup helpers ────────────────────────────────────────────

    /// <summary>
    /// Removes any route rules that have process_name (our injected rules).
    /// This makes re-injection idempotent — safe to call multiple times.
    /// </summary>
    /// <summary>
    /// Removes every route rule carrying a <c>process_name</c> field and returns
    /// how many were removed. In custom mode VPNRouter OWNS per-app routing:
    /// <see cref="Inject"/> always starts from the user's pristine JSON, so any
    /// process_name rules present are the user's own and are intentionally
    /// replaced by our injected list. The returned count lets the caller warn
    /// (W1.4-a) so the override is never silent.
    /// </summary>
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
}
