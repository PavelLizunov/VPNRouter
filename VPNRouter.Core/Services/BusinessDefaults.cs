using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

// The pilot is a preset of the existing generated-config engine, not another client.
internal static class BusinessDefaults
{
    internal static void Apply(AppSettings settings, string connection, List<CustomRule> rules,
        BusinessPolicySnapshot? policy = null)
    {
        var servers = ParseConnection(connection);
        if (rules.Count == 0 || rules.Any(r => !r.Enabled || ConfigGenerator.BuildCustomRouteRule(r) == null))
            throw new InvalidDataException("Business routing preset is invalid.");

        rules = BusinessPolicyRouting.Compose(rules, policy);

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
        settings.App.CustomRulesPriority = "custom_first";
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
        if (policy?.Phone == null && !settings.Tun.RouteExcludeAddress.Contains(BusinessPhonePolicy.LegacySipCidr))
            settings.Tun.RouteExcludeAddress.Add(BusinessPhonePolicy.LegacySipCidr);
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
            throw new InvalidDataException("Business pilot requires a usable VLESS or DNSTT connection.");
        foreach (var server in servers) ValidateProtocolFields(server);
        return servers;
    }

    internal static void ValidateProtocolFields(VlessServerEntry server)
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
            "dnstt" => server.Dnstt != null,
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

    internal static VlessConfig CreateServerPool(List<VlessServerEntry> servers, VlessConfig? previous,
        bool requireSelectedDnstt = false)
    {
        var choices = GetServerChoices(servers);
        var first = choices.First();
        var savedName = previous?.ActiveServer;
        var previousServer = previous?.Servers.FirstOrDefault(s =>
            string.Equals(s.Name, savedName, StringComparison.OrdinalIgnoreCase));
        if (previousServer?.Dnstt != null)
        {
            var source = new SubscriptionEntry { Servers = servers };
            var selection = new SubscriptionChainSelection(source.Id, source.Url, previousServer, null);
            var pinned = selection.Match(source, servers);
            if (pinned != null) return new VlessConfig { Servers = servers, ActiveServer = pinned.Name };
            if (requireSelectedDnstt)
                throw new InvalidDataException("The selected Business DNSTT route is no longer available.");
        }
        var selected = choices.FirstOrDefault(s =>
            s.Dnstt == null && string.Equals(s.Server, previousServer?.Server, StringComparison.OrdinalIgnoreCase));
        var saved = !string.IsNullOrWhiteSpace(savedName)
            ? servers.FirstOrDefault(s => string.Equals(s.Name, savedName, StringComparison.OrdinalIgnoreCase))
            : null;
        selected ??= saved == null ? null : choices.FirstOrDefault(s =>
            s.Dnstt == null && string.Equals(s.Server, saved.Server, StringComparison.OrdinalIgnoreCase));
        // Prefer the retained physical host even if another node reuses its label.
        return new VlessConfig { Servers = servers, ActiveServer = (selected ?? first).Name };
    }

    // The shared engine groups TCP/UDP variants on one host. Offer node choices,
    // while retaining every bundled variant for its existing sibling logic.
    internal static List<VlessServerEntry> GetServerChoices(IEnumerable<VlessServerEntry> servers) =>
        servers.Where(s => s.Protocol == "vless" && string.IsNullOrEmpty(s.DetourVia))
            .DistinctBy(s => s.Server, StringComparer.OrdinalIgnoreCase)
            .Concat(servers.Where(s => s.Dnstt != null)).ToList();

    internal static void ApplyDns(SingBoxConfig config, IEnumerable<CustomRule> rules, bool customFirst = true)
    {
        var domains = new List<DnsRule>();
        var processes = new List<DnsRule>();
        var existing = config.Dns.Rules.Where(d => d.CustomRule != null).ToLookup(d => d.CustomRule!);
        var tags = (config.Route.RuleSet ?? []).Select(entry => entry.Tag).ToHashSet(StringComparer.Ordinal);
        foreach (var rule in rules.Where(r => r.Enabled))
        {
            var dns = existing[rule].FirstOrDefault();
            if (dns == null)
            {
                var route = ConfigGenerator.BuildCustomRouteRule(rule);
                if (route?.Outbound is not ("direct" or "proxy")) continue;
                var sites = route.RuleSet?.Where(t => t.StartsWith("user-geosite-", StringComparison.Ordinal) && tags.Contains(t)).ToList();
                if (route.Domain == null && route.DomainSuffix == null && route.DomainKeyword == null &&
                    route.ProcessName == null && sites is not { Count: > 0 }) continue;
                dns = new DnsRule
                {
                    CustomRule = rule, Domain = route.Domain, DomainSuffix = route.DomainSuffix,
                    DomainKeyword = route.DomainKeyword, ProcessName = route.ProcessName, RuleSet = sites,
                    Server = route.Outbound == "direct" ? "local-dns" : "vpn-dns"
                };
            }
            (dns.ProcessName == null ? domains : processes).Add(dns);
        }
        var baseRules = config.Dns.Rules.Where(d => d.CustomRule == null).ToList();
        var infrastructure = baseRules.Where(d => d.QueryType != null);
        var toggles = baseRules.Where(d => d.RuleSet?.Any(t => t is "vpnrouter-geosite-ru" or "vpnrouter-adblock") == true).ToList();
        foreach (var dns in toggles.Where(d => d.RuleSet!.Contains("vpnrouter-geosite-ru")))
            dns.Server = "local-dns";

        // Query guards stay first. Destination policies precede process DNS, in route priority order.
        var destinations = customFirst ? domains.Concat(toggles) : toggles.Concat(domains);
        config.Dns.Rules = infrastructure.Concat(destinations).Concat(processes)
            .Concat(baseRules.Except(infrastructure).Except(toggles)).ToList();
    }

    internal static void ApplyDomainTransports(SingBoxConfig config, bool hasUdpProxy,
        bool proxyCarriesUdpNatively, bool blockQuicOnTcpProxy)
    {
        var proxyIsDnstt = config.Outbounds.Any(o => o.Tag == "proxy" && o.Detour == "dnstt-transport");
        // Explicit domain and IP rules precede per-app/global transport guards.
        // Preserve the same transport decision for every explicit destination.
        for (var i = config.Route.Rules.Count - 1; i >= 0; i--)
        {
            var rule = config.Route.Rules[i];
            if (proxyIsDnstt && rule.Outbound == "proxy" && (rule.Network is null or "udp"))
            {
                var reject = rule.RejectUdp();
                if (rule.Network == "udp") config.Route.Rules[i] = reject;
                else config.Route.Rules.Insert(i, reject);
                continue;
            }
            if (rule.Outbound != "proxy" || rule.Network != null ||
                (rule.Domain == null && rule.DomainSuffix == null && rule.IpCidr == null)) continue;

            RouteRule ForDestination() => new()
            {
                Domain = rule.Domain?.ToList(), DomainSuffix = rule.DomainSuffix?.ToList(),
                IpCidr = rule.IpCidr?.ToList()
            };

            if (hasUdpProxy)
            {
                var udp = ForDestination();
                udp.Network = "udp";
                udp.Action = "route";
                udp.Outbound = "proxy-udp";
                config.Route.Rules.Insert(i, udp);
            }
            else if (!proxyCarriesUdpNatively && blockQuicOnTcpProxy)
            {
                var quic = ForDestination();
                quic.Protocol = "quic";
                quic.Action = "reject";
                var udp443 = ForDestination();
                udp443.Network = "udp";
                udp443.Port = [443];
                udp443.Action = "reject";
                config.Route.Rules.Insert(i, quic);
                config.Route.Rules.Insert(i, udp443);
            }
        }
    }
}
