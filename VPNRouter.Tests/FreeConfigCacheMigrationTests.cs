using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public class FreeConfigCacheMigrationTests
{
    [Fact]
    public void Load_WithCorruptedSubThresholdLatencies_ResetsToZero()
    {
        var file = new VPNRouter.Core.Services.FreeConfigs.FreeConfigCache.CacheFile
        {
            Configs = new()
            {
                MakeEntry(1),
                MakeEntry(4),
                MakeEntry(0),
                MakeEntry(5),
                MakeEntry(42),
            },
        };

        var t = typeof(VPNRouter.Core.Services.FreeConfigs.FreeConfigCache);
        var m = t.GetMethod("HealCorruptedSubThresholdLatencies",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(m);
        m!.Invoke(null, new object[] { file });

        Assert.Equal(0, file.Configs[0].LatencyMs);
        Assert.Equal(0, file.Configs[1].LatencyMs);
        Assert.Equal(0, file.Configs[2].LatencyMs);
        Assert.Equal(5, file.Configs[3].LatencyMs);
        Assert.Equal(42, file.Configs[4].LatencyMs);
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
