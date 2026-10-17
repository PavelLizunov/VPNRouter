using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class ProblemDetectorTests
{
    // Lines from the 2026-10-17 support case: full tunnel, the game server reached through the Swedish VLESS server.
    private const string FullTunnelGameSession = """
+0300 2026-10-17 11:04:11 INFO [1456988962 0ms] outbound/vless[vless-Sweden VLESS ~zappodler]: outbound connection to 202.49.176.110:8455
+0300 2026-10-17 11:28:37 INFO sing-box started (0.53s)
+0300 2026-10-17 11:32:00 INFO [3479212803 0ms] outbound/vless[vless-Sweden VLESS ~zappodler]: outbound connection to 202.49.176.110:8456
+0300 2026-10-17 11:32:00 INFO [3479212803 412ms] outbound/vless[vless-Sweden VLESS ~zappodler]: outbound connection to 202.49.176.110:8456
+0300 2026-10-17 11:33:03 INFO [343192332 0ms] outbound/vless[vless-Sweden VLESS ~zappodler]: outbound connection to 202.49.176.110:8455
+0300 2026-10-17 11:35:34 INFO [2856132021 1ms] outbound/vless[vless-Sweden VLESS ~zappodler]: outbound packet connection to 202.49.176.110:8455
+0300 2026-10-17 11:35:53 INFO [911541764 0ms] outbound/vless[vless-Sweden VLESS ~zappodler]: outbound connection to 202.49.176.110:8101
+0300 2026-10-17 11:36:00 INFO [911541765 0ms] outbound/vless[vless-Sweden VLESS ~zappodler]: outbound connection to 64.233.164.188:443
+0300 2026-10-17 11:38:36 INFO [2060890596 0ms] outbound/direct[direct]: outbound connection to 31.31.198.99:443
""";

    private const string CurrentJson = """
{ "outbounds": [ { "type": "vless", "tag": "vless-Sweden", "server": "155.4.244.204", "server_port": 443 },
                 { "type": "direct", "tag": "direct" } ] }
""";

    [Fact]
    public void Detect_FullTunnelWithGameFlows_ReportsGameTraffic()
    {
        var problems = ProblemDetector.Detect(true, 1420, null, CurrentJson, FullTunnelGameSession);

        var problem = Assert.Single(problems);
        Assert.Equal(DetectedProblemKind.GameTrafficThroughFullTunnel, problem.Kind);
    }

    [Fact]
    public void Detect_SplitTunnel_DoesNotReportGameTraffic()
    {
        Assert.Empty(ProblemDetector.Detect(false, 1420, null, CurrentJson, FullTunnelGameSession));
    }

    [Fact]
    public void CountProxiedGamePorts_IgnoresWebPortsDirectFlowsAndOtherSessions()
    {
        var session = ProblemDetector.CurrentSession(FullTunnelGameSession);

        // 8456, 8455 (TCP and UDP) and 8101 after the last start; 443 and the direct flow do not count.
        Assert.Equal(3, ProblemDetector.CountProxiedGamePorts(session));
        Assert.DoesNotContain("11:04:11", session);
    }

    [Fact]
    public void Detect_WebOnlyFullTunnel_ReportsNothing()
    {
        const string log = """
+0300 2026-10-17 11:28:37 INFO sing-box started (0.53s)
+0300 2026-10-17 11:30:00 INFO [1 0ms] outbound/vless[vless-Sweden]: outbound connection to 142.250.1.1:443
+0300 2026-10-17 11:30:01 INFO [2 0ms] outbound/vless[vless-Sweden]: outbound connection to www.gstatic.com:80
+0300 2026-10-17 11:30:02 INFO [3 0ms] outbound/vless[vless-Sweden]: outbound connection to 64.233.164.188:5228
""";
        Assert.Empty(ProblemDetector.Detect(true, 1420, null, CurrentJson, log));
    }

    [Fact]
    public void Detect_MtuAboveMeasuredPath_SuggestsTheProbePayload()
    {
        var probe = new HealthCheck.PathMtuProbeResult(1380, false);

        var problem = Assert.Single(ProblemDetector.Detect(false, 1420, probe, null, null));

        Assert.Equal(DetectedProblemKind.MtuAbovePath, problem.Kind);
        Assert.Equal(1380, problem.SuggestedMtu);
    }

    [Theory]
    [InlineData(1420, 1420, false)] // path fits
    [InlineData(1400, 1380, false)] // 1400 <= 1380 + 28
    [InlineData(1420, 1300, false)] // payload too low for an automatic change
    [InlineData(1420, null, false)] // probe found nothing
    [InlineData(1420, 1380, true)]
    public void SuggestMtu_FollowsTheHealthCheckRule(int configured, int? payload, bool expected)
    {
        var suggestion = ProblemDetector.SuggestMtu(configured, new HealthCheck.PathMtuProbeResult(payload, false));
        Assert.Equal(expected, suggestion != null);
    }

    [Fact]
    public void SuggestMtu_BlockedPing_SuggestsNothing()
    {
        Assert.Null(ProblemDetector.SuggestMtu(1420, new HealthCheck.PathMtuProbeResult(null, true)));
        Assert.Null(ProblemDetector.SuggestMtu(1420, null));
    }

    [Fact]
    public void SuggestMtu_ProbeSkippedBecauseTunnelIsUp_SuggestsNothing()
    {
        // A ping through our own TUN (MTU 1420) tops out at payload 1392 and would always look too small.
        Assert.Null(ProblemDetector.SuggestMtu(1420, new HealthCheck.PathMtuProbeResult(null, false, SkippedTunnelUp: true)));
    }

    [Theory]
    [InlineData(1380)]
    [InlineData(1400)]
    [InlineData(1332)]
    public void SuggestMtu_AfterTheFix_TheSamePathSuggestsNothingMore(int payload)
    {
        var probe = new HealthCheck.PathMtuProbeResult(payload, false);
        var first = ProblemDetector.SuggestMtu(1500, probe);

        Assert.NotNull(first);
        Assert.Null(ProblemDetector.SuggestMtu(first!.Value, probe));
    }

    [Fact]
    public void SuggestMtu_NeverRaisesTheMtu()
    {
        Assert.Null(ProblemDetector.SuggestMtu(1350, new HealthCheck.PathMtuProbeResult(1340, false)));
        Assert.Null(ProblemDetector.SuggestMtu(1300, new HealthCheck.PathMtuProbeResult(1420, false)));
    }

    [Theory]
    [InlineData("VPNRouter-TUN", true)]
    [InlineData("vpnrouter-tun", true)]
    [InlineData("sing-box-tun", true)]
    [InlineData("Ethernet", false)]
    [InlineData("Wi-Fi", false)]
    public void IsOwnTunnelInterface_MatchesOnlyOurAdapters(string name, bool expected)
    {
        Assert.Equal(expected, HealthCheck.IsOwnTunnelInterface(name));
    }

    [Fact]
    public void Detect_RepeatedEndpointTimeoutsSinceLastStart_ReportsServerTimeouts()
    {
        const string log = """
+0300 2026-10-17 11:18:58 INFO sing-box started (0.46s)
+0300 2026-10-17 11:19:38 ERROR [2571678741 5.6s] outbound/urltest[proxy]: dial tcp 155.4.244.204:443: i/o timeout
+0300 2026-10-17 11:19:38 ERROR [2571678741 5.6s] connection: open connection to 140.248.139.82:443 using outbound/urltest[proxy]: dial tcp 155.4.244.204:443: i/o timeout
+0300 2026-10-17 11:20:38 ERROR [2571678742 5.6s] outbound/urltest[proxy]: dial tcp 155.4.244.204:443: i/o timeout
""";

        var problem = Assert.Single(ProblemDetector.Detect(false, 1420, null, CurrentJson, log));
        Assert.Equal(DetectedProblemKind.ServerTimeouts, problem.Kind);
    }

    [Fact]
    public void Detect_BrokenCurrentJson_DoesNotThrow()
    {
        Assert.Empty(ProblemDetector.Detect(false, 1420, null, "{ not json", "dial tcp 1.2.3.4:443: i/o timeout"));
    }

    [Fact]
    public void PlanFix_CombinesAllFixesAndReconnectsOnlyWhenConnected()
    {
        var problems = new[]
        {
            new DetectedProblem(DetectedProblemKind.GameTrafficThroughFullTunnel),
            new DetectedProblem(DetectedProblemKind.MtuAbovePath, 1380)
        };

        var connected = ProblemDetector.PlanFix(problems, connected: true);
        Assert.True(connected.SwitchToSplit);
        Assert.Equal(1380, connected.NewMtu);
        Assert.True(connected.Reconnect);

        Assert.False(ProblemDetector.PlanFix(problems, connected: false).Reconnect);
        Assert.True(ProblemDetector.PlanFix(System.Array.Empty<DetectedProblem>(), connected: true).IsEmpty);
    }
}

public sealed class HelpVideoLinkTests
{
    [Theory]
    [InlineData(null, true, "https://vpn.ninitux.com/help/")]
    [InlineData("games", true, "https://vpn.ninitux.com/help/#games")]
    [InlineData("rollback", false, "https://vpn.ninitux.com/help/?lang=en#rollback")]
    public void HelpVideoUrl_OpensThePageInTheAppLanguageAtTheCase(string? anchor, bool russian, string expected)
    {
        Assert.Equal(expected, VPNRouter.App.ViewModels.MainWindowViewModel.HelpVideoUrl(anchor, russian));
    }

    [Fact]
    public void HelpAnchor_GameTrafficOpensTheGamesGuide()
    {
        Assert.Equal("games", VPNRouter.App.ViewModels.MainWindowViewModel.HelpAnchor(DetectedProblemKind.GameTrafficThroughFullTunnel));
    }
}
