using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public sealed class AutoFailoverCandidateProbeTests
{
    private static AppSettings SubscribeWith(int count)
    {
        var names = Enumerable.Range(1, count).Select(i => $"srv-{i}").ToArray();
        var s = new AppSettings();
        s.App.ConfigMode = "subscribe";
        s.Vless.ActiveServer = names[0];
        s.App.ActiveSubscriptionServer = names[0];
        s.App.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "main",
            Url = "https://example.com/sub",
            Enabled = true,
            Servers = names.Select((n, i) => new VlessServerEntry
            {
                Name = n,
                Server = $"1.2.3.{i + 1}",
                Port = 443,
                Uuid = $"uuid-{i + 1}",
            }).ToList(),
        });
        return s;
    }

    private static AutoFailoverEngine Engine(AppSettings settings,
        Func<VlessServerEntry, CancellationToken, Task<ServerProbeResult>>? probe) =>
        new(settings, new ConfigSanityCheck(), restart: null, store: new InMemorySettingsStore())
        {
            ProbeCandidate = probe,
        };

    private static ServerProbeResult Ok(int ms) => new(ServerProbeStatus.Ok, ms, null);
    private static ServerProbeResult Dead() => new(ServerProbeStatus.Unreachable, 0, "refused");

    [Fact]
    public async Task PicksTheFastestReachableCandidate_NotTheFirstInList()
    {
        var engine = Engine(SubscribeWith(5), (s, _) => Task.FromResult(s.Name switch
        {
            "srv-2" => Dead(),
            "srv-3" => Ok(180),
            "srv-4" => Ok(40),
            _ => Dead(),
        }));

        var outcome = await engine.HandleDeadConfigAsync("dead", TestContext.Current.CancellationToken);

        Assert.True(outcome.Switched);
        Assert.Equal("srv-4", outcome.NewActiveServer);
    }

    [Fact]
    public async Task EqualLatency_KeepsListOrder()
    {
        var engine = Engine(SubscribeWith(4), (s, _) => Task.FromResult(Ok(50)));

        var outcome = await engine.HandleDeadConfigAsync("dead", TestContext.Current.CancellationToken);

        Assert.Equal("srv-2", outcome.NewActiveServer);
    }

    [Fact]
    public async Task AllProbedCandidatesDead_FallsBackToTheFirstInList()
    {
        var engine = Engine(SubscribeWith(4), (_, _) => Task.FromResult(Dead()));

        var outcome = await engine.HandleDeadConfigAsync("dead", TestContext.Current.CancellationToken);

        Assert.True(outcome.Switched);
        Assert.Equal("srv-2", outcome.NewActiveServer);
    }

    [Fact]
    public async Task ACandidateTheProbeCannotJudge_BeatsAKnownDeadOne()
    {
        var engine = Engine(SubscribeWith(4), (s, _) => Task.FromResult(s.Name switch
        {
            "srv-2" => Dead(),
            "srv-3" => new ServerProbeResult(ServerProbeStatus.SkippedNotApplicable, 0, "awg"),
            _ => Dead(),
        }));

        var outcome = await engine.HandleDeadConfigAsync("dead", TestContext.Current.CancellationToken);

        Assert.Equal("srv-3", outcome.NewActiveServer);
    }

    [Fact]
    public async Task AProbeThatThrows_IsTreatedAsUnknown_NotAsAFailoverFailure()
    {
        var engine = Engine(SubscribeWith(3), (s, _) => s.Name == "srv-2"
            ? throw new InvalidOperationException("boom")
            : Task.FromResult(Dead()));

        var outcome = await engine.HandleDeadConfigAsync("dead", TestContext.Current.CancellationToken);

        Assert.True(outcome.Switched);
        Assert.Equal("srv-2", outcome.NewActiveServer);
    }

    [Fact]
    public async Task OnlyTheFirstEightCandidatesAreProbed()
    {
        var probed = new List<string>();
        var engine = Engine(SubscribeWith(14), (s, _) =>
        {
            lock (probed) probed.Add(s.Name!);
            return Task.FromResult(Dead());
        });

        await engine.HandleDeadConfigAsync("dead", TestContext.Current.CancellationToken);

        Assert.Equal(AutoFailoverEngine.MaxCandidateProbes, probed.Count);
        Assert.DoesNotContain("srv-1", probed);
        Assert.Contains("srv-2", probed);
        Assert.DoesNotContain("srv-11", probed);
    }

    [Fact]
    public async Task WithoutAProbe_ListOrderDecides()
    {
        var engine = Engine(SubscribeWith(4), probe: null);

        var outcome = await engine.HandleDeadConfigAsync("dead", TestContext.Current.CancellationToken);

        Assert.Equal("srv-2", outcome.NewActiveServer);
    }

    [Fact]
    public async Task ASlowProbe_IsCutOffByTheBudget_AndFailoverStillSwitches()
    {
        var engine = Engine(SubscribeWith(3), async (s, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Ok(1);
        });

        var started = DateTime.UtcNow;
        var outcome = await engine.HandleDeadConfigAsync("dead", TestContext.Current.CancellationToken);

        Assert.True(outcome.Switched);
        Assert.True(DateTime.UtcNow - started < AutoFailoverEngine.CandidateProbeBudget + TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ProbingHappensOnceAndOnlyAfterTheRejectionChecks()
    {
        var calls = 0;
        var settings = SubscribeWith(3);
        settings.App.ConfigMode = "custom";
        var engine = Engine(settings, (s, _) => { Interlocked.Increment(ref calls); return Task.FromResult(Ok(5)); });

        var outcome = await engine.HandleDeadConfigAsync("dead", TestContext.Current.CancellationToken);

        Assert.False(outcome.Switched);
        Assert.Equal(0, calls);
    }
}
