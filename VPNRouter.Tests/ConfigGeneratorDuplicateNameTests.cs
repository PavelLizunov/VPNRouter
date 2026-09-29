using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public class ConfigGeneratorDuplicateNameTests
{
    private static AppSettings CreateTwoDuplicateNameServers()
    {
        var settings = new AppSettings
        {
            App = new AppConfig { LogLevel = "info" },
            Tun = new TunSettings(),
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
            SingBox = new SingBoxSettings(),
            Vless = new VlessConfig
            {
                Servers = new List<VlessServerEntry>
                {
                    new()
                    {
                        Name = "server-1",
                        Server = "1.1.1.1",
                        Port = 443,
                        Uuid = "uuid-a",
                        Flow = "xtls-rprx-vision",
                        Security = "reality",
                        Reality = new VlessRealityConfig { PublicKey = "kA", ShortId = "aa" }
                    },
                    new()
                    {
                        Name = "server-1",
                        Server = "2.2.2.2",
                        Port = 443,
                        Uuid = "uuid-b",
                        Flow = "xtls-rprx-vision",
                        Security = "reality",
                        Reality = new VlessRealityConfig { PublicKey = "kB", ShortId = "bb" }
                    }
                }
            }
        };
        return settings;
    }

    private static Profile SimpleProfile() => new()
    {
        Name = "Test",
        DnsMode = "vpn_only",
        Processes = new List<ProcessRule>
        {
            new() { Name = "Discord.exe", ScanPatterns = new[] { "Discord.exe" } }
        }
    };

    [Fact]
    public void DuplicateNames_DoNotProduceDuplicateOutboundTags()
    {
        var settings = CreateTwoDuplicateNameServers();
        foreach (var s in settings.Vless.Servers) s.Flow = "";
        settings.Vless.ActiveServer = "";

        var config = ConfigGenerator.Generate(SimpleProfile(), new[] { "Discord.exe" }, settings);

        var tags = config.Outbounds
            .Where(o => !string.IsNullOrEmpty(o.Tag))
            .Select(o => o.Tag!)
            .ToList();
        var uniqueTags = tags.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Assert.Equal(tags.Count, uniqueTags);
    }

    [Fact]
    public void SingleActiveServer_DuplicateNames_StillGeneratesOne()
    {
        var settings = CreateTwoDuplicateNameServers();
        settings.Vless.ActiveServer = "server-1";

        var config = ConfigGenerator.Generate(SimpleProfile(), new[] { "Discord.exe" }, settings);

        var vlessOutbounds = config.Outbounds.Where(o => o.Type == "vless").ToList();
        Assert.Single(vlessOutbounds);
        Assert.Equal("proxy", vlessOutbounds[0].Tag);
    }

}
