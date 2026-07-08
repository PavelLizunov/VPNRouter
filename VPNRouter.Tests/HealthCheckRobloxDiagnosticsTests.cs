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
}
