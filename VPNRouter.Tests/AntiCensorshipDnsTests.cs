using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using VPNRouter.Core.Json;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class AntiCensorshipDnsTests
{
    private static AppSettings CreateSettings()
    {
        return new AppSettings
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
                        Name = "main",
                        Server = "198.51.100.1",
                        Port = 443,
                        Uuid = "00000000-0000-0000-0000-000000000001",
                        Security = "reality",
                        Reality = new VlessRealityConfig { PublicKey = "pubkey", ShortId = "1234" }
                    }
                }
            }
        };
    }

    [Fact]
    public void Generate_EmitsEchSuppressionDnsRule_WithHttpsAndSvcbQueryTypes()
    {
        var settings = CreateSettings();
        var profile = new Profile { Name = "Test", DnsMode = "vpn_only" };
        var processes = new List<string> { "Discord.exe" };

        var config = ConfigGenerator.Generate(profile, processes, settings);

        Assert.NotNull(config.Dns);
        Assert.NotEmpty(config.Dns.Rules);

        // Find the anti-censorship ECH suppression rule
        var echRule = config.Dns.Rules.FirstOrDefault(r =>
            r.Action == "reject" &&
            r.QueryType != null &&
            r.QueryType.Contains("HTTPS") &&
            r.QueryType.Contains("SVCB"));

        Assert.NotNull(echRule);
        Assert.Equal("reject", echRule!.Action);
        Assert.Contains("HTTPS", echRule.QueryType!);
        Assert.Contains("SVCB", echRule.QueryType!);

        // Verify JSON serialization format for sing-box 1.14
        var json = JsonSerializer.Serialize(config, AppJsonContext.Default.SingBoxConfig);
        Assert.Contains("\"query_type\"", json);
        Assert.Contains("\"HTTPS\"", json);
        Assert.Contains("\"SVCB\"", json);
    }

    [Fact]
    public void Generate_DnsServers_EmitTypedFormat114()
    {
        var settings = CreateSettings();
        var profile = new Profile { Name = "Test", DnsMode = "vpn_only" };
        var processes = new List<string> { "Discord.exe" };

        var config = ConfigGenerator.Generate(profile, processes, settings);

        Assert.NotNull(config.Dns?.Servers);
        Assert.True(config.Dns.Servers.Count >= 2);

        var vpnDns = config.Dns.Servers.First(s => s.Tag == "vpn-dns");
        Assert.Equal("https", vpnDns.Type);
        Assert.Equal("1.1.1.1", vpnDns.Server);
        Assert.Equal("proxy", vpnDns.Detour);

        var localDns = config.Dns.Servers.First(s => s.Tag == "local-dns");
        Assert.Equal("https", localDns.Type);
        Assert.Equal("1.1.1.1", localDns.Server);
        Assert.Equal("dns-direct", localDns.Detour);
    }
}
