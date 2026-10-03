using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.FreeConfigs;
using Xunit;

namespace VPNRouter.Tests;

// The probe speaks IPv4 only. An IPv6-only server is "not judged", never "dead": a red mark or a failover skipping it would be a lie.
public sealed class TcpTlsProbeIpv6Tests
{
    private const string V6 = "2001:db8::1";

    [Fact]
    public async Task TcpProbe_Ipv6Literal_IsSkippedNotUnreachable()
    {
        var r = await TcpTlsProbe.ProbeAsync(V6, 443, sni: null, requireTls: false, TestContext.Current.CancellationToken);

        Assert.Equal(ServerProbeStatus.SkippedNotApplicable, r.Status);
        Assert.Equal(TcpTlsProbe.Ipv6OnlyNote, r.Error);
        Assert.False(r.IsReachable);
    }

    [Fact]
    public async Task UdpProbe_Ipv6Literal_IsSkippedNotUnreachable()
    {
        var r = await TcpTlsProbe.ProbeUdpAsync(V6, 443, TestContext.Current.CancellationToken);

        Assert.Equal(ServerProbeStatus.SkippedNotApplicable, r.Status);
        Assert.Equal(TcpTlsProbe.Ipv6OnlyNote, r.Error);
    }

    [Theory]
    [InlineData("vless", "reality")]
    [InlineData("vless", "tls")]
    [InlineData("shadowsocks", "")]
    [InlineData("hysteria2", "")]
    public async Task ServerProbe_Ipv6Server_IsSkippedForEveryProtocolFamily(string protocol, string security)
    {
        var server = new VlessServerEntry { Name = "v6", Server = V6, Port = 443, Protocol = protocol, Security = security };

        var r = await TcpTlsProbe.ProbeServerAsync(server, TestContext.Current.CancellationToken);

        Assert.Equal(ServerProbeStatus.SkippedNotApplicable, r.Status);
    }

    [Fact]
    public async Task FreeConfigTester_Ipv6OnlyConfig_IsNotOfferedAsWorking()
    {
        var cfg = new FreeConfigEntry { Host = V6, Port = 443, Sni = "example.com" };

        await new FreeConfigTester().TestOneAsync(cfg, TestContext.Current.CancellationToken);

        Assert.Equal(FreeConfigStatus.Unreachable, cfg.Status);
        Assert.Contains("IPv6", cfg.LastError);
        Assert.Equal(0, cfg.LatencyMs);
    }
}
