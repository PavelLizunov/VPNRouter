using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

// The pilot is a preset of the existing generated-config engine, not another client.
internal static class BusinessDefaults
{
    internal static void Apply(AppSettings settings, string connection, List<CustomRule> rules)
    {
        var servers = SubscriptionFetcher.ParseBody(connection);
        var first = servers.FirstOrDefault(s => s.Protocol == "vless" && string.IsNullOrEmpty(s.DetourVia));
        if (first == null)
            throw new InvalidDataException("Business pilot requires a usable VLESS connection.");
        if (rules.Count == 0 || rules.Any(r => !r.Enabled || ConfigGenerator.BuildCustomRouteRule(r) == null))
            throw new InvalidDataException("Business routing preset is invalid.");

        settings.Vless = new VlessConfig { Servers = servers, ActiveServer = first.Name };
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
