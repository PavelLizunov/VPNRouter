using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static partial class ConfigGenerator
{
    internal const string BusinessPortalOutbound = "business-portal";
    internal const string BusinessPortalDns = "business-portal-dns";

    internal static void ApplyBusinessPortal(SingBoxConfig config, AppSettings settings)
    {
        // Resolve from the Business choices rather than the general pool, whose
        // health and transport selection may substitute another exit.
        var servers = settings.Vless.GetEffectiveServers();
        var choices = BusinessDefaults.GetServerChoices(servers);
        var selected = string.IsNullOrEmpty(settings.Vless.ActiveServer)
            ? choices.FirstOrDefault()
            : choices.SingleOrDefault(s => string.Equals(s.Name, settings.Vless.ActiveServer,
                StringComparison.OrdinalIgnoreCase));
        if (selected == null || servers.Count(s => string.Equals(s.Name, selected.Name,
                StringComparison.OrdinalIgnoreCase)) != 1 || !IsBusinessPortalTransportAvailable(selected))
            throw new InvalidOperationException("Business portal requires the selected direct VLESS choice to be available.");
        BusinessDefaults.ValidateProtocolFields(selected);

        config.Outbounds.Add(BuildVlessOutbound(selected, BusinessPortalOutbound));
        // HTTPS keeps DNS on TCP through the same exit. Resolver/endpoint bootstrap
        // still uses local-dns, so resolving the proxy host cannot recurse.
        var dns = BuildVpnDnsServer(settings, proxyIsUdpNative: false);
        dns.Tag = BusinessPortalDns;
        dns.Detour = BusinessPortalOutbound;
        config.Dns.Servers.Add(dns);
        // Associate DNS replies with IP-addressed traffic for the portal UDP guard.
        config.Dns.ReverseMapping = true;

        static bool IsPortalOnly(List<string>? domains) =>
            domains is [BusinessPolicyDomains.PortalDomain];
        config.Route.Rules.RemoveAll(r => IsPortalOnly(r.DomainSuffix));
        config.Dns.Rules.RemoveAll(r => IsPortalOnly(r.DomainSuffix));

        var routeAt = config.Route.Rules.FindIndex(r =>
            r.Action is not ("sniff" or "hijack-dns") && !r.IsInfrastructure);
        if (routeAt < 0) routeAt = config.Route.Rules.Count;
        config.Route.Rules.InsertRange(routeAt,
        [
            new RouteRule
            {
                DomainSuffix = [BusinessPolicyDomains.PortalDomain], Network = "udp", Action = "reject"
            },
            new RouteRule
            {
                DomainSuffix = [BusinessPolicyDomains.PortalDomain], Network = "tcp",
                Action = "route", Outbound = BusinessPortalOutbound
            }
        ]);
        var dnsAt = config.Dns.Rules.FindIndex(r => r.QueryType == null);
        if (dnsAt < 0) dnsAt = config.Dns.Rules.Count;
        config.Dns.Rules.Insert(dnsAt, new DnsRule
        {
            DomainSuffix = [BusinessPolicyDomains.PortalDomain], Action = "route", Server = BusinessPortalDns
        });
    }

    internal static bool IsBusinessPortalTransportAvailable(VlessServerEntry server) =>
        !server.IsDnsTunnel && FilterUnavailableServers([server]).Count == 1;
}
