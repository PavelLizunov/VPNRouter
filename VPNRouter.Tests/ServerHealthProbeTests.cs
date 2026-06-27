using System;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// G1 (2026-06-27) Smart Connect acceptance, at the deterministic logic layer:
/// the probe + selector must land Connect on a LIVE server, never a dead one,
/// and signal "none alive" honestly. The protocol probe is injected so the
/// exclude-dead / fastest-wins / none-alive contract is fully testable.
/// </summary>
public class ServerHealthProbeTests
{
    private static VlessServerEntry Srv(string name, string host, int port = 443)
        => new() { Name = name, Server = host, Port = port };

    private static ServerLiveness Live(string name, string host, bool alive, int ms)
        => new(Srv(name, host), alive, alive ? ms : int.MaxValue);

    // Injected probe: "good*" hosts reachable, everything else unreachable.
    private static ServerHealthProbe ProbeWhere(Func<VlessServerEntry, bool> alive, Func<VlessServerEntry, int>? latency = null)
        => new(probeOverride: (s, _) => Task.FromResult(
            alive(s)
                ? new ServerProbeResult(ServerProbeStatus.Ok, latency?.Invoke(s) ?? 50, null)
                : new ServerProbeResult(ServerProbeStatus.Unreachable, 0, "dead")));

    // ── PickBest / AliveRanked (pure) ────────────────────────────────────────

    [Fact]
    public void PickBest_ExcludesDead_PicksFastestAlive()
    {
        var results = new[]
        {
            Live("Germany", "1.1.1.1", alive: true, ms: 120),
            Live("Latvia",  "2.2.2.2", alive: false, ms: 0),   // dead -> never picked
            Live("Iceland", "3.3.3.3", alive: true, ms: 40),   // fastest alive
            Live("Nether",  "4.4.4.4", alive: true, ms: 200),
        };

        var best = ServerHealthProbe.PickBest(results);

        Assert.NotNull(best);
        Assert.Equal("Iceland", best!.Name);
    }

    [Fact]
    public void PickBest_DeadIsNeverPicked_EvenIfListedFirst()
    {
        var results = new[]
        {
            Live("DeadFirst", "1.1.1.1", alive: false, ms: 0),
            Live("LiveOnly",  "2.2.2.2", alive: true,  ms: 999),
        };

        Assert.Equal("LiveOnly", ServerHealthProbe.PickBest(results)!.Name);
    }

    [Fact]
    public void PickBest_AllDead_ReturnsNull()
    {
        var results = new[]
        {
            Live("A", "1.1.1.1", alive: false, ms: 0),
            Live("B", "2.2.2.2", alive: false, ms: 0),
        };

        Assert.Null(ServerHealthProbe.PickBest(results));
    }

    [Fact]
    public void PickBest_EmptyOrNull_ReturnsNull()
    {
        Assert.Null(ServerHealthProbe.PickBest(Array.Empty<ServerLiveness>()));
        Assert.Null(ServerHealthProbe.PickBest(null!));
    }

    [Fact]
    public void AliveRanked_ReturnsOnlyAlive_FastestFirst()
    {
        var results = new[]
        {
            Live("Slow", "1.1.1.1", alive: true,  ms: 300),
            Live("Dead", "2.2.2.2", alive: false, ms: 0),
            Live("Fast", "3.3.3.3", alive: true,  ms: 30),
        };

        var ranked = ServerHealthProbe.AliveRanked(results);

        Assert.Equal(2, ranked.Count);
        Assert.Equal("Fast", ranked[0].Name);
        Assert.Equal("Slow", ranked[1].Name);
        Assert.DoesNotContain(ranked, s => s.Name == "Dead");
    }

    // ── ProbeAllAsync (injected protocol probe) ──────────────────────────────

    [Fact]
    public async Task ProbeAllAsync_MarksAliveDeadPerInjectedProbe()
    {
        var probe = ProbeWhere(s => s.Server.StartsWith("good"), s => s.Server.Length);

        var servers = new List<VlessServerEntry>
        {
            Srv("Germany", "good-de"),
            Srv("Latvia",  "dead-lv"),
            Srv("Iceland", "good-is"),
        };

        var results = await probe.ProbeAllAsync(servers, TimeSpan.FromSeconds(5));

        Assert.Equal(3, results.Count);
        Assert.True(results.Single(r => r.Server.Name == "Germany").Alive);
        Assert.False(results.Single(r => r.Server.Name == "Latvia").Alive);
        Assert.True(results.Single(r => r.Server.Name == "Iceland").Alive);

        // Acceptance: PickBest over the probe output never yields the dead one.
        var best = ServerHealthProbe.PickBest(results);
        Assert.NotEqual("Latvia", best!.Name);
    }

    [Fact]
    public async Task ProbeAllAsync_AllDead_PickBestIsNull()
    {
        var probe = ProbeWhere(_ => false);
        var servers = new List<VlessServerEntry> { Srv("A", "x"), Srv("B", "y") };

        var results = await probe.ProbeAllAsync(servers, TimeSpan.FromSeconds(5));

        Assert.All(results, r => Assert.False(r.Alive));
        Assert.Null(ServerHealthProbe.PickBest(results)); // honest "none alive"
    }

    [Fact]
    public async Task ProbeAllAsync_SlowStatus_CountsAsAlive()
    {
        // ServerProbeStatus.Slow is still reachable (IsReachable == true).
        var probe = new ServerHealthProbe(
            probeOverride: (_, _) => Task.FromResult(new ServerProbeResult(ServerProbeStatus.Slow, 900, null)));
        var results = await probe.ProbeAllAsync(new List<VlessServerEntry> { Srv("S", "s") }, TimeSpan.FromSeconds(5));

        Assert.True(results.Single().Alive);
        Assert.Equal(900, results.Single().LatencyMs);
    }

    [Fact]
    public async Task ProbeAllAsync_ProbeThrows_TreatedAsDead_NotFatalToOthers()
    {
        var probe = new ServerHealthProbe(probeOverride: (s, _) =>
            s.Server == "boom"
                ? throw new Exception("probe blew up")
                : Task.FromResult(new ServerProbeResult(ServerProbeStatus.Ok, 10, null)));

        var servers = new List<VlessServerEntry> { Srv("Boom", "boom"), Srv("Ok", "ok") };

        var results = await probe.ProbeAllAsync(servers, TimeSpan.FromSeconds(5));

        Assert.False(results.Single(r => r.Server.Name == "Boom").Alive);
        Assert.True(results.Single(r => r.Server.Name == "Ok").Alive);
    }
}
