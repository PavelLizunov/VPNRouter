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

        var recreated = new ConnectConsentGate(saved.Owner, saved.Pending);
        Assert.Null(recreated.ClaimPending(true, id => id == current));
        var resultOwner = recreated.ResolveResult(id => id == current);
        Assert.Null(resultOwner); // Neither grant nor denial belongs to the later Connect.
        var next = recreated.Requests.Pending;
        Assert.Equal(current == "latest" ? current : null, next);
        Assert.False(recreated.IsOpen);
        if (next != null)
        {
            Assert.Equal(next, recreated.ClaimPending(true, id => id == current));
            Assert.True(recreated.OpenOrQueue(next));
            Assert.Equal(next, recreated.ResolveResult(id => id == next));
        }
    }

    [Fact]
    public void RecreationAfterResult_RetriesPendingConnectExactlyOnceOnTheCurrentActivity()
    {
        static bool IsCurrent(string? id) => id == "queued";
        var original = new ConnectConsentGate();
        Assert.True(original.OpenOrQueue("opening"));
        Assert.False(original.OpenOrQueue("queued"));
        Assert.Null(original.ResolveResult(IsCurrent));

        Assert.Null(original.ClaimPending(false, IsCurrent));
        var saved = original.Requests;
        Assert.Null(saved.Owner);
        Assert.Equal("queued", saved.Pending);
        var recreated = new ConnectConsentGate(saved.Owner, saved.Pending);
        Assert.Null(recreated.ResolveResult(IsCurrent));
        var retry = recreated.ClaimPending(true, IsCurrent);
        Assert.Equal("queued", retry);
        Assert.Null(recreated.ClaimPending(true, IsCurrent));

        Assert.True(recreated.OpenOrQueue(retry!));
        Assert.Null(recreated.ClaimPending(true, IsCurrent));
        Assert.Equal(retry, recreated.ResolveResult(IsCurrent));
        Assert.Null(recreated.ClaimPending(true, IsCurrent));
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("newer-connect")]
    public void SupersededWhileWaitingToResume_DiscardsTheSavedRetry(string current)
    {
        var gate = new ConnectConsentGate();
        Assert.True(gate.OpenOrQueue("opening"));
        Assert.False(gate.OpenOrQueue("queued"));
        Assert.Null(gate.ResolveResult(id => id == "queued"));
        var saved = gate.Requests;

        var recreated = new ConnectConsentGate(saved.Owner, saved.Pending);
        Assert.Null(recreated.ClaimPending(true, id => id == current));
        Assert.Null(recreated.Requests.Pending);
        Assert.Null(recreated.ClaimPending(true, id => id == current));
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
