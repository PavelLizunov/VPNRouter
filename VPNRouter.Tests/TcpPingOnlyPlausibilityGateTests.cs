using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;
public class TcpPingOnlyPlausibilityGateTests
{
    [Fact]
    public async Task TcpPingOnlyAsync_UnreachablePort_DoesNotMutateLatency()
    {
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Host = "127.0.0.1",
            Port = 1,
            LatencyMs = 30,
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
        };

        var tester = new VPNRouter.Core.Services.FreeConfigs.FreeConfigTester();
        await tester.TcpPingOnlyAsync(entry, TestContext.Current.CancellationToken);

        Assert.Equal(30, entry.LatencyMs);
        Assert.Equal(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            entry.Status);
    }
}
