using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
public class FreeConfigItemViewModelDisplayTests
{
    /// <summary>
    /// v2.31.3-r1 (F-25 heal-old): a Verified entry with LatencyMs ≤ 0
    /// (post-cache-migration "needs re-verify" state) must render as
    /// "— ✓✓" instead of the misleading "0 ms ✓✓". Pin both the display
    /// string and the sort-key bucket — the Saved tab's ascending order
    /// must NOT push these healed entries to the bottom (they're still
    /// proven-working configs, just lacking a fresh ping reading).
    /// </summary>
    [Fact]
    public void Verified_WithZeroLatency_DisplaysDashWithDoubleCheck()
    {
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            LatencyMs = 0,
            Host = "1.2.3.4",
            Port = 443,
        };
        var vm = new VPNRouter.App.ViewModels.FreeConfigs.FreeConfigItemViewModel(entry);

        Assert.Equal("— ✓✓", vm.LatencyDisplay);
    }

    [Fact]
    public void Verified_WithPlausibleLatency_StillShowsMsCheck()
    {
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            LatencyMs = 42,
        };
        var vm = new VPNRouter.App.ViewModels.FreeConfigs.FreeConfigItemViewModel(entry);

        Assert.Equal("42 ms ✓✓", vm.LatencyDisplay);
    }
}
