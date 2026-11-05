using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

// The pilot is a preset of the existing generated-config engine, not another client.
internal static class BusinessDefaults
{
    internal static void Apply(AppSettings settings, string connection, List<CustomRule> rules)
    {
        var servers = ParseConnection(connection);
        if (rules.Count == 0 || rules.Any(r => !r.Enabled || ConfigGenerator.BuildCustomRouteRule(r) == null))
            throw new InvalidDataException("Business routing preset is invalid.");

        settings.Vless = CreateServerPool(servers, settings.Vless);
        settings.ActiveProfile = string.Empty;
        settings.CustomApps.Clear();
        settings.ExcludedApps.Clear();
        settings.App.ConfigMode = "generated";
        settings.App.Subscriptions.Clear();
        settings.App.SubscriptionUrl = string.Empty;
        settings.App.SubscriptionServers.Clear();
        settings.App.CustomConfig = string.Empty;
        settings.App.CustomConfigs.Clear();
        settings.App.RoutingMode = settings.App.RoutingMode == "full" ? "full" : "split";
        settings.App.RoutingAppsMode = "include";
        settings.App.RoutingAppsIncludeInitialized = true;
        settings.App.RoutingAppsExclude.Clear();
        settings.App.RoutingAppsInclude = rules
            .Where(r => r.Type == "process_name" && r.Action == "proxy")
            .SelectMany(r => r.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Process proxy rules belong to the existing per-app path, which owns
        // proxy-udp sibling selection and the TCP-only QUIC fallback guards.
        settings.App.CustomRules = rules.Where(r => r.Type != "process_name" || r.Action != "proxy").ToList();
        settings.App.BypassRussianTraffic = true;
        settings.App.CustomRulesPriority = "toggles_first";
        settings.App.StrictDns = false;
        settings.App.DnsLeakLockdown = false;
        settings.App.BlockAds = false;
        settings.App.ZapretEnabled = settings.App.AutostartZapret = false;
        settings.App.TgProxyEnabled = settings.App.AutostartTgProxy = false;
        settings.App.ForceIpv4Only = true;
        settings.Tun.Ipv6Enabled = false;
        settings.Tun.AutoRoute = true;
        settings.Tun.StrictRoute = false;
        settings.SingBox.ExecutablePath = AppPaths.SingBoxExePath;
        settings.App.LogFile = Path.Combine(AppPaths.LogsDir, "vpnrouter.log");
        // Confirmed alternate UIS SIP host; the customer must supply its RTP ranges.
        if (!settings.Tun.RouteExcludeAddress.Contains("195.211.120.35/32"))
            settings.Tun.RouteExcludeAddress.Add("195.211.120.35/32");
        settings.Monitoring.RestartOnFailure = true;
        settings.Update.AutoCheck = false;
    }

    internal static List<VlessServerEntry> ParseConnection(string connection)
    {
        if (connection.Length > 1024 * 1024) throw new InvalidDataException("Business response too large.");
        var servers = SubscriptionFetcher.ParseBody(connection);
        if (servers.Count > 500 || servers.Any(s => string.IsNullOrWhiteSpace(s.Name)) ||
            servers.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != servers.Count)
            throw new InvalidDataException("Business server names must be nonempty and unique.");
        if (GetServerChoices(servers).Count == 0)
            throw new InvalidDataException("Business pilot requires a usable VLESS connection.");
        foreach (var server in servers) ValidateProtocolFields(server);
        return servers;
    }

    private static void ValidateProtocolFields(VlessServerEntry server)
    {
        var valid = !string.IsNullOrWhiteSpace(server.Server) && server.Port is > 0 and <= 65535;
        valid &= (server.Protocol ?? "vless").ToLowerInvariant() switch
        {
            "vless" => !string.IsNullOrWhiteSpace(server.Uuid) &&
                (string.Equals(server.Security, "tls", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(server.Security, "none", StringComparison.OrdinalIgnoreCase) ||
                 (string.Equals(server.Security, "reality", StringComparison.OrdinalIgnoreCase) &&
                  VlessUriParser.IsValidRealityPublicKey(server.Reality?.PublicKey) &&
                  VlessUriParser.IsValidRealityShortId(server.Reality?.ShortId))),
            "hysteria2" => !string.IsNullOrWhiteSpace(server.Password),
            "tuic" => !string.IsNullOrWhiteSpace(server.Uuid),
            "shadowsocks" => !string.IsNullOrWhiteSpace(server.Method) && !string.IsNullOrWhiteSpace(server.Password) &&
                (!server.Method.StartsWith("2022-blake3-", StringComparison.OrdinalIgnoreCase) ||
                 LeakProtection.IsValidSs2022Key(server.Method, server.Password)),
            "amneziawg" => server.Awg != null &&
                IsValid32ByteKey(server.Awg.PrivateKey) &&
                IsValid32ByteKey(server.Awg.PeerPublicKey) &&
                server.Awg.Address.Count > 0 && server.Awg.Address.All(a => !string.IsNullOrWhiteSpace(a)) &&
                (string.IsNullOrEmpty(server.Awg.PresharedKey) || IsValid32ByteKey(server.Awg.PresharedKey)),
            _ => true
        };
        if (!valid) throw new InvalidDataException("Business endpoint lacks valid required protocol fields.");
    }

    private static bool IsValid32ByteKey(string value) =>
        !string.IsNullOrEmpty(value) && VlessUriParser.TryDecodeBase64Url(value, out var bytes) && bytes.Length == 32;

    internal static VlessConfig CreateServerPool(List<VlessServerEntry> servers, VlessConfig? previous)
    {
        var choices = GetServerChoices(servers);
        var first = choices.First();
        var savedName = previous?.ActiveServer;
        var previousHost = previous?.Servers.FirstOrDefault(s =>
            string.Equals(s.Name, savedName, StringComparison.OrdinalIgnoreCase))?.Server;
        var selected = choices.FirstOrDefault(s =>
            string.Equals(s.Server, previousHost, StringComparison.OrdinalIgnoreCase));
        var saved = !string.IsNullOrWhiteSpace(savedName)
            ? servers.FirstOrDefault(s => string.Equals(s.Name, savedName, StringComparison.OrdinalIgnoreCase))
            : null;
        selected ??= saved == null ? null : choices.FirstOrDefault(s =>
            string.Equals(s.Server, saved.Server, StringComparison.OrdinalIgnoreCase));
        // Prefer the retained physical host even if another node reuses its label.
        return new VlessConfig { Servers = servers, ActiveServer = (selected ?? first).Name };
    }

    // The shared engine groups TCP/UDP variants on one host. Offer node choices,
    // while retaining every bundled variant for its existing sibling logic.
    internal static List<VlessServerEntry> GetServerChoices(IEnumerable<VlessServerEntry> servers) =>
        servers.Where(s => s.Protocol == "vless" && string.IsNullOrEmpty(s.DetourVia))
            .DistinctBy(s => s.Server, StringComparer.OrdinalIgnoreCase).ToList();

    internal static void ApplyDns(SingBoxConfig config, IEnumerable<CustomRule> rules)
    {
        // Explicit DNS destinations take precedence over app matching (Windows DNS may
        // belong to svchost). Direct sites must never inherit a proxy app's resolver.
        var direct = new List<DnsRule>();
        var proxy = new List<DnsRule>();
        foreach (var rule in rules.Where(r => r.Enabled && r.Action is "direct" or "proxy"))
        {
            var route = ConfigGenerator.BuildCustomRouteRule(rule);
            if (route == null || (route.Domain == null && route.DomainSuffix == null && route.ProcessName == null))
                continue;
            (rule.Action == "direct" ? direct : proxy).Add(new DnsRule
            {
                Domain = route.Domain, DomainSuffix = route.DomainSuffix, ProcessName = route.ProcessName,
                Server = rule.Action == "direct" ? "local-dns" : "vpn-dns"
            });
        }
        // RU resources use both direct routing and a direct resolver in Business.
        var geo = config.Dns.Rules.Where(r => r.RuleSet?.Contains("vpnrouter-geosite-ru") == true).ToList();
        foreach (var rule in geo)
            rule.Server = "local-dns";
        var infrastructure = config.Dns.Rules.Where(r => r.QueryType != null).ToList();
        var rest = config.Dns.Rules.Except(geo).Except(infrastructure).ToList();
        config.Dns.Rules = infrastructure.Concat(direct).Concat(geo).Concat(proxy).Concat(rest).ToList();
    }
}
