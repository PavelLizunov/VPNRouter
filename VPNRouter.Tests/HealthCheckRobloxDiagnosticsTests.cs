using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class HealthCheckRobloxDiagnosticsTests
{
    [Fact]
    public void ExtractProxyEndpoints_ReadsSingBoxOutbounds()
    {
        var endpoints = HealthCheck.ExtractProxyEndpointsFromCurrentJson("""
{
  "outbounds": [
    { "type": "direct", "tag": "direct" },
    { "type": "vless", "tag": "proxy", "server": "104.194.156.93", "server_port": 443 },
    { "type": "hysteria2", "tag": "proxy-udp", "server": "104.194.156.93", "server_port": 8444 }
  ]
}
""");

        Assert.Contains("104.194.156.93:443", endpoints);
        Assert.Contains("104.194.156.93:8444", endpoints);
    }

    [Fact]
    public void CountProxyDialTimeouts_CountsOnlyConfiguredEndpointTimeouts()
    {
        var endpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "104.194.156.93:443"
        };
        var log = """
+0300 2026-07-08 ERROR connection: open connection to 128.116.44.3:443 using outbound/vless[proxy]: dial tcp 104.194.156.93:443: i/o timeout
+0300 2026-07-08 ERROR connection: open connection to 104.18.22.242:443 using outbound/vless[proxy]: dial tcp 104.194.156.93:443: i/o timeout
+0300 2026-07-08 ERROR connection: open connection to 149.154.167.41:443 using outbound/vless[proxy]: dial tcp 38.60.245.177:443: i/o timeout
+0300 2026-07-08 ERROR connection: connection upload closed: raw read: An existing connection was forcibly closed by the remote host.
""";

        Assert.Equal(2, HealthCheck.CountProxyDialTimeouts(log, endpoints));
    }

    [Fact]
    public void BuildPathMtuWarning_WarnsWhenConfiguredMtuAboveMeasuredPath()
    {
        var warning = HealthCheck.BuildPathMtuWarning(1380, bestPayload: 1350, plainPingBlocked: false);

        Assert.NotNull(warning);
        Assert.Equal(HealthCheck.Level.Warn, warning.Value.Severity);
        Assert.Contains("Roblox", warning.Value.Message);
        Assert.Contains("1350", warning.Value.Message);
    }

    [Fact]
    public void BuildPathMtuWarning_ReturnsNullWhenConfiguredMtuHasRoom()
    {
        Assert.Null(HealthCheck.BuildPathMtuWarning(1320, bestPayload: 1350, plainPingBlocked: false));
    }

    [Fact]
    public void BuildAdvice_RobloxOnVlessGivesTransportAction()
    {
        var advice = HealthCheck.BuildAdvice(new AppSettings(), """
{
  "outbounds": [
    { "type": "vless", "tag": "proxy", "server": "104.194.156.93", "server_port": 443 },
    { "type": "hysteria2", "tag": "proxy-udp", "server": "104.194.156.93", "server_port": 8444 }
  ]
}
""", """
open connection to 128.116.21.33:58581 using outbound/hysteria2[vless-udp-Germany HY2]
gamejoin.roblox.com resolved
""");

        var item = Assert.Single(advice, a => a.Action == HealthAdviceAction.ChangeTransport);
        Assert.Contains("Roblox", item.Problem);
        Assert.Contains("UDP", item.ActionText);
    }

    [Fact]
    public void BuildAdvice_PrivacyDoesNotAutoBypassRoblox()
    {
        var settings = new AppSettings();
        settings.App.ConnectionIntent = ConnectionIntent.Privacy;

        var advice = HealthCheck.BuildAdvice(settings, "{}", "gamejoin.roblox.com");

        Assert.Contains(advice, a => a.Action == HealthAdviceAction.BypassApp
            && a.ActionText.Contains("explicit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildAdvice_DiscordDnsStalls_GivesBypassAction()
    {
        var advice = HealthCheck.BuildAdvice(new AppSettings(), "{}", """
+0300 2026-07-08 22:06:34 ERROR [1790913872 10.0s] dns: exchange failed for discord.com. IN A: context deadline exceeded
+0300 2026-07-08 22:07:36 INFO [3701978089 17.20s] dns: exchanged A discord.com. 204 IN A 162.159.137.232
""");

        var item = Assert.Single(advice, a => a.Action == HealthAdviceAction.BypassApp
            && a.Problem.Contains("Discord", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("direct", item.ActionText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildAdvice_InvalidCurrentJson_DoesNotThrow()
    {
        var advice = HealthCheck.BuildAdvice(new AppSettings(), "{", "gamejoin.roblox.com");

        Assert.NotNull(advice);
    }
}
