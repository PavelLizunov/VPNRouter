using System.Linq;
using System.Text.Json;
using VPNRouter.Core.Json;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class LocalNetworkInvariantConfigTests
{
    private static readonly string[] Mandatory = TunSettings.MandatoryLocalRouteExcludeAddress;

    private static VlessServerEntry Vless() => new()
    {
        Name = "srv",
        Server = "1.2.3.4",
        Port = 443,
        Uuid = "11111111-2222-3333-4444-555555555555",
        Flow = "xtls-rprx-vision",
        Security = "reality",
        Reality = new VlessRealityConfig
        {
            Enabled = true,
            ServerName = "www.cloudflare.com",
            Fingerprint = "chrome",
            PublicKey = "gDawCMB0X6iGXZkG8nZIFW5TaaW29x0DMzWijN-gc2A",
            ShortId = "d86e92a0c6dd2271",
        },
    };

    private static AppSettings Settings(string routingMode) => new()
    {
        App = new AppConfig { LogLevel = "info", ConfigMode = "generated", RoutingMode = routingMode },
        Tun = new TunSettings(),
        Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
        SingBox = new SingBoxSettings(),
        Vless = new VlessConfig { ActiveServer = "srv", Servers = new() { Vless() } },
    };

    private static Profile FullProfile() => new() { Name = "FullTunnel", DnsMode = "vpn_only", Processes = new() };
    private static Profile SplitProfile() => new()
    {
        Name = "Split",
        DnsMode = "vpn_only",
        Processes = new() { new ProcessRule { Name = "Discord.exe" } },
    };

    private static List<string> TunExcludes(string json)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var ib in doc.RootElement.GetProperty("inbounds").EnumerateArray())
        {
            if (ib.TryGetProperty("type", out var t) && t.GetString() == "tun"
                && ib.TryGetProperty("route_exclude_address", out var rex)
                && rex.ValueKind == JsonValueKind.Array)
                return rex.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        }
        return new();
    }

    [Theory]
    [InlineData("full")]
    [InlineData("split")]
    public void GeneratedConfig_TunExcludes_ContainEveryMandatoryLocalRange(string mode)
    {
        var profile = mode == "full" ? FullProfile() : SplitProfile();
        var config = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, Settings(mode));
        var json = JsonSerializer.Serialize(config, AppJsonContext.Default.SingBoxConfig);

        var excludes = TunExcludes(json);
        foreach (var range in Mandatory)
            Assert.Contains(range, excludes);
    }

    [Fact]
    public void CustomConfigInjection_MergesMandatoryRanges_AndKeepsUserRange()
    {
        const string userRange = "10.9.1.0/24";
        var raw =  """
            {
              "inbounds": [
                { "type": "tun", "tag": "tun-in", "address": ["172.19.0.1/30"],
                  "route_exclude_address": ["10.9.1.0/24"] }
              ],
              "outbounds": [
                { "type": "vless", "tag": "proxy", "server": "1.2.3.4", "server_port": 443,
                  "uuid": "11111111-2222-3333-4444-555555555555" },
                { "type": "direct", "tag": "direct" }
              ]
            }
            """;

        var settings = new AppSettings
        {
            App = new AppConfig { ConfigMode = "custom", RoutingMode = "full" },
            Tun = new TunSettings(),
        };

        var injected = CustomConfigInjector.Inject(raw, new[] { "Discord.exe" }, settings);
        var excludes = TunExcludes(injected);

        foreach (var range in Mandatory)
            Assert.Contains(range, excludes);
        Assert.Contains(userRange, excludes);
    }

    [Fact]
    public void MandatorySet_CoversV4AndV6PrivateAndLinkLocal()
    {
        foreach (var expect in new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16",
                                       "169.254.0.0/16", "127.0.0.0/8", "::1/128", "fe80::/10", "fc00::/7" })
            Assert.Contains(expect, Mandatory);
    }
}
