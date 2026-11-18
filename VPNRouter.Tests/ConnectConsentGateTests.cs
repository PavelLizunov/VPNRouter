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

    [Theory]
    [InlineData("latest")]
    [InlineData("another-stop")]
    public void RecreatedDialogDropsSupersededResultAndRechecksPendingConnect(string current)
    {
        var firstActivity = new ConnectConsentGate();
        Assert.True(firstActivity.OpenOrQueue("before-stop"));
        Assert.False(firstActivity.OpenOrQueue("next"));
        Assert.False(firstActivity.OpenOrQueue("latest"));
        var saved = firstActivity.Requests;

        var recreated = new ConnectConsentGate();
        Assert.True(recreated.OpenOrQueue(saved.Owner!));
        Assert.False(recreated.OpenOrQueue(saved.Pending!));
        var (resultOwner, next) = recreated.ResolveResult(id => id == current);
        Assert.Null(resultOwner); // Neither grant nor denial belongs to the later Connect.
        Assert.Equal(current == "latest" ? current : null, next);
        Assert.False(recreated.IsOpen);
        if (next != null)
        {
            Assert.True(recreated.OpenOrQueue(next));
            Assert.Equal(next, recreated.ResolveResult(id => id == next).Owner);
        }
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
