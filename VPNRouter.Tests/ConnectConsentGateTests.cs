#nullable enable

using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class ConnectConsentGateTests
{
    [Fact]
    public void FirstRequest_MayOpenTheDialog()
    {
        var gate = new ConnectConsentGate();

        Assert.False(gate.IsOpen);
        Assert.True(gate.TryOpen());
        Assert.True(gate.IsOpen);
    }

    [Fact]
    public void SecondRequestWhileOpen_IsRefused()
    {
        var gate = new ConnectConsentGate();
        Assert.True(gate.TryOpen());

        Assert.False(gate.TryOpen());
        Assert.False(gate.TryOpen());
        Assert.True(gate.IsOpen);
    }

    [Fact]
    public void AfterTheResult_TheNextRequestMayOpenAgain()
    {
        var gate = new ConnectConsentGate();
        Assert.True(gate.TryOpen());

        gate.Close();

        Assert.False(gate.IsOpen);
        Assert.True(gate.TryOpen());
    }

    [Fact]
    public void Close_WithoutAnOpenDialog_IsHarmless()
    {
        var gate = new ConnectConsentGate();

        gate.Close();
        gate.Close();

        Assert.True(gate.TryOpen());
    }

    [Fact]
    public async Task ManyRacingRequests_OpenExactlyOneDialog()
    {
        var gate = new ConnectConsentGate();
        var winners = 0;

        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            if (gate.TryOpen()) Interlocked.Increment(ref winners);
        })));

        Assert.Equal(1, winners);
    }
}
