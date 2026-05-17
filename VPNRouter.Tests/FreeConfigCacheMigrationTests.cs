using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
// ═══════════════════════════════════════════════════════════════════════════════
// v2.31.5-r1 — UI-fix regression pins (no dispatcher needed)
// ═══════════════════════════════════════════════════════════════════════════════
//
// Several v2.31.x fixes touched data the App layer already exposes via plain
// .NET getters / converters / collections. Those don't need a headless Avalonia
// dispatcher, so we pin them as regular [Fact] tests in this file. Tests that
// need full XAML rendering (chevron-flip visual, F-27 .armed style) live in
// the headless suite (HeadlessGuiTests / PageScreenshotTests) where the dis-
// patcher is wired up.

public class FreeConfigCacheMigrationTests
{
    /// <summary>
    /// v2.31.3-r1 (F-25 heal-old): on cache load, any LatencyMs in [1..4]
    /// gets reset to 0 — those values were written by the pre-v2.31.2
    /// Recheck flow that skipped the plausibility gate. The migration is
    /// in-memory only (a subsequent Save persists). Test it via a synthetic
    /// CacheFile + reflection on the private static helper.
    /// </summary>
    [Fact]
    public void Load_WithCorruptedSubThresholdLatencies_ResetsToZero()
    {
        var file = new VPNRouter.Core.Services.FreeConfigs.FreeConfigCache.CacheFile
        {
            Configs = new()
            {
                MakeEntry(1),   // implausible — must heal
                MakeEntry(4),   // implausible — must heal
                MakeEntry(0),   // already zero — leave alone
                MakeEntry(5),   // threshold — keep (gate uses < 5)
                MakeEntry(42),  // plausible — keep
            },
        };

        // Invoke the private static heal helper via reflection. It mirrors
        // what FreeConfigCache.Load() does post-deserialise.
        var t = typeof(VPNRouter.Core.Services.FreeConfigs.FreeConfigCache);
        var m = t.GetMethod("HealCorruptedSubThresholdLatencies",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(m);
        m!.Invoke(null, new object[] { file });

        Assert.Equal(0, file.Configs[0].LatencyMs); // 1 → 0
        Assert.Equal(0, file.Configs[1].LatencyMs); // 4 → 0
        Assert.Equal(0, file.Configs[2].LatencyMs); // 0 → 0
        Assert.Equal(5, file.Configs[3].LatencyMs); // 5 → 5 (threshold inclusive)
        Assert.Equal(42, file.Configs[4].LatencyMs); // 42 → 42
    }

    private static VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry MakeEntry(int latency) =>
        new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Host = "1.2.3.4",
            Port = 443,
            LatencyMs = latency,
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
        };
}
