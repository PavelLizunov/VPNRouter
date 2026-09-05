using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

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

    // ── PickForConnect (G1 connect decision) ─────────────────────────────────

    [Fact]
    public void PickForConnect_ActiveAlive_KeepsActive_EvenIfSlower()
    {
        var results = new[] { Live("DE", "1", true, 100), Live("IS", "2", true, 40) };
        // Active = DE, which is alive (though slower) — respect the explicit pick.
        Assert.Equal("DE", ServerHealthProbe.PickForConnect(results, "DE")!.Name);
    }

    [Fact]
    public void PickForConnect_ActiveDead_SwitchesToFastestLive()
    {
        var results = new[] { Live("DE", "1", false, 0), Live("IS", "2", true, 40), Live("NL", "3", true, 200) };
        Assert.Equal("IS", ServerHealthProbe.PickForConnect(results, "DE")!.Name);
    }

    [Fact]
    public void PickForConnect_AllDead_ReturnsNull()
    {
        var results = new[] { Live("DE", "1", false, 0), Live("IS", "2", false, 0) };
        Assert.Null(ServerHealthProbe.PickForConnect(results, "DE"));
    }

    [Fact]
    public void PickForConnect_ActiveNotInPool_OrNoActive_PicksFastestLive()
    {
        var results = new[] { Live("DE", "1", true, 100), Live("IS", "2", true, 40) };
        Assert.Equal("IS", ServerHealthProbe.PickForConnect(results, "GONE")!.Name);
        Assert.Equal("IS", ServerHealthProbe.PickForConnect(results, null)!.Name);
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

    // ── NIGHT-09 & Aggregator Bounded Workers & Cancellation Invariants ─────

    [Fact]
    public void MaxConcurrency_ConstantIsEight()
    {
        Assert.Equal(8, ServerHealthProbe.MaxConcurrency);

        var field = typeof(ServerHealthProbe).GetField(
            "MaxConcurrency",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(field);
        Assert.Equal(8, field!.GetValue(null));
    }

    [Fact]
    public async Task ProbeAllAsync_MoreThanEightCandidates_BlockingTcsObservesMaxEight_AllEventuallyProcessed_FastestTailChosen()
    {
        var servers = Enumerable.Range(1, 12).Select(i => Srv($"S{i:D2}", $"host{i}")).ToList();

        int currentActive = 0;
        int maxActive = 0;
        var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var probe = new ServerHealthProbe(probeOverride: async (s, ct) =>
        {
            var active = Interlocked.Increment(ref currentActive);
            int prev;
            do
            {
                prev = maxActive;
                if (active <= prev) break;
            } while (Interlocked.CompareExchange(ref maxActive, active, prev) != prev);

            if (active == 8)
            {
                readyTcs.TrySetResult();
            }

            // Wait for release signal so all 8 workers are concurrently active
            await releaseTcs.Task.ConfigureAwait(false);
            Interlocked.Decrement(ref currentActive);

            // Tail server (S12) is alive and fastest
            int latency = s.Name == "S12" ? 15 : 100;
            return new ServerProbeResult(ServerProbeStatus.Ok, latency, null);
        });

        var probeTask = probe.ProbeAllAsync(servers, TimeSpan.FromSeconds(15));

        List<ServerLiveness> results;
        try
        {
            // Block until exactly 8 workers are active simultaneously
            await readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(8, Volatile.Read(ref maxActive));
        }
        finally
        {
            // Release the workers to process remaining items
            releaseTcs.TrySetResult();
            results = await probeTask;
        }

        // All 12 candidates were eventually processed
        Assert.Equal(12, results.Count);
        Assert.All(results, r => Assert.True(r.Alive));
        Assert.Equal(8, maxActive);

        // Fastest tail server S12 was chosen
        var best = ServerHealthProbe.PickBest(results);
        Assert.NotNull(best);
        Assert.Equal("S12", best!.Name);
    }

    [Fact]
    public async Task ProbeAllAsync_DeadlineStopsUnstartedAndCancelsWorkers_ReturnsDeadFullCount()
    {
        var servers = Enumerable.Range(1, 12).Select(i => Srv($"S{i:D2}", $"host{i}")).ToList();

        int started = 0;
        int completed = 0;
        var probe = new ServerHealthProbe(probeOverride: async (s, token) =>
        {
            Interlocked.Increment(ref started);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                return new ServerProbeResult(ServerProbeStatus.Ok, 20, null);
            }
            finally
            {
                Interlocked.Increment(ref completed);
            }
        });

        // Short deadline stops unstarted and cancels in-flight workers
        var results = await probe.ProbeAllAsync(servers, TimeSpan.FromMilliseconds(50));

        // Must return all 12 entries (full count, unstarted stay dead)
        Assert.Equal(12, results.Count);
        Assert.All(results, r =>
        {
            Assert.False(r.Alive);
            Assert.Equal(int.MaxValue, r.LatencyMs);
        });

        // Concurrency is bounded by MaxConcurrency (8), so at most 8 could have started
        Assert.True(started <= 8);
        // All started workers completed cleanup on deadline cancellation
        Assert.Equal(started, completed);
    }

    [Fact]
    public async Task ProbeAllAsync_AllHangUntilDeadline_ReturnsDeadFullCount_PickBestNull()
    {
        var servers = Enumerable.Range(1, 10).Select(i => Srv($"S{i:D2}", $"host{i}")).ToList();

        var probe = new ServerHealthProbe(probeOverride: async (s, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return new ServerProbeResult(ServerProbeStatus.Ok, 10, null);
        });

        var results = await probe.ProbeAllAsync(servers, TimeSpan.FromMilliseconds(30));

        Assert.Equal(10, results.Count);
        Assert.All(results, r =>
        {
            Assert.False(r.Alive);
            Assert.Equal(int.MaxValue, r.LatencyMs);
        });
        Assert.Null(ServerHealthProbe.PickBest(results));
    }

    [Fact]
    public async Task ProbeAllAsync_DeadlineExpired_SwallowedProbeReturnsOk_NeverReportedAlive()
    {
        // Tests: "after awaited _probe token.ThrowIfCancellationRequested so canceled receive never alive."
        var probe = new ServerHealthProbe(probeOverride: async (s, token) =>
        {
            var tcs = new TaskCompletionSource();
            using var reg = token.Register(() => tcs.TrySetResult());
            await tcs.Task.ConfigureAwait(false);

            // Probe swallowed cancellation and attempts to return Ok
            return new ServerProbeResult(ServerProbeStatus.Ok, 15, null);
        });

        var servers = new List<VlessServerEntry> { Srv("S1", "host1") };
        var results = await probe.ProbeAllAsync(servers, TimeSpan.FromMilliseconds(30));

        Assert.Single(results);
        Assert.False(results[0].Alive);
        Assert.Equal(int.MaxValue, results[0].LatencyMs);
    }

    [Fact]
    public async Task ProbeAllAsync_PreCancel_ThrowsOperationCanceledException_EvenEmptyOrNull()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var probe = new ServerHealthProbe(probeOverride: (_, _) =>
            Task.FromResult(new ServerProbeResult(ServerProbeStatus.Ok, 10, null)));

        // Empty list throws
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            probe.ProbeAllAsync(new List<VlessServerEntry>(), TimeSpan.FromSeconds(5), cts.Token));

        // Null list throws
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            probe.ProbeAllAsync(null!, TimeSpan.FromSeconds(5), cts.Token));

        // Populated list throws
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            probe.ProbeAllAsync(new List<VlessServerEntry> { Srv("S1", "host1") }, TimeSpan.FromSeconds(5), cts.Token));
    }

    [Fact]
    public async Task ProbeAllAsync_CallerCancelDuringProbe_ThrowsOperationCanceledException_NotDead()
    {
        using var callerCts = new CancellationTokenSource();
        var probeStartedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var probe = new ServerHealthProbe(probeOverride: async (s, token) =>
        {
            probeStartedTcs.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return new ServerProbeResult(ServerProbeStatus.Ok, 10, null);
        });

        var servers = Enumerable.Range(1, 10).Select(i => Srv($"S{i:D2}", $"host{i}")).ToList();
        var probeTask = probe.ProbeAllAsync(servers, TimeSpan.FromSeconds(10), callerCts.Token);

        try
        {
            await probeStartedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            callerCts.Cancel();

            // Must throw OperationCanceledException, NOT return dead list
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probeTask);
        }
        finally
        {
            callerCts.Cancel();
            try
            {
                await probeTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task ProbeAllAsync_CallerCancel_WorkerCompletionLeavesNoUnobservedTasks()
    {
        int activeWorkers = 0;
        var startTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callerCts = new CancellationTokenSource();

        var probe = new ServerHealthProbe(probeOverride: async (s, token) =>
        {
            Interlocked.Increment(ref activeWorkers);
            startTcs.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                return new ServerProbeResult(ServerProbeStatus.Ok, 10, null);
            }
            finally
            {
                Interlocked.Decrement(ref activeWorkers);
            }
        });

        var servers = Enumerable.Range(1, 10).Select(i => Srv($"S{i:D2}", $"host{i}")).ToList();
        var probeTask = probe.ProbeAllAsync(servers, TimeSpan.FromSeconds(10), callerCts.Token);

        try
        {
            await startTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            callerCts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probeTask);

            // When ProbeAllAsync throws, all workers MUST have completed — zero unobserved running tasks
            Assert.Equal(0, Volatile.Read(ref activeWorkers));
        }
        finally
        {
            callerCts.Cancel();
            try
            {
                await probeTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task ProbeAllAsync_CallerCancelSwallowedByProbe_StillThrowsOperationCanceledException()
    {
        // Tests: "before return ct.ThrowIfCancellationRequested even swallowed probe returns success"
        using var callerCts = new CancellationTokenSource();

        var probe = new ServerHealthProbe(probeOverride: (s, token) =>
        {
            callerCts.Cancel();
            // Swallowed probe returns success
            return Task.FromResult(new ServerProbeResult(ServerProbeStatus.Ok, 20, null));
        });

        var servers = new List<VlessServerEntry> { Srv("S1", "host1") };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            probe.ProbeAllAsync(servers, TimeSpan.FromSeconds(10), callerCts.Token));
    }

    [Fact]
    public async Task ProbeAllAsync_ZeroOrEmpty_ReturnsEmptyList()
    {
        var probe = new ServerHealthProbe();

        var emptyResults = await probe.ProbeAllAsync(new List<VlessServerEntry>(), TimeSpan.FromSeconds(5));
        Assert.Empty(emptyResults);

        var nullResults = await probe.ProbeAllAsync(null!, TimeSpan.FromSeconds(5));
        Assert.Empty(nullResults);
    }

    [Fact]
    public async Task ProbeAllAsync_OrdinaryFailureIsolated_OtherServersSucceed()
    {
        var probe = new ServerHealthProbe(probeOverride: (s, _) =>
        {
            if (s.Name == "Bad")
            {
                throw new InvalidOperationException("simulated network failure");
            }

            return Task.FromResult(new ServerProbeResult(ServerProbeStatus.Ok, 45, null));
        });

        var servers = new List<VlessServerEntry>
        {
            Srv("Bad", "bad.host"),
            Srv("Good", "good.host")
        };

        var results = await probe.ProbeAllAsync(servers, TimeSpan.FromSeconds(5));

        Assert.Equal(2, results.Count);

        var bad = results.Single(r => r.Server.Name == "Bad");
        Assert.False(bad.Alive);
        Assert.Equal(int.MaxValue, bad.LatencyMs);

        var good = results.Single(r => r.Server.Name == "Good");
        Assert.True(good.Alive);
        Assert.Equal(45, good.LatencyMs);

        Assert.Equal("Good", ServerHealthProbe.PickBest(results)!.Name);
    }

    [Fact]
    public async Task ProbeAllAsync_ZeroDeadline_ExecutesNoProbes_ReturnsAllDead()
    {
        int probeCallCount = 0;
        var probe = new ServerHealthProbe(probeOverride: (s, _) =>
        {
            Interlocked.Increment(ref probeCallCount);
            return Task.FromResult(new ServerProbeResult(ServerProbeStatus.Ok, 10, null));
        });

        var servers = new List<VlessServerEntry>
        {
            Srv("S1", "1.1.1.1"),
            Srv("S2", "2.2.2.2"),
            Srv("S3", "3.3.3.3"),
        };

        var results = await probe.ProbeAllAsync(servers, TimeSpan.Zero);

        // Zero deadline must short-circuit without executing any probes
        Assert.Equal(0, Volatile.Read(ref probeCallCount));

        // Must return all entries as dead
        Assert.Equal(3, results.Count);
        Assert.All(results, r =>
        {
            Assert.False(r.Alive);
            Assert.Equal(int.MaxValue, r.LatencyMs);
        });
        Assert.Null(ServerHealthProbe.PickBest(results));
    }

    [Fact]
    public async Task ProbeAllAsync_InfiniteDeadline_WithSynchronousProbe_Succeeds()
    {
        var probe = new ServerHealthProbe(probeOverride: (s, _) =>
            Task.FromResult(new ServerProbeResult(ServerProbeStatus.Ok, 42, null)));

        var servers = new List<VlessServerEntry>
        {
            Srv("S1", "1.1.1.1"),
            Srv("S2", "2.2.2.2"),
        };

        var results = await probe.ProbeAllAsync(servers, Timeout.InfiniteTimeSpan);

        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
        {
            Assert.True(r.Alive);
            Assert.Equal(42, r.LatencyMs);
        });
        var best = ServerHealthProbe.PickBest(results);
        Assert.NotNull(best);
        Assert.Equal("S1", best!.Name);
    }

    [Fact]
    public async Task ProbeAllAsync_InvalidNegativeDeadline_ThrowsArgumentException()
    {
        var probe = new ServerHealthProbe(probeOverride: (_, _) =>
            Task.FromResult(new ServerProbeResult(ServerProbeStatus.Ok, 10, null)));

        var servers = new List<VlessServerEntry> { Srv("S1", "1.1.1.1") };

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            probe.ProbeAllAsync(servers, TimeSpan.FromMilliseconds(-5)));
    }
}
