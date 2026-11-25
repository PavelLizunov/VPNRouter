using System.IO;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static partial class ConfigGenerator
{
    private static string SlipstreamProcessName => Path.GetFileName(AppPaths.SlipstreamExePath);

    private static SingBoxRoute BuildRoute(Profile profile, List<string> processes, string tunAddress,
        string routingMode = "split", bool hasUdpProxy = false, bool isExcludeMode = false,
        bool blockQuicOnTcpProxy = true, bool isDnsTunnel = false,
        List<string>? dnsTunnelResolverIps = null, bool proxyIsUdpNative = false, bool tcpOnlyProxy = false)
    {
        var isFullTunnel = (routingMode ?? "split").Equals("full", StringComparison.OrdinalIgnoreCase);

        var rules = new List<RouteRule>
        {
            new() { Action = "sniff", Timeout = "300ms" },
        };

        // DNS-tunnel self-exclusion must precede hijack-dns and the proxy final, or slipstream's own resolver traffic loops back into the tunnel.
        if (isDnsTunnel)
            AddDnsTunnelSelfExclusionRules(rules, dnsTunnelResolverIps);

        rules.Add(new RouteRule { Protocol = "dns", Action = "hijack-dns" });

        // The virtual peer is not a LAN host. Direct ICMP forwarding to it
        // re-enters this TUN and recursively allocates raw sockets in sing-box.
        // DNS must still reach hijack-dns before this infrastructure guard.
        rules.Add(new RouteRule { IpCidr = [tunAddress], Network = "icmp", Action = "reject", IsInfrastructure = true });

        rules.Add(new RouteRule
        {
            IpIsPrivate = true,
            Action      = "route",
            Outbound    = "direct"
        });

        bool excludeApps = !isFullTunnel && isExcludeMode;
        if (excludeApps)
            AddPerAppRoutingRules(rules, processes, isFullTunnel, isExcludeMode, hasUdpProxy);

        if (blockQuicOnTcpProxy && !hasUdpProxy && !proxyIsUdpNative)
            AddQuicRejectRules(rules, processes, isFullTunnel, isExcludeMode);

        if (tcpOnlyProxy && !isFullTunnel && !isExcludeMode && processes.Count > 0)
            rules.Add(new RouteRule { Network = "udp", ProcessName = processes.ToList(), Action = "reject" });

        if (!excludeApps)
            AddPerAppRoutingRules(rules, processes, isFullTunnel, isExcludeMode, hasUdpProxy);

        if (tcpOnlyProxy && (isFullTunnel || isExcludeMode))
            rules.Add(new RouteRule { Network = "udp", Action = "reject" });

        string finalOutbound;
        if (isFullTunnel)
            finalOutbound = "proxy";
        else if (isExcludeMode)
            finalOutbound = "proxy";
        else
            finalOutbound = "direct";

        return new SingBoxRoute
        {
            Rules                   = rules,
            Final                   = finalOutbound,
            AutoDetectInterface     = true,
            DefaultDomainResolver   = "local-dns"
        };
    }

    private static void AddDnsTunnelSelfExclusionRules(List<RouteRule> rules, List<string>? dnsTunnelResolverIps)
    {
        if (dnsTunnelResolverIps is { Count: > 0 })
            rules.Add(new RouteRule
            {
                IpCidr           = dnsTunnelResolverIps,
                Action           = "route",
                Outbound         = "direct",
                IsInfrastructure = true,
            });
        rules.Add(new RouteRule
        {
            ProcessName      = new List<string> { SlipstreamProcessName },
            Action           = "route",
            Outbound         = "direct",
            IsInfrastructure = true,
        });
    }

    private static void AddQuicRejectRules(
        List<RouteRule> rules, List<string> processes, bool isFullTunnel, bool isExcludeMode)
    {
        if (isFullTunnel || isExcludeMode)
        {
            rules.Add(new RouteRule { Network = "udp", Port = new List<int> { 443 }, Action = "reject" });
            rules.Add(new RouteRule { Protocol = "quic", Action = "reject" });
        }
        else if (processes.Count > 0)
        {
            rules.Add(new RouteRule
            {
                ProcessName = processes.ToList(),
                Network     = "udp",
                Port        = new List<int> { 443 },
                Action      = "reject"
            });
            rules.Add(new RouteRule
            {
                ProcessName = processes.ToList(),
                Protocol    = "quic",
                Action      = "reject"
            });
        }
    }

    private static void AddPerAppRoutingRules(
        List<RouteRule> rules, List<string> processes, bool isFullTunnel, bool isExcludeMode, bool hasUdpProxy)
    {
        if (!isFullTunnel && processes.Count > 0)
        {
            var perAppOutbound = isExcludeMode ? "direct" : "proxy";
            if (hasUdpProxy && !isExcludeMode)
            {
                rules.Add(new RouteRule
                {
                    ProcessName = processes.ToList(),
                    Network     = "udp",
                    Action      = "route",
                    Outbound    = "proxy-udp"
                });
                rules.Add(new RouteRule
                {
                    ProcessName = processes.ToList(),
                    Network     = "tcp",
                    Action      = "route",
                    Outbound    = "proxy"
                });
            }
            else
            {
                rules.Add(new RouteRule
                {
                    ProcessName = processes.ToList(),
                    Action      = "route",
                    Outbound    = perAppOutbound
                });
            }
        }
        if ((isFullTunnel || isExcludeMode) && hasUdpProxy)
        {
            rules.Add(new RouteRule
            {
                Network  = "udp",
                Action   = "route",
                Outbound = "proxy-udp"
            });
        }
    }
}
