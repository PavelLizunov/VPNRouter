using System;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public class NaivePairingUdpLivenessTests
{
    private static VlessServerEntry Naive(string name, string pair)
        => new() { Name = name, Protocol = "naive", PairGroup = pair, Server = "naive.host", Port = 443 };
    private static VlessServerEntry Hy2(string name, string pair, string host)
        => new() { Name = name, Protocol = "hysteria2", PairGroup = pair, Server = host, Port = 443 };
    private static VlessServerEntry Tuic(string name, string pair, string host)
        => new() { Name = name, Protocol = "tuic", PairGroup = pair, Server = host, Port = 443 };

    [Fact]
    public void NoProbe_PicksPairedSibling_Unchanged()
    {
        var naive = Naive("Latvia NAIVE", "lv");
        var hy2 = Hy2("Latvia HY2", "lv", "213.155.15.93");
        var pool = new[] { naive, hy2 };

        var pick = NaivePairing.FindUdpSibling(naive, pool);
        Assert.NotNull(pick);
        Assert.Equal("Latvia HY2", pick!.Name);
    }

    [Fact]
    public void Probe_PairedSiblingAlive_PicksPaired()
    {
        var naive = Naive("Latvia NAIVE", "lv");
        var hy2 = Hy2("Latvia HY2", "lv", "213.155.15.93");
        var pool = new[] { naive, hy2 };

        var pick = NaivePairing.FindUdpSibling(naive, pool, isAlive: _ => true);
        Assert.Equal("Latvia HY2", pick!.Name);
    }

    [Fact]
    public void Probe_PairedSiblingDead_FallsBackToAnyAliveUdp()
    {
        var naive = Naive("Latvia NAIVE", "lv");
        var deadHy2 = Hy2("Latvia HY2", "lv", "213.155.15.93");
        var liveHy2 = Hy2("Germany HY2", "de", "104.194.156.93");
        var pool = new[] { naive, deadHy2, liveHy2 };

        Func<VlessServerEntry, bool> alive = s => s.Name != "Latvia HY2";
        var pick = NaivePairing.FindUdpSibling(naive, pool, alive);

        Assert.NotNull(pick);
        Assert.Equal("Germany HY2", pick!.Name);
    }

    [Fact]
    public void RB2_LiveFallback_PrefersUdpNative_Hy2OverTuic()
    {
        var naive = Naive("Latvia NAIVE", "lv");
        var deadHy2 = Hy2("Latvia HY2", "lv", "213.155.15.93");
        var liveTuic = Tuic("Germany TUIC", "de", "104.194.156.93");
        var liveHy2 = Hy2("France HY2", "fr", "51.158.10.1");
        var pool = new[] { naive, deadHy2, liveTuic, liveHy2 };

        Func<VlessServerEntry, bool> alive = s => s.Name != "Latvia HY2";
        var pick = NaivePairing.FindUdpSibling(naive, pool, alive);

        Assert.NotNull(pick);
        Assert.Equal("France HY2", pick!.Name);
    }

    [Fact]
    public void Probe_PairedSiblingDead_PrefersSameHostOverRemote()
    {
        var naive = Naive("Latvia NAIVE", "lv");
        naive.Server = "198.51.100.5";
        var deadHy2 = Hy2("Latvia HY2", "lv", "198.51.100.5");
        var remoteLiveHy2 = Hy2("US HY2", "us", "203.0.113.1");
        var sameHostLiveTuic = Tuic("Latvia TUIC", "lv-other", "198.51.100.5");
        var pool = new[] { naive, deadHy2, remoteLiveHy2, sameHostLiveTuic };

        Func<VlessServerEntry, bool> alive = s => s.Name != "Latvia HY2";
        var pick = NaivePairing.FindUdpSibling(naive, pool, alive);

        Assert.NotNull(pick);
        Assert.Equal("Latvia TUIC", pick!.Name);
    }

    [Fact]
    public void Probe_AllUdpDead_ReturnsNull_NoDeadSibling()
    {
        var naive = Naive("Latvia NAIVE", "lv");
        var deadHy2 = Hy2("Latvia HY2", "lv", "213.155.15.93");
        var pool = new[] { naive, deadHy2 };

        var pick = NaivePairing.FindUdpSibling(naive, pool, isAlive: _ => false);
        Assert.Null(pick);
    }
}
