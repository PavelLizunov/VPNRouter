using System.IO;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static partial class ConfigGenerator
{
    private static string SlipstreamProcessName => Path.GetFileName(AppPaths.SlipstreamExePath);

    private static SingBoxRoute BuildRoute(Profile profile, List<string> processes,
        string routingMode = "split", bool hasUdpProxy = false, bool isExcludeMode = false,
        bool blockQuicOnTcpProxy = true, bool isDnsTunnel = false,
        List<string>? dnsTunnelResolverIps = null, bool proxyIsUdpNative = false)
    {
        var isFullTunnel = (routingMode ?? "split").Equals("full", StringComparison.OrdinalIgnoreCase);

        var rules = new List<RouteRule>
        {
            new() { Action = "sniff", Timeout = "300ms" },
        };

        // DNS-tunnel self-exclusion must precede hijack-dns and the proxy final, or slipstream's own resolver traffic loops back into the tunnel.
        if (isDnsTunnel)
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

        rules.Add(new RouteRule { Protocol = "dns", Action = "hijack-dns" });

        rules.Add(new RouteRule
        {
            IpIsPrivate = true,
            Action      = "route",
            Outbound    = "direct"
        });

        if (blockQuicOnTcpProxy && !hasUdpProxy && !proxyIsUdpNative)
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
        else if (isFullTunnel && hasUdpProxy)
        {
            rules.Add(new RouteRule
            {
                Network  = "udp",
                Action   = "route",
                Outbound = "proxy-udp"
            });
        }

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
}
