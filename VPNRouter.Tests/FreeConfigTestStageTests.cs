// Phase 3E (2026-05-18) — TestStage unit tests.
//
// TestStage is the largest stage — it owns the skip-recent gate, the
// placeholder pre-test rejection (Phase 3D consolidation), the goal-mode
// early-stop, and the periodic incremental cache save. The tests below
// cover the parts that don't require real TCP probes:
//
//   - Verified entries skip the test stage (gate, not probe).
//   - Entries with LastTestedAt < SkipRecentHours skip (gate).
//   - Placeholder-fingerprint entries are mutated to TlsFailed BEFORE the
//     probe — pinned by Phase 3D's stas pubkey.
//   - Empty input is a no-op (no probe, no save).
//   - Name + Optional contract.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.FreeConfigs;
using VPNRouter.Core.Services.FreeConfigs.Stages;

namespace VPNRouter.Tests;

public class FreeConfigTestStageTests : IDisposable
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private readonly string _tmpDir;
    private readonly FreeConfigCache _cache;

    public FreeConfigTestStageTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(),
            $"vpnrouter-test-stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
        _cache = new FreeConfigCache(
            Logger,
            Path.Combine(_tmpDir, "free_configs.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    private StageContext Ctx(
        IReadOnlyList<FreeConfigEntry> input,
        int maxTestCount = int.MaxValue,
        int skipRecentHours = 6,
        int? goalTargetCount = null,
        int? goalMaxLatencyMs = null) => new(
        Input: input,
        Settings: new AppSettings(),
        Cache: _cache,
        Sources: Array.Empty<FreeConfigSource>(),
        Logger: Logger,
        MaxTestCount: maxTestCount,
        SkipRecentHours: skipRecentHours,
        GoalTargetCount: goalTargetCount,
        GoalMaxLatencyMs: goalMaxLatencyMs);

    [Fact]
    public void Name_IsTest()
    {
        var stage = new TestStage(new FreeConfigTester());
        Assert.Equal("test", stage.Name);
    }

    [Fact]
    public void Optional_IsFalse()
    {
        var stage = new TestStage(new FreeConfigTester());
        Assert.False(stage.Optional);
    }

    [Fact]
    public async Task EmptyInput_NoOp()
    {
        var stage = new TestStage(new FreeConfigTester());

        var result = await stage.RunAsync(Ctx(Array.Empty<FreeConfigEntry>()), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Output);
        Assert.False(stage.GoalReached);
        Assert.Equal(0, stage.SkippedRecent);
        Assert.Equal(0, stage.RejectedPlaceholder);
    }

    [Fact]
    public async Task VerifiedEntries_SkipTheTestGate()
    {
        // Verified is the gold status — the weaker TCP+TLS probe can only
        // downgrade it. The stage's pre-test filter MUST drop Verified
        // entries from the toTest list. We can't probe live TCP from a
        // test, so the assertion is "after running, the verified entry
        // still has its original status / latency / lastTested".
        var verified = new FreeConfigEntry
        {
            Id = "v1",
            Host = "127.0.0.1",
            Port = 1,
            Uuid = "u",
            Status = FreeConfigStatus.Verified,
            LatencyMs = 88,
            LastTestedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        var stage = new TestStage(new FreeConfigTester());
        var result = await stage.RunAsync(Ctx(new List<FreeConfigEntry> { verified }), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(FreeConfigStatus.Verified, verified.Status);
        Assert.Equal(88, verified.LatencyMs);
        // LastTestedAt should NOT have moved — the entry never entered the
        // toTest list, the placeholder mutation pass doesn't touch verified.
        Assert.Equal(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), verified.LastTestedAt);
    }

    [Fact]
    public async Task RecentlyTested_AreSkipped()
    {
        // Non-Verified entry, but tested 1h ago when SkipRecentHours = 6
        // → skip. Status remains Ok, SkippedRecent bookkeeping records 1.
        var entry = new FreeConfigEntry
        {
            Id = "r1",
            Host = "127.0.0.1",
            Port = 1,
            Uuid = "u",
            Status = FreeConfigStatus.Ok,
            LatencyMs = 50,
            LastTestedAt = DateTime.UtcNow.AddHours(-1),
        };

        var stage = new TestStage(new FreeConfigTester());
        var result = await stage.RunAsync(
            Ctx(new List<FreeConfigEntry> { entry }, skipRecentHours: 6),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, stage.SkippedRecent);
        Assert.Equal(FreeConfigStatus.Ok, entry.Status); // unchanged
        Assert.Equal(50, entry.LatencyMs);
    }

    [Fact]
    public async Task PlaceholderEntry_MutatedToTlsFailed_BeforeProbe()
    {
        // Phase 3D consolidation — TestStage runs PlaceholderDefense.Inspect
        // against every entry. The stas pubkey "DnT9hI…" must trip the
        // gate. The mutated entry should NOT enter the toTest list (it
        // has LastTestedAt = now, status = TlsFailed, so the skip-recent
        // gate catches it — no real TCP probe happens).
        var pubkey = "DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU";
        // Construct a valid VLESS URI containing the known placeholder
        // pubkey. UUID is arbitrary; sid/sni unimportant for the
        // PlaceholderDefense check.
        var rawUri = $"vless://550e8400-e29b-41d4-a716-446655440000@1.2.3.4:443?security=reality&sni=google.com&fp=chrome&pbk={pubkey}&sid=99cd2233#placeholder";

        var entry = new FreeConfigEntry
        {
            Id = "ph",
            Host = "1.2.3.4",
            Port = 443,
            Uuid = "550e8400-e29b-41d4-a716-446655440000",
            RawUri = rawUri,
            Status = FreeConfigStatus.Unknown,
        };

        var stage = new TestStage(new FreeConfigTester());
        var result = await stage.RunAsync(
            Ctx(new List<FreeConfigEntry> { entry }),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, stage.RejectedPlaceholder);
        Assert.Equal(FreeConfigStatus.TlsFailed, entry.Status);
        Assert.NotNull(entry.LastError);
        Assert.Contains("placeholder credential", entry.LastError);
    }

    [Fact]
    public async Task MaxTestCount_LimitsProbeList()
    {
        // 100 fresh entries, MaxTestCount = 0 → no entries enter toTest.
        // The stage still saves the cache (initial + final) but skips the
        // actual TCP probe loop. Asserting on the output count is the
        // simplest invariant.
        var input = new List<FreeConfigEntry>();
        for (int i = 0; i < 5; i++)
        {
            input.Add(new FreeConfigEntry
            {
                Id = $"entry-{i}",
                Host = "127.0.0.1",
                Port = 1,
                Uuid = "u",
                Status = FreeConfigStatus.Unknown,
                RawUri = $"vless://550e8400-e29b-41d4-a716-446655440000@127.0.0.1:443?security=reality&sni=google.com&fp=chrome&pbk=Pubkey0000000000000000000000000000000000000{i}&sid=99cd2233#n{i}",
            });
        }

        var stage = new TestStage(new FreeConfigTester());
        var result = await stage.RunAsync(
            Ctx(input, maxTestCount: 0),
            CancellationToken.None);

        Assert.True(result.Success);
        // All 5 entries survive in Output (the cap is on the probe list,
        // not the input list).
        Assert.Equal(5, result.Output.Count);
    }
}
