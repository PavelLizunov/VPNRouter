using VPNRouter.Android;

namespace VPNRouter.Tests;

public class AndroidVerificationSessionTests
{
    [Fact]
    public async Task CancelDuringStartup_WakesItsWaitAndSkipsTheProbe()
    {
        using var source = new CancellationTokenSource();
        using var stopRequested = new ManualResetEventSlim();
        var starting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probed = false;
        var closed = false;

        var attempt = Task.Run(() => AndroidVerificationSession.RunAsync(
            () =>
            {
                starting.SetResult();
                if (!stopRequested.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                throw new InvalidOperationException("Java wait interrupted");
            },
            stopRequested.Set, () => closed = true, () =>
            {
                probed = true;
                return Task.FromResult(true);
            }, source.Token));

        await starting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
        Assert.Equal(source.Token, error.CancellationToken);
        Assert.True(closed);
        Assert.False(probed);
    }

    [Fact]
    public async Task CancelDuringFinalClose_DiscardsTheSuccessfulProbe()
    {
        using var source = new CancellationTokenSource();
        var stopRequested = false;
        var probeFinished = false;

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AndroidVerificationSession.RunAsync(
            () => { }, () => stopRequested = true, () =>
            {
                Assert.True(probeFinished);
                source.Cancel();
            }, () =>
            {
                probeFinished = true;
                return Task.FromResult(true);
            }, source.Token));

        Assert.True(stopRequested);
        Assert.Equal(source.Token, error.CancellationToken);
    }
}
