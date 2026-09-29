using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public class ValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public List<string> Errors { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
}

public static class LeakProtection
{
    public static ValidationResult ValidateAppSettings(AppSettings settings)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (settings == null)
        {
            errors.Add("AppSettings is null");
            return new ValidationResult { Errors = errors, Warnings = warnings };
        }

        var configMode = (settings.App?.ConfigMode ?? "generated").Trim();
        var isSubscribe = configMode.Equals("subscribe", StringComparison.OrdinalIgnoreCase);
        var isGenerated = configMode.Equals("generated", StringComparison.OrdinalIgnoreCase);

        var subs = settings.App?.Subscriptions ?? new List<SubscriptionEntry>();
        var enabledSubs = subs.Where(s => s != null && s.Enabled).ToList();
        var enabledSubsWithServers = enabledSubs
            .Where(s => s.Servers != null && s.Servers.Count > 0)
            .ToList();
        var manualServerCount = settings.Vless?.Servers?.Count ?? 0;
        var hasLegacyVlessServer = !string.IsNullOrWhiteSpace(settings.Vless?.Server);

        if (isSubscribe)
        {
            if (subs.Count == 0)
            {
                errors.Add(
                    "ConfigMode=subscribe but no subscriptions are registered. " +
                    "Either register a subscription URL (Subscribe tab) or switch ConfigMode " +
                    "back to 'generated'/'custom' before connecting.");
            }
            else if (enabledSubs.Count == 0)
            {
                errors.Add(
                    "ConfigMode=subscribe but every subscription is disabled. " +
                    "Enable at least one subscription before connecting.");
            }
            _ = enabledSubsWithServers;
            _ = manualServerCount;
            _ = hasLegacyVlessServer;
        }
        else if (isGenerated)
        {
            var hasActiveServer = !string.IsNullOrWhiteSpace(settings.Vless?.ActiveServer)
                || !string.IsNullOrWhiteSpace(settings.App?.ActiveSubscriptionServer);
            if (manualServerCount == 0 && !hasLegacyVlessServer
                && enabledSubsWithServers.Count == 0 && !hasActiveServer)
            {
                errors.Add(
                    "ConfigMode=generated but no VLESS server is configured. " +
                    "Add a server in the Servers tab or switch to subscribe mode.");
            }
        }

        return new ValidationResult { Errors = errors, Warnings = warnings };
    }

    public static ValidationResult ValidateConfig(SingBoxConfig config, AppSettings? settings = null)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (config.Outbounds == null)
        {
            if (string.Equals(settings?.App?.ConfigMode, "custom", StringComparison.OrdinalIgnoreCase))
                ValidateCustomModeProxyOutbound(config, errors);
            else
                errors.Add("[LeakProtection] config has no outbounds; traffic routing cannot be validated.");
            return new ValidationResult { Errors = errors, Warnings = warnings };
        }

        if (settings != null)
            ValidateOutboundServersScopeAware(config, settings, errors, warnings);

        if (config.Dns.Strategy != "ipv4_only")
            errors.Add($"dns.strategy must be 'ipv4_only', got '{config.Dns.Strategy}'");

        foreach (var inbound in config.Inbounds)
        {
            if (inbound.StrictRoute)
                errors.Add($"inbound '{inbound.Tag}': strict_route must be false to avoid dual stack errors");

            if (inbound.Address == null || inbound.Address.Count == 0)
                errors.Add($"inbound '{inbound.Tag}': address is missing");
        }

        var processesInRouteRules = config.Route.Rules
            .Where(r => r.ProcessName != null && r.ProcessName.Count > 0
                     && (r.Outbound == "proxy" || r.Outbound == "proxy-udp"))
            .SelectMany(r => r.ProcessName!)
            .Distinct()
            .ToList();

        var processesInDnsRules = config.Dns.Rules
            .Where(r => (r.Server == "vpn-dns" || r.Server == "local-dns") && r.Action == "route")
            .Where(r => r.ProcessName != null)
            .SelectMany(r => r.ProcessName!)
            .Distinct()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var proc in processesInRouteRules)
        {
            if (!processesInDnsRules.Contains(proc))
                warnings.Add($"Process '{proc}' is routed through proxy but has no DNS rule — DNS may leak");
        }

        foreach (var proc in config.Dns.Rules
                     .Where(r => r.Server == "local-dns" && r.Action == "route" && r.ProcessName != null)
                     .SelectMany(r => r.ProcessName!)
                     .Where(p => processesInRouteRules.Contains(p))
                     .Distinct())
            warnings.Add($"Process '{proc}' resolves DNS via local DoH (smart mode) — its DNS path " +
                         "leaves the tunnel (encrypted, but the resolver sees your real IP)");

        foreach (var o in config.Outbounds)
        {
            if ((o.Type ?? string.Empty).Equals("vless", StringComparison.OrdinalIgnoreCase)
                && o.Tls?.Reality?.Enabled == true
                && string.IsNullOrEmpty(o.Flow))
                warnings.Add($"VLESS+Reality outbound '{o.Tag}' has no flow — if the server expects " +
                             "xtls-rprx-vision the handshake will fail; verify the share-link includes &flow=");
        }

        var isFullTunnel = config.Route.Final == "proxy";
        if (isFullTunnel)
        {
            if (config.Dns.Final != "vpn-dns")
                warnings.Add("Full tunnel mode: DNS final is not 'vpn-dns' — DNS may bypass VPN");
        }

        if (settings != null
            && !string.Equals(settings.App.ConfigMode, "custom", StringComparison.OrdinalIgnoreCase))
        {
            var routingMode = (settings.App.RoutingMode ?? "split").Trim().ToLowerInvariant();
            var isFull = routingMode == "full";
            var isExclude = string.Equals(settings.App.RoutingAppsMode, "exclude",
                StringComparison.OrdinalIgnoreCase);
            var expectedFinal = (isFull || isExclude) ? "proxy" : "direct";
            if (config.Route.Final != expectedFinal)
                warnings.Add(
                    $"route.final is '{config.Route.Final}' but mode " +
                    $"({routingMode}/{(isExclude ? "exclude" : "include")}) expects " +
                    $"'{expectedFinal}' — possible routing inversion (traffic/DNS may leak)");
        }

        var hasProxy = config.Outbounds.Any(o => o.Tag == "proxy")
            || (config.Endpoints?.Any(e => e.Tag == "proxy") ?? false);
        if (!hasProxy)
            errors.Add("No 'proxy' outbound defined");

        if (!config.Outbounds.Any(o => o.Tag == "direct"))
            errors.Add("No 'direct' outbound defined");

        var hasDnsHijack = config.Route.Rules.Any(r => r.Action == "hijack-dns");
        if (!hasDnsHijack)
            warnings.Add("No 'hijack-dns' route rule — DNS traffic may not be handled correctly");

        foreach (var proxyTag in new[] { "proxy", "proxy-udp" })
        {
            var proxyOutbound = config.Outbounds.FirstOrDefault(o => o.Tag == proxyTag);
            if (proxyOutbound == null) continue;

            ValidateProxyOutbound(proxyOutbound, config, errors, proxyTag);
        }

        var proxyEndpoint = config.Endpoints?.FirstOrDefault(e => e.Tag == "proxy");
        if (proxyEndpoint != null)
            ValidateProxyEndpoint(proxyEndpoint, errors);

        return new ValidationResult { Errors = errors, Warnings = warnings };
    }

    private static void ValidateProxyEndpoint(SingBoxEndpoint ep, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(ep.PrivateKey))
            errors.Add("AWG 'proxy' endpoint: private_key is empty");
        if (ep.Address == null || ep.Address.Count == 0 || ep.Address.All(string.IsNullOrWhiteSpace))
            errors.Add("AWG 'proxy' endpoint: no local tunnel address");
        if (ep.Peers == null || ep.Peers.Count == 0)
        {
            errors.Add("AWG 'proxy' endpoint: no peers (nothing to route through)");
            return;
        }
        for (var i = 0; i < ep.Peers.Count; i++)
        {
            var p = ep.Peers[i];
            if (string.IsNullOrWhiteSpace(p.PublicKey))
                errors.Add($"AWG 'proxy' endpoint peer[{i}]: public_key is empty");
            if (string.IsNullOrWhiteSpace(p.Address))
                errors.Add($"AWG 'proxy' endpoint peer[{i}]: endpoint address is empty (can't dial the server)");
            if (p.Port <= 0 || p.Port > 65535)
                errors.Add($"AWG 'proxy' endpoint peer[{i}]: invalid port {p.Port}");
        }
    }

    private static void ValidateProxyOutbound(
        SingBoxOutbound outbound,
        SingBoxConfig config,
        List<string> errors,
        string proxyTag)
    {
        if (outbound.Type == "urltest")
        {
            if (outbound.Outbounds == null || outbound.Outbounds.Count < 2)
                errors.Add($"urltest outbound '{proxyTag}': must have at least 2 child outbounds");

            var outboundTags = config.Outbounds.Select(o => o.Tag).ToHashSet();
            foreach (var childTag in outbound.Outbounds ?? new())
            {
                if (!outboundTags.Contains(childTag))
                {
                    errors.Add($"urltest '{proxyTag}' references non-existent outbound '{childTag}'");
                    continue;
                }
                var child = config.Outbounds.First(o => o.Tag == childTag);
                ValidateConcreteOutbound(child, errors);
            }
            return;
        }

        ValidateConcreteOutbound(outbound, errors);
    }

    private static void ValidateConcreteOutbound(SingBoxOutbound o, List<string> errors)
    {
        var type = (o.Type ?? string.Empty).ToLowerInvariant();
        switch (type)
        {
            case "vless":
                ValidateVlessOutbound(o, errors);
                break;
            case "hysteria2":
                ValidateHysteria2Outbound(o, errors);
                break;
            case "tuic":
                ValidateTuicOutbound(o, errors);
                break;
            case "shadowsocks":
                ValidateShadowsocksOutbound(o, errors);
                break;
            default:
                if (string.IsNullOrWhiteSpace(o.Server))
                    errors.Add($"{o.Type} outbound '{o.Tag}': server is empty");
                if (o.ServerPort is null or <= 0)
                    errors.Add($"{o.Type} outbound '{o.Tag}': server_port is invalid");
                break;
        }
    }

    private static void ValidateVlessOutbound(SingBoxOutbound vless, List<string> errors)
    {
        var label = $"VLESS outbound '{vless.Tag}'";
        if (string.IsNullOrWhiteSpace(vless.Server))
            errors.Add($"{label}: server is empty");
        if (string.IsNullOrWhiteSpace(vless.Uuid))
            errors.Add($"{label}: uuid is empty");
        if (vless.ServerPort is null or <= 0)
            errors.Add($"{label}: server_port is invalid");
        if (vless.Tls?.Reality?.Enabled == true
            && !VlessUriParser.IsValidRealityPublicKey(vless.Tls.Reality.PublicKey))
            errors.Add($"{label}: reality public_key is missing or not a 32-byte base64url key");
    }

    private static void ValidateHysteria2Outbound(SingBoxOutbound hy2, List<string> errors)
    {
        var label = $"Hysteria2 outbound '{hy2.Tag}'";
        if (string.IsNullOrWhiteSpace(hy2.Server))
            errors.Add($"{label}: server is empty");
        if (string.IsNullOrWhiteSpace(hy2.Password))
            errors.Add($"{label}: password is empty");
        if (hy2.ServerPort is null or <= 0)
            errors.Add($"{label}: server_port is invalid");
    }

    private static void ValidateTuicOutbound(SingBoxOutbound tuic, List<string> errors)
    {
        var label = $"TUIC outbound '{tuic.Tag}'";
        if (string.IsNullOrWhiteSpace(tuic.Server))
            errors.Add($"{label}: server is empty");
        if (string.IsNullOrWhiteSpace(tuic.Uuid))
            errors.Add($"{label}: uuid is empty");
        if (tuic.ServerPort is null or <= 0)
            errors.Add($"{label}: server_port is invalid");
    }

    private static void ValidateShadowsocksOutbound(SingBoxOutbound ss, List<string> errors)
    {
        var label = $"Shadowsocks outbound '{ss.Tag}'";
        if (string.IsNullOrWhiteSpace(ss.Server))
            errors.Add($"{label}: server is empty");
        if (string.IsNullOrWhiteSpace(ss.Method))
            errors.Add($"{label}: method (cipher) is empty");
        if (string.IsNullOrWhiteSpace(ss.Password))
            errors.Add($"{label}: password is empty");
        else if (!string.IsNullOrWhiteSpace(ss.Method)
                 && ss.Method!.StartsWith("2022-blake3-", StringComparison.OrdinalIgnoreCase)
                 && !IsValidSs2022Key(ss.Method, ss.Password))
            errors.Add($"{label}: SS2022 key for '{ss.Method}' is not valid base64 of the required length");
        if (ss.ServerPort is null or <= 0)
            errors.Add($"{label}: server_port is invalid");
    }

    internal static bool IsValidSs2022Key(string method, string? password)
    {
        if (string.IsNullOrEmpty(password)) return false;
        var need = method.Contains("aes-128", StringComparison.OrdinalIgnoreCase) ? 16 : 32;
        var parts = password!.Split(':');
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) return false;
            if (!VlessUriParser.TryDecodeBase64Url(part, out var raw) || raw.Length != need)
                return false;
        }
        return true;
    }

    private static void ValidateOutboundServersScopeAware(
        SingBoxConfig config, AppSettings settings,
        List<string> errors, List<string> warnings)
    {
        if (config == null)
            return;

        var configMode = (settings.App?.ConfigMode ?? "generated").Trim();
        var isCustom = configMode.Equals("custom", StringComparison.OrdinalIgnoreCase);

        if (isCustom)
        {
            ValidateCustomModeProxyOutbound(config, errors);
            return;
        }

        var allowed = BuildScopedAllowedServers(settings, out var hasEnabledSubsWithServers);

        if (config.Outbounds != null)
        {
            foreach (var ob in config.Outbounds)
            {
                if (!IsProxyLikeOutbound(ob))
                    continue;

                var server = ob.Server?.Trim();
                if (string.IsNullOrEmpty(server))
                    continue;

                if (IsLoopbackServer(server))
                    continue;

                var port = ob.ServerPort ?? 0;
                var uuid = ob.Uuid?.Trim() ?? string.Empty;

                if (!IsAllowed(allowed, server, port, uuid))
                {
                    if (hasEnabledSubsWithServers)
                    {
                        errors.Add(
                            $"[LeakProtection] Outbound '{ob.Tag}' points to " +
                            $"{server}:{port} which is not in the active subscription " +
                            $"scope (subscription={true}). Possible legacy vless.servers " +
                            $"leak — placeholder entries are shadowing live subscription " +
                            $"servers. Review config.yaml app.subscriptions[*].servers " +
                            $"and remove stale vless.servers entries.");
                    }
                    else
                    {
                        warnings.Add(
                            $"[LeakProtection] Outbound '{ob.Tag}' points to " +
                            $"{server}:{port} which is not in your VLESS server list. " +
                            $"Possible leak from stale configuration or placeholder.");
                    }
                }
            }
        }

        if (config.Endpoints != null)
        {
            foreach (var ep in config.Endpoints)
            {
                if (ep?.Peers == null)
                    continue;

                foreach (var p in ep.Peers)
                {
                    if (p == null)
                        continue;

                    var server = p.Address?.Trim();
                    if (string.IsNullOrEmpty(server))
                        continue;

                    if (IsLoopbackServer(server))
                        continue;

                    var port = p.Port;

                    if (!IsAllowed(allowed, server, port, string.Empty))
                    {
                        if (hasEnabledSubsWithServers)
                        {
                            errors.Add(
                                $"[LeakProtection] AWG endpoint '{ep.Tag}' peer points to " +
                                $"{server}:{port} which is not in the active subscription " +
                                $"scope (subscription={true}). Possible legacy vless.servers " +
                                $"leak — placeholder entries are shadowing live subscription " +
                                $"servers. Review config.yaml app.subscriptions[*].servers " +
                                $"and remove stale vless.servers entries.");
                        }
                        else
                        {
                            warnings.Add(
                                $"[LeakProtection] AWG endpoint '{ep.Tag}' peer points to " +
                                $"{server}:{port} which is not in your VLESS server list. " +
                                $"Possible leak from stale configuration or placeholder.");
                        }
                    }
                }
            }
        }
    }

    private static void ValidateCustomModeProxyOutbound(
        SingBoxConfig config, List<string> errors)
    {
        var proxy = config.Outbounds?.FirstOrDefault(o =>
            string.Equals(o.Tag, "proxy", StringComparison.OrdinalIgnoreCase));

        if (proxy == null)
        {
            errors.Add(
                "[LeakProtection] config_mode=custom but no 'proxy' outbound " +
                "exists in the pasted JSON. Traffic would route to 'direct' " +
                "silently. Add a proxy outbound or switch ConfigMode.");
            return;
        }

        var type = (proxy.Type ?? string.Empty).ToLowerInvariant();
        if (type == "selector" || type == "urltest")
            return;

        if (string.IsNullOrWhiteSpace(proxy.Server))
            errors.Add(
                "[LeakProtection] config_mode=custom: proxy outbound has an " +
                "empty 'server' field. Traffic would fail-to-direct silently.");
        if ((proxy.ServerPort ?? 0) <= 0)
            errors.Add(
                "[LeakProtection] config_mode=custom: proxy outbound has an " +
                "invalid 'server_port' (must be > 0).");
    }

    private static bool IsProxyLikeOutbound(SingBoxOutbound ob)
    {
        var type = (ob.Type ?? string.Empty).ToLowerInvariant();
        if (type == "direct" || type == "block" || type == "dns"
            || type == "selector" || type == "urltest")
            return false;

        if (string.Equals(ob.Tag, "dns-direct", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static bool IsLoopbackServer(string? server)
    {
        if (string.IsNullOrWhiteSpace(server))
            return false;

        var s = server.Trim();
        if (s.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        return System.Net.IPAddress.TryParse(s, out var ip)
            && System.Net.IPAddress.IsLoopback(ip);
    }

    private static bool IsAllowed(
        List<VlessServerEntry> allowed, string server, int port, string uuid)
    {
        if (allowed.Count == 0)
            return false;

        foreach (var entry in allowed)
        {
            var entryServer = entry?.Server?.Trim();
            if (string.IsNullOrEmpty(entryServer))
                continue;

            if (!string.Equals(entryServer, server, StringComparison.OrdinalIgnoreCase))
                continue;

            if (entry!.Port != port && port > 0 && entry.Port > 0)
                continue;

            var entryUuid = entry.Uuid?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(entryUuid)
                && !string.IsNullOrEmpty(uuid)
                && !string.Equals(entryUuid, uuid, StringComparison.OrdinalIgnoreCase))
                continue;

            return true;
        }

        return false;
    }

    internal static List<VlessServerEntry> BuildScopedAllowedServers(
        AppSettings settings, out bool hasEnabledSubsWithServers)
    {
        var subscriptionServers = new List<VlessServerEntry>();

        var subs = settings.App?.Subscriptions;
        if (subs != null)
        {
            foreach (var sub in subs)
            {
                if (sub == null || !sub.Enabled) continue;
                if (sub.Servers == null) continue;
                foreach (var s in sub.Servers)
                {
                    if (s != null && !string.IsNullOrWhiteSpace(s.Server))
                        subscriptionServers.Add(s);
                }
            }
        }

        var cached = settings.App?.SubscriptionServers;
        if (cached != null)
        {
            foreach (var s in cached)
            {
                if (s != null && !string.IsNullOrWhiteSpace(s.Server))
                    subscriptionServers.Add(s);
            }
        }

        hasEnabledSubsWithServers = subscriptionServers.Count > 0;

        var configMode = (settings.App?.ConfigMode ?? "generated").Trim();
        var isGenerated = configMode.Equals("generated", StringComparison.OrdinalIgnoreCase);

        if (hasEnabledSubsWithServers && !isGenerated)
            return subscriptionServers;

        if (hasEnabledSubsWithServers && isGenerated)
        {
            var union = new List<VlessServerEntry>(subscriptionServers);
            var manualForUnion = settings.Vless?.Servers;
            if (manualForUnion != null)
            {
                foreach (var s in manualForUnion)
                {
                    if (s == null || string.IsNullOrWhiteSpace(s.Server)) continue;
                    if (VlessServersResolver.IsPlaceholderEntry(s)) continue;
                    union.Add(s);
                }
            }
            return union;
        }

        var legacy = new List<VlessServerEntry>();

        var manual = settings.Vless?.Servers;
        if (manual != null)
        {
            foreach (var s in manual)
            {
                if (s != null && !string.IsNullOrWhiteSpace(s.Server))
                    legacy.Add(s);
            }
        }

        var legacyServer = settings.Vless?.Server;
        if (!string.IsNullOrWhiteSpace(legacyServer))
        {
            legacy.Add(new VlessServerEntry
            {
                Server = legacyServer!.Trim(),
                Port = settings.Vless?.Port ?? 0,
                Uuid = settings.Vless?.Uuid ?? string.Empty,
            });
        }

        return legacy;
    }
}
