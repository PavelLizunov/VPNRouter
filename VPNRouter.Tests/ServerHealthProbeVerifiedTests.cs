using System.Linq;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class ServerHealthProbeVerifiedTests
{
    private static ServerLiveness Live(string name, int ms, bool verified = true)
        => new(new VlessServerEntry { Name = name, Server = "10.0.0.1", Port = 443 }, true, ms, verified);

    private static ServerLiveness Dead(string name)
        => new(new VlessServerEntry { Name = name, Server = "10.0.0.2", Port = 443 }, false, int.MaxValue);

    [Theory]
    [InlineData("amneziawg", ServerProbeStatus.SkippedNotApplicable, false)]
    [InlineData("vless", ServerProbeStatus.Unknown, false)]
    [InlineData("vless", ServerProbeStatus.Unreachable, true)]
    [InlineData("vless", ServerProbeStatus.Timeout, true)]
    [InlineData("vless", ServerProbeStatus.Ok, true)]
    public async Task ProbeAll_MarksAServerTheProbeCouldNotJudge_AsNotJudged_NotAsDead(string protocol, ServerProbeStatus status, bool judged)
    {
        var probe = new ServerHealthProbe(logger: null, probeOverride: (_, _) => Task.FromResult(new ServerProbeResult(status, status == ServerProbeStatus.Ok ? 30 : 0, null)));
        var server = new VlessServerEntry { Name = "s", Server = "10.0.0.1", Port = 443, Protocol = protocol };

        var result = (await probe.ProbeAllAsync(new[] { server }, System.TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)).Single();

        Assert.Equal(judged, result.Judged);
        Assert.Equal(status == ServerProbeStatus.Ok, result.Alive);
    }

    [Fact]
    public void SilentUdpPort_IsReachableButNotVerified()
    {
        var silent = new ServerProbeResult(ServerProbeStatus.Ok, 2000, TcpTlsProbe.UdpNoReplyNote);
        var tcp = new ServerProbeResult(ServerProbeStatus.Ok, 40, null);

        Assert.True(silent.IsReachable);
        Assert.False(silent.IsVerified);
        Assert.True(tcp.IsVerified);
    }

    [Fact]
    public void PickBest_PrefersAVerifiedServerOverAFasterSilentUdpOne()
    {
        var results = new[] { Live("Iceland HY2", 5, verified: false), Live("Sweden VLESS", 90) };

        Assert.Equal("Sweden VLESS", ServerHealthProbe.PickBest(results)!.Name);
    }

    [Fact]
    public void PickBest_FallsBackToASilentUdpServerWhenNothingIsVerified()
    {
        var results = new[] { Dead("Sweden VLESS"), Live("Iceland HY2", 2000, verified: false) };

        Assert.Equal("Iceland HY2", ServerHealthProbe.PickBest(results)!.Name);
    }

    [Fact]
    public void PickForConnect_KeepsTheSelectedServerEvenWhenItIsAnUnverifiedUdpOne()
    {
        var results = new[] { Live("Sweden VLESS", 30), Live("Iceland HY2", 2000, verified: false) };

        Assert.Equal("Iceland HY2", ServerHealthProbe.PickForConnect(results, "Iceland HY2")!.Name);
    }

    [Fact]
    public void PickForConnect_TunnelPollutedSession_OnlyUnverifiedLeft_StillPicksOne()
    {
        var results = new[]
        {
            Dead("Sweden VLESS"), Dead("TRANSIP VLESS"), Dead("infomaniak VLESS"),
            Live("Sweden HY2", 2000, verified: false), Live("Iceland New HY2", 1999, verified: false),
        };

        var picked = ServerHealthProbe.PickForConnect(results, "Sweden VLESS");

        Assert.NotNull(picked);
        Assert.EndsWith("HY2", picked!.Name);
    }

    [Fact]
    public async Task ProbeAll_MarksSilentUdpResultsUnverified()
    {
        var probe = new ServerHealthProbe(probeOverride: (s, _) => Task.FromResult(
            s.Name == "udp"
                ? new ServerProbeResult(ServerProbeStatus.Ok, 2000, TcpTlsProbe.UdpNoReplyNote)
                : new ServerProbeResult(ServerProbeStatus.Ok, 40, null)));
        var servers = new[]
        {
            new VlessServerEntry { Name = "udp", Server = "1.1.1.1", Port = 8444 },
            new VlessServerEntry { Name = "tcp", Server = "2.2.2.2", Port = 443 },
        };

        var results = await probe.ProbeAllAsync(servers, System.TimeSpan.FromSeconds(2));

        Assert.False(results.Single(r => r.Server.Name == "udp").Verified);
        Assert.True(results.Single(r => r.Server.Name == "tcp").Verified);
    }
}
