using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class ConfigGeneratorRealtimeGamesDirectTests
{
    [Fact]
    public void DefaultSettings_RouteGamesDirectDisabled()
    {
        // Default-OFF: the rule is whole-process (TCP+UDP), so in censored regions
        // it breaks login and worsens Error 277. See plans/roblox-277-rca-2026-06-27.md.
        Assert.False(new AppSettings().App.RouteGamesDirect);
    }

    [Fact]
    public void FullTunnel_WithUdpProxy_RoutesRobloxDirectBeforeGenericUdpProxy()
    {
        var settings = CreateFullTunnelUdpSplitSettings(routeGamesDirect: true);

        var config = ConfigGenerator.Generate(CreateProfile(), Array.Empty<string>(), settings);

        Assert.Contains(config.Outbounds, o => o.Tag == "proxy-udp");
        var rules = config.Route.Rules;
        var gameRuleIndex = rules.FindIndex(IsRobloxDirectRule);
        var genericUdpIndex = rules.FindIndex(r =>
            string.Equals(r.Network, "udp", StringComparison.OrdinalIgnoreCase)
            && r.Action == "route"
            && r.Outbound == "proxy-udp");

        Assert.True(gameRuleIndex >= 0, "Roblox direct route rule should be present.");
        Assert.True(genericUdpIndex >= 0, "Full-tunnel UDP proxy rule should be present.");
        Assert.True(gameRuleIndex < genericUdpIndex,
            $"Roblox direct rule at {gameRuleIndex} must precede generic UDP proxy rule at {genericUdpIndex}.");

        var gameRule = rules[gameRuleIndex];
        Assert.Equal(new[] { "RobloxPlayerBeta.exe", "RobloxPlayerLauncher.exe" }, gameRule.ProcessName);
        Assert.Equal("direct", gameRule.Outbound);
    }

    [Fact]
    public void FullTunnel_WhenToggleDisabled_DoesNotEmitRobloxDirectRule()
    {
        var settings = CreateFullTunnelUdpSplitSettings(routeGamesDirect: false);

        var config = ConfigGenerator.Generate(CreateProfile(), Array.Empty<string>(), settings);

        Assert.DoesNotContain(config.Route.Rules, IsRobloxDirectRule);
        Assert.Contains(config.Route.Rules, r =>
            string.Equals(r.Network, "udp", StringComparison.OrdinalIgnoreCase)
            && r.Action == "route"
            && r.Outbound == "proxy-udp");
    }

    private static bool IsRobloxDirectRule(RouteRule rule) =>
        rule.Action == "route"
        && rule.Outbound == "direct"
        && rule.ProcessName?.Contains("RobloxPlayerBeta.exe") == true;

    private static Profile CreateProfile() => new()
    {
        Name = "Games",
        DnsMode = "vpn_only",
    };

    private static AppSettings CreateFullTunnelUdpSplitSettings(bool routeGamesDirect) => new()
    {
        App = new AppConfig
        {
            LogLevel = "info",
            RoutingMode = "full",
            RouteGamesDirect = routeGamesDirect,
            BlockQuicOnTcpProxy = true,
        },
        Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
        SingBox = new SingBoxSettings(),
        Tun = new TunSettings(),
        Vless = new VlessConfig
        {
            ActiveServer = "main-vless",
            Servers = new List<VlessServerEntry>
            {
                new()
                {
                    Name = "main-vless",
                    Protocol = "vless",
                    Server = "game.example.com",
                    Port = 443,
                    Uuid = "11111111-1111-1111-1111-111111111111",
                    Flow = "xtls-rprx-vision",
                    Security = "reality",
                    Reality = new VlessRealityConfig
                    {
                        PublicKey = "testkey",
                        ShortId = "abcd",
                    },
                },
                new()
                {
                    Name = "main-hy2",
                    Protocol = "hysteria2",
                    Server = "game.example.com",
                    Port = 8444,
                    Password = "hy2-password",
                    Tls = new VlessTlsConfig
                    {
                        Enabled = true,
                        ServerName = "game.example.com",
                    },
                },
            },
        },
    };
}
