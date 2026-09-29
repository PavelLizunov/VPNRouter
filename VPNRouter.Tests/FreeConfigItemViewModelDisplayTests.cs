using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;
public class FreeConfigItemViewModelDisplayTests
{
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
}
