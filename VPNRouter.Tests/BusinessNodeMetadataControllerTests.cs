using System.Text.Json;
using System.Threading.Channels;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VPNRouter.App.Services;
using VPNRouter.App.ViewModels.Internals;

namespace VPNRouter.Tests;

public sealed class BusinessNodeMetadataControllerTests
{
    private static readonly Uri Endpoint = new("https://example.invalid/node-status/private-fixture");
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [AvaloniaFact]
    public async Task VisibleRefresh_IsSingleFlight_AndPublishesOnDispatcher()
    {
        using var fetch = new FetchQueue();
        var callbacksOnDispatcher = true;
        var callbacks = 0;
        using var controller = new BusinessNodeMetadataController(Endpoint, fetch.FetchAsync,
            () => { callbacks++; callbacksOnDispatcher &= Dispatcher.UIThread.CheckAccess(); });

        await controller.RefreshAsync();
        Assert.Equal(0, fetch.Calls);
        controller.SetVisible(true);
        var first = controller.RefreshAsync();
        var second = controller.RefreshAsync();
        var pending = await fetch.NextAsync();
        Assert.Equal(Endpoint, pending.Endpoint);
        Assert.False(pending.Token.IsCancellationRequested);
        Assert.Equal(1, fetch.Calls);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        var snapshot = Snapshot();
        pending.Result.SetResult(snapshot);
        await Task.WhenAll(first, second).WaitAsync(Budget);

        Assert.Same(snapshot, controller.LastSnapshot);
        Assert.True(callbacksOnDispatcher);
        Assert.True(callbacks > 0);
        Assert.Equal(1, fetch.MaximumActive);
    }

    [AvaloniaFact]
    public async Task FailedRefresh_RetainsOriginalSnapshotAndReceipt_ThenAcceptsEmptyGrant()
    {
        using var fetch = new FetchQueue();
        using var controller = new BusinessNodeMetadataController(Endpoint, fetch.FetchAsync, () => { });
        controller.SetVisible(true);
        var initial = controller.RefreshAsync();
        var snapshot = Snapshot();
        (await fetch.NextAsync()).Result.SetResult(snapshot);
        await initial.WaitAsync(Budget);
        var received = snapshot.ReceivedTimestamp;

        var failed = controller.RefreshAsync();
        (await fetch.NextAsync()).Result.SetException(new IOException("synthetic private transport failure"));
        await failed.WaitAsync(Budget);

        Assert.Same(snapshot, controller.LastSnapshot);
        Assert.Equal(received, controller.LastSnapshot!.ReceivedTimestamp);
        Assert.Equal<ulong?>(100, controller.LastSnapshot.GetAgeSeconds(snapshot.Nodes.Values.Single(), received));

        var refreshed = controller.RefreshAsync();
        var empty = Snapshot(empty: true);
        (await fetch.NextAsync()).Result.SetResult(empty);
        await refreshed.WaitAsync(Budget);
        Assert.Same(empty, controller.LastSnapshot);
        Assert.Empty(controller.LastSnapshot!.Nodes);
        Assert.Equal(1, fetch.MaximumActive);
    }

    [AvaloniaFact]
    public async Task HideShow_CancelsAndIgnoresLateResult_ThenStartsOneReplacement()
    {
        using var fetch = new FetchQueue();
        var published = new List<BusinessNodeSnapshot?>();
        BusinessNodeMetadataController? owner = null;
        using var controller = new BusinessNodeMetadataController(Endpoint, fetch.FetchAsync, () => published.Add(owner!.LastSnapshot));
        owner = controller;
        controller.SetVisible(true);
        var initial = controller.RefreshAsync();
        var original = Snapshot();
        (await fetch.NextAsync()).Result.SetResult(original);
        await initial.WaitAsync(Budget);

        var oldRound = controller.RefreshAsync();
        var abandoned = await fetch.NextAsync();
        controller.SetVisible(false);
        Assert.True(abandoned.Token.IsCancellationRequested);
        await controller.RefreshAsync();
        controller.SetVisible(true);
        controller.SetVisible(true);
        Assert.Equal(2, fetch.Calls);
        Assert.Same(original, controller.LastSnapshot);
        var received = original.ReceivedTimestamp;

        var late = Snapshot(country: "SE");
        abandoned.Result.SetResult(late);
        await oldRound.WaitAsync(Budget);
        var replacement = await fetch.NextAsync();
        Assert.Equal(3, fetch.Calls);
        Assert.Equal(1, fetch.MaximumActive);
        Assert.Same(original, controller.LastSnapshot);
        Assert.Equal(received, controller.LastSnapshot!.ReceivedTimestamp);
        Assert.DoesNotContain(late, published);
        Assert.False(replacement.Token.IsCancellationRequested);

        var newRound = controller.RefreshAsync();
        var current = Snapshot(country: "CH");
        replacement.Result.SetResult(current);
        await newRound.WaitAsync(Budget);
        Assert.Same(current, controller.LastSnapshot);
        Assert.DoesNotContain(late, published);
    }

    [AvaloniaFact]
    public async Task HiddenCompletion_DoesNotPublishOrRestart_UntilShownAgain()
    {
        using var fetch = new FetchQueue();
        var callbacks = 0;
        using var controller = new BusinessNodeMetadataController(Endpoint, fetch.FetchAsync, () => callbacks++);
        controller.SetVisible(true);
        var round = controller.RefreshAsync();
        var pending = await fetch.NextAsync();
        controller.SetVisible(false);
        var before = callbacks;
        pending.Result.SetResult(Snapshot());
        await round.WaitAsync(Budget);

        Assert.Null(controller.LastSnapshot);
        Assert.Equal(before, callbacks);
        Assert.Equal(1, fetch.Calls);
        controller.SetVisible(true);
        var nextRound = controller.RefreshAsync();
        var next = await fetch.NextAsync();
        next.Result.SetResult(Snapshot());
        await nextRound.WaitAsync(Budget);
        Assert.NotNull(controller.LastSnapshot);
        Assert.Equal(2, fetch.Calls);
    }

    [AvaloniaFact]
    public async Task Dispose_CancelsPendingWorkAndOwnerOnce_AndSuppressesLatePublication()
    {
        using var fetch = new FetchQueue();
        var callbacks = 0;
        var disposals = 0;
        using var controller = new BusinessNodeMetadataController(Endpoint, fetch.FetchAsync,
            () => callbacks++, () => disposals++);
        controller.SetVisible(true);
        var round = controller.RefreshAsync();
        var pending = await fetch.NextAsync();

        controller.Dispose();
        controller.Dispose();
        var before = callbacks;
        Assert.True(pending.Token.IsCancellationRequested);
        controller.SetVisible(true);
        await controller.RefreshAsync();
        pending.Result.SetResult(Snapshot());
        await round.WaitAsync(Budget);

        Assert.Equal(1, disposals);
        Assert.Equal(1, fetch.Calls);
        Assert.Equal(before, callbacks);
        Assert.Null(controller.LastSnapshot);
    }

    private static BusinessNodeSnapshot Snapshot(string country = "IS", bool empty = false) =>
        BusinessNodeSnapshot.Parse(JsonSerializer.Serialize(new
        {
            schema = 1, generated_at = 2000, max_age_seconds = 1320,
            nodes = Enumerable.Repeat(
                new { endpoint_host = "192.0.2.1", country_iso = country, memory_total_mib = 1024,
                    memory_available_mib = 512, load_1min_x100 = 125, observed_at = 1900,
                    age_seconds = 100, freshness = "fresh" }, empty ? 0 : 1).ToArray()
        }));

    private sealed class FetchQueue : IDisposable
    {
        private readonly Channel<Pending> _entered = Channel.CreateUnbounded<Pending>();
        private readonly List<Pending> _all = [];
        private readonly object _sync = new();
        private int _active;
        internal int Calls { get; private set; }
        internal int MaximumActive { get; private set; }

        internal async Task<BusinessNodeSnapshot> FetchAsync(Uri endpoint, CancellationToken token)
        {
            var pending = new Pending(endpoint, token);
            lock (_sync)
            {
                Calls++;
                _active++;
                MaximumActive = Math.Max(MaximumActive, _active);
                _all.Add(pending);
            }
            _entered.Writer.TryWrite(pending);
            try { return await pending.Result.Task.ConfigureAwait(false); }
            finally { lock (_sync) _active--; }
        }

        internal async Task<Pending> NextAsync() => await _entered.Reader.ReadAsync().AsTask().WaitAsync(Budget);
        public void Dispose()
        {
            lock (_sync) foreach (var pending in _all) pending.Result.TrySetCanceled();
        }
    }

    private sealed class Pending(Uri endpoint, CancellationToken token)
    {
        internal Uri Endpoint { get; } = endpoint;
        internal CancellationToken Token { get; } = token;
        internal TaskCompletionSource<BusinessNodeSnapshot> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
