using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using VPNRouter.Core.Json;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static partial class ConfigGenerator
{

    internal static int NormalizeTunMtu(int mtu)
        => mtu < TunSettings.MinimumMtu || mtu > TunSettings.MaximumMtu
            ? TunSettings.DefaultMtu
            : mtu;

    internal static string SelectTunStack(bool isMacOS)
        => isMacOS ? "gvisor" : "system";

    internal const int AwgEndpointMtu = 1420;

    internal static List<string> ResolveEffectiveAppProcesses(
        IEnumerable<string> resolvedProcessNames,
        AppSettings settings)
    {
        static List<string> Normalize(IEnumerable<string>? names) => (names ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Where(p => !p.Contains('*') && !p.Contains('?'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var routingAppsMode = (settings.App.RoutingAppsMode ?? "include")
            .ToLowerInvariant();
        if (routingAppsMode == "exclude")
            return Normalize(settings.App.RoutingAppsExclude);

        var explicitInclude = Normalize(settings.App.RoutingAppsInclude);
        if (explicitInclude.Count > 0 || settings.App.RoutingAppsIncludeInitialized)
            return explicitInclude;
        return Normalize(resolvedProcessNames);
    }

    internal static string ComputeAppRoutingFingerprint(
        IEnumerable<string> resolvedProcessNames,
        AppSettings settings)
    {
        if ((settings.App.RoutingMode ?? "split")
            .Equals("full", StringComparison.OrdinalIgnoreCase))
            return "full";

        var mode = (settings.App.RoutingAppsMode ?? "include")
            .Equals("exclude", StringComparison.OrdinalIgnoreCase)
            ? "exclude"
            : "include";
        var processes = ResolveEffectiveAppProcesses(resolvedProcessNames, settings)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        return $"{mode}:{string.Join('\n', processes)}";
    }

    public static SingBoxConfig Generate(
        Profile profile,
        IEnumerable<string> resolvedProcessNames,
        AppSettings settings,
        bool? strictDnsOverride = null,
        Func<VlessServerEntry, bool>? isServerAlive = null)
    {
        var routingAppsMode = (settings.App.RoutingAppsMode ?? "include")
            .ToLowerInvariant();
        var isExcludeMode = routingAppsMode == "exclude";
        var appsProcessList = ResolveEffectiveAppProcesses(resolvedProcessNames, settings);

        if (OperatingSystem.IsMacOS())
            appsProcessList = ExpandMacHelperNames(appsProcessList);

        var logPath = AppPaths.SingBoxLogPath;

        var outbounds = BuildOutbounds(settings, out bool hasUdpProxy,
            out bool isDnsTunnel, out var dnsTunnelResolverIps, out var endpoints,
            out bool proxyIsUdpNativeOutbound, isServerAlive);

        var proxyIsUdpNative = endpoints != null && endpoints.Count > 0;
        var proxyCarriesUdpNatively = proxyIsUdpNative || proxyIsUdpNativeOutbound;

        var config = new SingBoxConfig
        {
            Log = new SingBoxLog
            {
                Level = settings.App.LogLevel,
                Timestamp = true,
                Output = logPath
            },
            Dns = BuildDns(profile, appsProcessList, settings, isExcludeMode, strictDnsOverride, proxyIsUdpNative),
            Inbounds = BuildInbounds(settings, proxyIsUdpNative),
            Outbounds = outbounds,
            Endpoints = endpoints,
            Route = BuildRoute(profile, appsProcessList, settings.App.RoutingMode, hasUdpProxy, isExcludeMode, settings.App.BlockQuicOnTcpProxy, isDnsTunnel, dnsTunnelResolverIps, proxyIsUdpNative: proxyCarriesUdpNatively),
            Experimental = new SingBoxExperimental
            {
                ClashApi = new ClashApi
                {
                    ExternalController = string.IsNullOrWhiteSpace(settings.SingBox?.ClashApi)
                        ? "127.0.0.1:9090" : settings.SingBox.ClashApi,
                    Secret = string.IsNullOrEmpty(settings.SingBox?.ClashApiSecret)
                        ? null : settings.SingBox.ClashApiSecret,
                }
            }
        };

        var customFirst = string.Equals(
            settings.App.CustomRulesPriority,
            "custom_first",
            StringComparison.OrdinalIgnoreCase);

        if (customFirst)
        {
            if (settings.App.BlockAds) ApplyAdBlock(config);
            if (settings.App.BypassRussianTraffic && GeoDataDownloader.AreGeoFilesAvailable())
                ApplyGeoBypass(config);
            if (settings.App.CustomRules?.Count > 0)
                ApplyCustomRules(config, settings.App.CustomRules);
        }
        else
        {
            if (settings.App.CustomRules?.Count > 0)
                ApplyCustomRules(config, settings.App.CustomRules);
            if (settings.App.BlockAds) ApplyAdBlock(config);
            if (settings.App.BypassRussianTraffic && GeoDataDownloader.AreGeoFilesAvailable())
                ApplyGeoBypass(config);
        }

        return config;
    }

    internal static readonly JsonSerializerOptions SingBoxOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = JsonTypeInfoResolver.Combine(
            AppJsonContext.Default,
            new DefaultJsonTypeInfoResolver()),
    };

    public static string Serialize(SingBoxConfig config)
    {
        return JsonSerializer.Serialize(config, Json.AppJsonContext.Default.SingBoxConfig);
    }

    private static List<SingBoxInbound> BuildInbounds(AppSettings settings, bool proxyIsUdpNative = false)
    {
        var routeExcludes = settings.Tun.GetEffectiveRouteExcludeAddress();
        var mtu = NormalizeTunMtu(settings.Tun.Mtu);
        return new List<SingBoxInbound>
        {
            new()
            {
                Type                    = "tun",
                Tag                     = "tun-in",
                InterfaceName           = OperatingSystem.IsMacOS() ? "utun99" : settings.Tun.InterfaceName,
                Address                 = new List<string> { settings.Tun.Ipv4Address },
                Mtu                     = proxyIsUdpNative
                                            ? Math.Min(mtu, AwgEndpointMtu)
                                            : mtu,
                AutoRoute               = settings.Tun.AutoRoute,
                StrictRoute             = false,
                RouteExcludeAddress     = routeExcludes.Count > 0
                                            ? routeExcludes
                                            : null,
                EndpointIndependentNat  = true,
                Stack                   = SelectTunStack(OperatingSystem.IsMacOS())
            }
        };
    }

}
