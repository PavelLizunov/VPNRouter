using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using VPNRouter.App.Services;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class BusinessPolicyCoordinatorTests
{
    [Fact]
    public async Task Load_AndEqualRefresh_DoNotRepublishCachedApplyNowRevision()
    {
        using var fixture = new Fixture();
        var store = fixture.Store();
        var cached = store.Accept(fixture.Envelope(8, applyNow: true));
        var response = fixture.Envelope(8, applyNow: true);
        await using var coordinator = new BusinessPolicyCoordinator(fixture.Configuration, store,
            _ => Task.FromResult(response));
        var notifications = new List<BusinessPolicySnapshot>();
        long? durableRevisionAtNotification = null;
        coordinator.Accepted += policy =>
        {
            notifications.Add(policy);
            durableRevisionAtNotification = fixture.Store().Load()?.Revision;
        };

        Assert.Equal(cached.PayloadDigest, coordinator.Load()!.PayloadDigest);
        await coordinator.RefreshAsync();
        Assert.Empty(notifications);
        Assert.NotNull(coordinator.LastContactUtc);

        response = fixture.Envelope(9, empty: true);
        await coordinator.RefreshAsync();
        var accepted = Assert.Single(notifications);
        Assert.Equal(9, accepted.Revision);
        Assert.Empty(accepted.Rules);
        Assert.Equal(9, durableRevisionAtNotification);
        Assert.Same(accepted, coordinator.Current);
    }

    [Fact]
    public async Task Refresh_OfflineInvalidAndOlderResponses_RetainLastValidPolicyAndRecover()
    {
        using var fixture = new Fixture();
        var store = fixture.Store();
        store.Accept(fixture.Envelope(8));
        byte[] response = [];
        var offline = true;
        await using var coordinator = new BusinessPolicyCoordinator(fixture.Configuration, store,
            _ => offline ? Task.FromException<byte[]>(new IOException("Synthetic private failure")) : Task.FromResult(response));
        var initial = coordinator.Load();
        Assert.NotNull(initial);
        var notifications = new List<long>();
        coordinator.Accepted += policy => notifications.Add(policy.Revision);

        await coordinator.RefreshAsync();
        Assert.True(coordinator.IsUnavailable);
        Assert.Same(initial, coordinator.Current);
        Assert.Null(coordinator.LastContactUtc);

        offline = false;
        response = "not a signed policy"u8.ToArray();
        await coordinator.RefreshAsync();
        Assert.True(coordinator.IsUnavailable);
        using var untrusted = new Fixture();
        response = untrusted.Envelope(9);
        await coordinator.RefreshAsync();
        Assert.True(coordinator.IsUnavailable);
        response = fixture.Envelope(7);
        await coordinator.RefreshAsync();
        Assert.True(coordinator.IsUnavailable);
        Assert.Empty(notifications);
        Assert.Equal(8, coordinator.Current!.Revision);

        response = fixture.Envelope(9);
        await coordinator.RefreshAsync();
        Assert.False(coordinator.IsUnavailable);
        Assert.Equal(9, coordinator.Current!.Revision);
        Assert.Equal([9L], notifications);
    }

    [Fact]
    public async Task Refresh_DurableWriteFailure_DoesNotAdvertiseAcceptance()
    {
        using var fixture = new Fixture();
        var failWrite = false;
        var store = fixture.Store((source, target) =>
        {
            if (failWrite) throw new IOException("Synthetic write failure");
            File.Move(source, target, overwrite: true);
        });
        store.Accept(fixture.Envelope(8));
        var response = fixture.Envelope(9);
        await using var coordinator = new BusinessPolicyCoordinator(fixture.Configuration, store,
            _ => Task.FromResult(response));
        coordinator.Load();
        var notifications = 0;
        coordinator.Accepted += _ => notifications++;
        failWrite = true;

        await coordinator.RefreshAsync();

        Assert.True(coordinator.IsUnavailable);
        Assert.Equal(0, notifications);
        Assert.Equal(8, coordinator.Current!.Revision);
        Assert.Equal(8, fixture.Store().Load()!.Revision);
        Assert.Null(coordinator.LastContactUtc);
    }

    [Fact]
    public async Task Load_CorruptJournal_BlocksFetchUntilJournalIsRepaired()
    {
        using var fixture = new Fixture();
        var original = fixture.Store();
        original.Accept(fixture.Envelope(8));
        var saved = File.ReadAllBytes(original.JournalPath);
        File.WriteAllBytes(original.JournalPath, saved[..^1]);
        var fetches = 0;
        var response = fixture.Envelope(9);
        await using var coordinator = new BusinessPolicyCoordinator(fixture.Configuration, fixture.Store(),
            _ => { fetches++; return Task.FromResult(response); });

        Assert.Null(coordinator.Load());
        Assert.True(coordinator.IsUnavailable);
        await coordinator.RefreshAsync();
        Assert.Equal(0, fetches);
        Assert.Null(coordinator.Current);
        Assert.Equal(saved[..^1], File.ReadAllBytes(original.JournalPath));

        File.WriteAllBytes(original.JournalPath, saved);
        await coordinator.RefreshAsync();
        Assert.False(coordinator.IsUnavailable);
        Assert.Equal(1, fetches);
        Assert.Equal(9, coordinator.Current!.Revision);
    }

    [Fact]
    public async Task Refresh_ConcurrentCallsShareOneRequest_AndCallerCancellationDoesNotCancelOthers()
    {
        using var fixture = new Fixture();
        var entered = Signal();
        var release = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetches = 0;
        CancellationToken requestToken = default;
        await using var coordinator = new BusinessPolicyCoordinator(fixture.Configuration, fixture.Store(), ct =>
        {
            Interlocked.Increment(ref fetches);
            requestToken = ct;
            entered.TrySetResult();
            return release.Task;
        });

        var first = coordinator.RefreshAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = coordinator.RefreshAsync();
            using var caller = new CancellationTokenSource();
            var cancelledWait = coordinator.RefreshAsync(caller.Token);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWait);
            Assert.False(requestToken.IsCancellationRequested);
            Assert.Same(first, second);
            Assert.Equal(1, fetches);
        }
        finally { release.TrySetResult(fixture.Envelope(8)); }
        await first;
        Assert.Equal(8, coordinator.Current!.Revision);
    }

    [Fact]
    public async Task Dispose_DuringUncooperativeFetch_CancelsAndDrainsWithoutAcceptanceOrCallback()
    {
        using var fixture = new Fixture();
        var store = fixture.Store();
        var entered = Signal();
        var release = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken requestToken = default;
        await using var coordinator = new BusinessPolicyCoordinator(fixture.Configuration, store, ct =>
        {
            requestToken = ct;
            entered.TrySetResult();
            return release.Task;
        });
        var notifications = 0;
        coordinator.Accepted += _ => notifications++;
        var refresh = coordinator.RefreshAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            coordinator.Dispose();
            var drain = coordinator.DisposeAsync().AsTask();
            Assert.True(requestToken.IsCancellationRequested);
            Assert.False(drain.IsCompleted);
            Assert.False(coordinator.Completion.IsCompleted);
        }
        finally { release.TrySetResult(fixture.Envelope(8)); }
        await Task.WhenAll(refresh, coordinator.Completion).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(coordinator.Completion.IsCompletedSuccessfully);
        Assert.Equal(0, notifications);
        Assert.Null(coordinator.Current);
        Assert.False(File.Exists(store.JournalPath));
    }

    [Fact]
    public async Task Start_IsIdempotent_AndPollsAfterJitterWithoutAWindowOrVpn()
    {
        using var fixture = new Fixture();
        var clock = new ManualClock();
        var response = fixture.Envelope(8);
        var fetches = 0;
        await using var coordinator = new BusinessPolicyCoordinator(fixture.Configuration, fixture.Store(),
            _ => { Interlocked.Increment(ref fetches); return Task.FromResult(response); }, clock);
        coordinator.Start();
        coordinator.Start();
        var firstDelay = await clock.NextTimerAsync();
        Assert.InRange(firstDelay.DueTime, TimeSpan.FromSeconds(55), TimeSpan.FromSeconds(65));
        Assert.Equal(1, fetches);
        Assert.Equal(8, coordinator.Current!.Revision);

        firstDelay.Fire();
        var secondDelay = await clock.NextTimerAsync();
        Assert.InRange(secondDelay.DueTime, TimeSpan.FromSeconds(55), TimeSpan.FromSeconds(65));
        Assert.Equal(2, fetches);
        await coordinator.DisposeAsync();
        secondDelay.Fire();
        Assert.Equal(2, fetches);
        Assert.True(coordinator.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Accepted_ThrowingObserverDoesNotBlockOtherObserversOrNextRefresh()
    {
        using var fixture = new Fixture();
        var response = fixture.Envelope(8);
        await using var coordinator = new BusinessPolicyCoordinator(fixture.Configuration, fixture.Store(),
            _ => Task.FromResult(response));
        var revisions = new List<long>();
        coordinator.Accepted += _ => throw new InvalidOperationException("Synthetic observer failure");
        coordinator.Accepted += policy => revisions.Add(policy.Revision);
        await coordinator.RefreshAsync();
        response = fixture.Envelope(9);
        await coordinator.RefreshAsync();
        Assert.Equal([8L, 9L], revisions);
        Assert.False(coordinator.IsUnavailable);
    }

    [Fact]
    public async Task Accepted_DisposeFromObserver_DrainsWithoutFurtherCallbacks()
    {
        using var fixture = new Fixture();
        var response = fixture.Envelope(8);
        await using var coordinator = new BusinessPolicyCoordinator(fixture.Configuration, fixture.Store(),
            _ => Task.FromResult(response));
        var afterDispose = 0;
        coordinator.Accepted += _ => coordinator.Dispose();
        coordinator.Accepted += _ => afterDispose++;
        await coordinator.RefreshAsync();
        await coordinator.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, afterDispose);
        Assert.Equal(8, coordinator.Current!.Revision);
    }

    [Fact]
    public async Task Refresh_TwoIndependentClients_AcceptTheSamePublishedRevision()
    {
        using var firstFixture = new Fixture();
        using var secondFixture = new Fixture();
        var configuration = firstFixture.Configuration;
        var firstStore = firstFixture.Store();
        var secondStore = secondFixture.Store(configuration);
        var response = firstFixture.Envelope(8, applyNow: true);
        await using var first = new BusinessPolicyCoordinator(configuration, firstStore, _ => Task.FromResult(response));
        await using var second = new BusinessPolicyCoordinator(configuration, secondStore, _ => Task.FromResult(response));

        await Task.WhenAll(first.RefreshAsync(), second.RefreshAsync());

        Assert.NotEqual(firstStore.JournalPath, secondStore.JournalPath);
        Assert.Equal(8, first.Current!.Revision);
        Assert.Equal(first.Current.PayloadDigest, second.Current!.PayloadDigest);
        Assert.Equal(8, firstFixture.Store().Load()!.Revision);
        Assert.Equal(8, secondFixture.Store(configuration).Load()!.Revision);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ManualClock : TimeProvider
    {
        private readonly Channel<ManualTimer> _timers = Channel.CreateUnbounded<ManualTimer>();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime);
            _timers.Writer.TryWrite(timer);
            return timer;
        }

        internal Task<ManualTimer> NextTimerAsync() =>
            _timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        internal sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
        {
            private int _disposed;
            internal TimeSpan DueTime => dueTime;
            internal void Fire() { if (Volatile.Read(ref _disposed) == 0) callback(state); }
            public bool Change(TimeSpan due, TimeSpan period) => Volatile.Read(ref _disposed) == 0;
            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "business-policy-coordinator-" + Guid.NewGuid().ToString("N"));
        internal BusinessPolicyConfiguration Configuration { get; }
        internal Fixture() => Configuration = BusinessPolicyConfigurationTests.Configuration(_key);
        internal BusinessPolicyStore Store(Action<string, string>? replaceFile = null) => new(Configuration, _directory, replaceFile);
        internal BusinessPolicyStore Store(BusinessPolicyConfiguration configuration) => new(configuration, _directory);

        internal byte[] Envelope(long revision, bool empty = false, bool applyNow = false)
        {
            var rules = new[] { new { domain = "example.com", action = "proxy", comment = "Synthetic rule" } };
            if (empty) rules = [];
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = 1, policy_id = "fixture-policy", key_id = "fixture-key", revision,
                published_at = 1700000000, apply = applyNow ? "now" : "next_connection",
                rules
            });
            var signature = _key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                payload = Convert.ToBase64String(payload), signature = Convert.ToBase64String(signature)
            });
        }

        public void Dispose()
        {
            _key.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}
