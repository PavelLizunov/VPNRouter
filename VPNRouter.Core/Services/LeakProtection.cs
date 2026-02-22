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
    public static ValidationResult ValidateConfig(SingBoxConfig config)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        // 1. DNS strategy must be ipv4_only
        if (config.Dns.Strategy != "ipv4_only")
            errors.Add($"dns.strategy must be 'ipv4_only', got '{config.Dns.Strategy}'");

        // 2. strict_route must be false (dual stack protection)
        foreach (var inbound in config.Inbounds)
        {
            if (inbound.StrictRoute)
                errors.Add($"inbound '{inbound.Tag}': strict_route must be false to avoid dual stack errors");

            // 3. No IPv6 address in TUN
            if (inbound.Address == null || inbound.Address.Count == 0)
                errors.Add($"inbound '{inbound.Tag}': address is missing");
        }

        // 4. Every process in route rules must have a DNS rule (sing-box 1.12+ action format)
        var processesInRouteRules = config.Route.Rules
            .Where(r => r.ProcessName != null && r.ProcessName.Count > 0
                     && (r.Outbound == "proxy" || r.Action == "route"))
            .SelectMany(r => r.ProcessName!)
            .Distinct()
            .ToList();

        // DNS rules use action="route" + server (vpn-dns or local-dns depending on dns_mode)
        // Smart mode uses local-dns, vpn_only uses vpn-dns — both are valid leak protection
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

        // 4b. Full tunnel mode checks
        var isFullTunnel = config.Route.Final == "proxy";
        if (isFullTunnel)
        {
            // In full tunnel, DNS final should be vpn-dns
            if (config.Dns.Final != "vpn-dns")
                warnings.Add("Full tunnel mode: DNS final is not 'vpn-dns' — DNS may bypass VPN");
        }

        // 5. Proxy outbound must exist
        var hasProxy = config.Outbounds.Any(o => o.Tag == "proxy");
        if (!hasProxy)
            errors.Add("No 'proxy' outbound defined");

        // 6. Direct outbound must exist
        // Note: "block" outbound removed in sing-box 1.12+ — now use action: "reject" in route rules
        if (!config.Outbounds.Any(o => o.Tag == "direct"))
            errors.Add("No 'direct' outbound defined");

        // Check that DNS hijack rule exists (replaces legacy "dns" outbound)
        var hasDnsHijack = config.Route.Rules.Any(r => r.Action == "hijack-dns");
        if (!hasDnsHijack)
            warnings.Add("No 'hijack-dns' route rule — DNS traffic may not be handled correctly");

        // 7. Validate proxy outbound — single VLESS or urltest wrapper
        var proxyOutbound = config.Outbounds.FirstOrDefault(o => o.Tag == "proxy");
        if (proxyOutbound != null)
        {
            if (proxyOutbound.Type == "vless")
            {
                // Single server mode
                ValidateVlessOutbound(proxyOutbound, errors);
            }
            else if (proxyOutbound.Type == "urltest")
            {
                // Multi-server mode — validate urltest + each child VLESS
                if (proxyOutbound.Outbounds == null || proxyOutbound.Outbounds.Count < 2)
                    errors.Add("urltest outbound: must have at least 2 child outbounds");

                var outboundTags = config.Outbounds.Select(o => o.Tag).ToHashSet();
                foreach (var childTag in proxyOutbound.Outbounds ?? new())
                {
                    if (!outboundTags.Contains(childTag))
                    {
                        errors.Add($"urltest references non-existent outbound '{childTag}'");
                        continue;
                    }
                    var child = config.Outbounds.First(o => o.Tag == childTag);
                    ValidateVlessOutbound(child, errors);
                }
            }
        }

        return new ValidationResult { Errors = errors, Warnings = warnings };
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
    }
}
