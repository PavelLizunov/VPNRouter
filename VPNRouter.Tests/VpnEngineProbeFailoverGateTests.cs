using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class VpnEngineProbeFailoverGateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServiceSettingsReceipt_AReplacedStartupCannotSendItsCredentialOrActOnALateVerdict(bool inFlight)
    {
        bool current = inFlight;
        int requests = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<ProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = VpnEngine.ProbeCurrentStartAsync(() => current, _ =>
        {
            requests++;
            entered.SetResult();
            return response.Task;
        }, TestContext.Current.CancellationToken);
        if (inFlight)
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            current = false;
            response.SetResult(new ProbeResult(true, "fixture", 0));
        }
        Assert.Null(await result);
        Assert.Equal(inFlight ? 1 : 0, requests);
    }

    [Fact]
    public void DeadProbe_NoWarmupConfirm_FailsOver()
        => Assert.True(VpnEngine.ShouldAutoFailoverAfterProbe(
            probeIsDead: true, probeCancelled: false, warmupConfirmed: false));
}
