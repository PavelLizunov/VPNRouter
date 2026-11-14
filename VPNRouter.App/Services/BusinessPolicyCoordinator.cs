using VPNRouter.Core.Services;

namespace VPNRouter.App.Services;

// App lifetime owns synchronization; no window visibility or VPN state controls polling.
internal sealed class BusinessPolicyCoordinator : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly BusinessPolicyStore _store;
    private readonly BusinessPolicyHttp? _http;
    private readonly Func<CancellationToken, Task<byte[]>> _fetch;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan> _nextInterval;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _polling;
    private Task? _refresh;
    private bool _loaded;
    private volatile bool _disposed;
    private bool _unavailable;
    private DateTimeOffset? _lastContactUtc;

    internal BusinessPolicySnapshot? Current => _store.Current;
    internal bool IsUnavailable { get { lock (_gate) return _unavailable; } }
    internal DateTimeOffset? LastContactUtc { get { lock (_gate) return _lastContactUtc; } }
    internal Task Completion => _completion.Task;

    // Callbacks must be nonblocking; UI owners post to their dispatcher.
    internal event Action<BusinessPolicySnapshot>? Accepted;
    internal event Action<bool>? AvailabilityChanged;

    internal BusinessPolicyCoordinator(BusinessPolicyConfiguration configuration, BusinessPolicyStore store,
        Func<CancellationToken, Task<byte[]>>? fetch = null, TimeProvider? timeProvider = null,
        Func<TimeSpan>? nextInterval = null)
    {
        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _nextInterval = nextInterval ?? (() => TimeSpan.FromSeconds(Random.Shared.Next(55, 66)));
        if (fetch == null)
        {
            _http = new BusinessPolicyHttp(timeProvider: _timeProvider);
            _fetch = ct => _http.FetchAsync(configuration, ct);
        }
        else _fetch = fetch;
    }

    // Bootstrap reads the journal before composing effective rules. Loading an old
    // apply-now revision is not a new publication and must not request a reconnect.
    internal BusinessPolicySnapshot? Load()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            TryLoad();
            return Current;
        }
    }

    internal void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _polling ??= Task.Run(PollAsync);
        }
    }

    // Concurrent requests share one operation. Caller cancellation abandons only
    // that wait; it cannot cancel the app-owned request for other consumers.
    internal Task RefreshAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
            if (_refresh == null || _refresh.IsCompleted)
                _refresh = Task.Run(RefreshCoreAsync);
            return ct.CanBeCanceled ? _refresh.WaitAsync(ct) : _refresh;
        }
    }

    private bool TryLoad()
    {
        try
        {
            _store.Load();
            _loaded = true;
            SetUnavailable(false);
            return true;
        }
        catch (Exception e) when (IsExpectedFailure(e))
        {
            _loaded = false;
            SetUnavailable(true);
            return false;
        }
    }

    private async Task RefreshCoreAsync()
    {
        try
        {
            lock (_gate)
            {
                if (_disposed) return;
                if (!_loaded && !TryLoad()) return;
                if (_disposed) return;
            }

            var envelope = await _fetch(_lifetime.Token).ConfigureAwait(false);
            lock (_gate)
            {
                // A transport that finishes after cancellation cannot publish into
                // the disposed application, even if it ignored its cancellation token.
                if (_disposed || _lifetime.IsCancellationRequested) return;
                var previous = Current;
                var accepted = _store.Accept(envelope);
                _lastContactUtc = _timeProvider.GetUtcNow();
                SetUnavailable(false);
                if (previous == null || previous.Revision != accepted.Revision ||
                    previous.PayloadDigest != accepted.PayloadDigest)
                    Notify(Accepted, accepted);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception e) when (IsExpectedFailure(e))
        {
            lock (_gate)
                if (!_disposed) SetUnavailable(true);
        }
    }

    private async Task PollAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await RefreshAsync().ConfigureAwait(false);
                var interval = _nextInterval();
                if (interval <= TimeSpan.Zero || interval > TimeSpan.FromMinutes(5))
                    throw new InvalidOperationException("Invalid Business policy polling interval.");
                await Task.Delay(interval, _timeProvider, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_disposed) { }
        catch (Exception e) when (IsExpectedFailure(e))
        {
            lock (_gate)
                if (!_disposed) SetUnavailable(true);
        }
    }

    private void SetUnavailable(bool value)
    {
        if (_unavailable == value) return;
        _unavailable = value;
        Notify(AvailabilityChanged, value);
    }

    private void Notify<T>(Action<T>? handlers, T value)
    {
        if (handlers == null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<T>>())
        {
            if (_disposed) return;
            try { handler(value); } catch { /* An observer cannot break durable synchronization. */ }
        }
    }

    private static bool IsExpectedFailure(Exception e) => e is InvalidDataException or IOException or UnauthorizedAccessException or
        System.Security.SecurityException or System.Security.Cryptography.CryptographicException or
        HttpRequestException or OperationCanceledException or ArgumentException or InvalidOperationException or
        NotSupportedException;

    public void Dispose()
    {
        Task polling;
        Task refresh;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            polling = _polling ?? Task.CompletedTask;
            refresh = _refresh ?? Task.CompletedTask;
            Accepted = null;
            AvailabilityChanged = null;
        }
        // Cancellation callbacks run outside our lock. Resources remain alive
        // until the loop and its last request have both observed shutdown.
        try { _lifetime.Cancel(); } catch (AggregateException) { }
        _ = DrainAsync(polling, refresh);
    }

    private async Task DrainAsync(Task polling, Task refresh)
    {
        try { await Task.WhenAll(polling, refresh).ConfigureAwait(false); }
        catch { /* Shutdown still has to release owned resources after a task fault. */ }
        finally
        {
            try { _http?.Dispose(); }
            finally
            {
                _lifetime.Dispose();
                _completion.TrySetResult();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await Completion.ConfigureAwait(false);
    }
}
