using System.Security.Cryptography;
using System.Text;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.App.Services;

// App lifetime owns synchronization; no window visibility or VPN state controls polling.
internal sealed class BusinessPolicyCoordinator : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly BusinessPolicyStore _store;
    private readonly BusinessPolicyStore? _phoneStore;
    private readonly BusinessPolicyHttp? _http;
    private readonly Func<CancellationToken, Task<byte[]>> _fetch;
    private readonly Func<CancellationToken, Task<byte[]?>>? _fetchPhone;
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
    private BusinessPolicySnapshot? _current;
    private readonly BusinessPolicySnapshot? _embeddedMain;
    private string? _pendingMainApply;
    private string? _pendingPhoneApply;

    internal BusinessPolicySnapshot? Current => Volatile.Read(ref _current);
    internal bool IsUnavailable { get { lock (_gate) return _unavailable; } }
    internal DateTimeOffset? LastContactUtc { get { lock (_gate) return _lastContactUtc; } }
    internal Task Completion => _completion.Task;

    // Callbacks must be nonblocking; UI owners post to their dispatcher.
    internal event Action<BusinessPolicySnapshot>? Accepted;
    internal event Action<bool>? AvailabilityChanged;

    internal BusinessPolicyCoordinator(BusinessPolicyConfiguration configuration, BusinessPolicyStore store,
        Func<CancellationToken, Task<byte[]>>? fetch = null, TimeProvider? timeProvider = null,
        Func<TimeSpan>? nextInterval = null, BusinessPolicyStore? phoneStore = null,
        Func<CancellationToken, Task<byte[]?>>? fetchPhone = null,
        IReadOnlyList<CustomRule>? embeddedRules = null)
    {
        _store = store;
        _phoneStore = phoneStore;
        if (phoneStore != null)
        {
            ArgumentNullException.ThrowIfNull(embeddedRules);
            var sites = embeddedRules.Where(rule => rule.Enabled &&
                    (rule.Type is "domain" or "domain_suffix") && !BusinessPolicyDomains.IsProtected(rule.Value))
                .Select(rule => new BusinessPolicyRule(rule.Value, rule.Action, rule.Comment)).ToArray();
            BusinessPolicyDomains.ValidateRules(sites);
            var identity = string.Join('\n', sites.Select(rule => rule.Domain + "\0" + rule.Action + "\0" + rule.Comment));
            _embeddedMain = new(configuration.PolicyId, "embedded", 0, 0, false, sites,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
        }
        _timeProvider = timeProvider ?? TimeProvider.System;
        _nextInterval = nextInterval ?? (() => TimeSpan.FromSeconds(Random.Shared.Next(55, 66)));
        if (fetch == null || (phoneStore != null && fetchPhone == null))
        {
            _http = new BusinessPolicyHttp(timeProvider: _timeProvider);
        }
        _fetch = fetch ?? (ct => _http!.FetchAsync(configuration, ct));
        if (phoneStore != null)
            _fetchPhone = fetchPhone ?? (ct => _http!.FetchPhoneAsync(configuration, ct));
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
            var main = _store.Load();
            var phone = _phoneStore?.Load()?.Phone;
            Volatile.Write(ref _current, Compose(main, phone, main?.ApplyNow == true));
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

            byte[]? envelope = null;
            byte[]? phoneEnvelope = null;
            var unavailable = false;
            try { envelope = await _fetch(_lifetime.Token).ConfigureAwait(false); }
            catch (Exception e) when (IsExpectedFailure(e)) { unavailable = true; }
            if (_fetchPhone != null && !_lifetime.IsCancellationRequested)
            {
                try { phoneEnvelope = await _fetchPhone(_lifetime.Token).ConfigureAwait(false); }
                catch (Exception e) when (IsExpectedFailure(e)) { unavailable = true; }
            }
            lock (_gate)
            {
                // A transport that finishes after cancellation cannot publish into
                // the disposed application, even if it ignored its cancellation token.
                if (_disposed || _lifetime.IsCancellationRequested) return;
                var previous = Current;
                if (envelope != null)
                {
                    try
                    {
                        _store.Accept(envelope);
                        _lastContactUtc = _timeProvider.GetUtcNow();
                    }
                    catch (Exception e) when (IsExpectedFailure(e)) { unavailable = true; }
                }
                if (_phoneStore != null)
                {
                    if (phoneEnvelope == null)
                    {
                        if (_phoneStore.Current != null) unavailable = true;
                    }
                    else
                    {
                        try { _phoneStore.Accept(phoneEnvelope); }
                        catch (Exception e) when (IsExpectedFailure(e)) { unavailable = true; }
                    }
                }
                var main = _store.Current;
                var phone = _phoneStore?.Current?.Phone;
                var mainChanged = main != null && main.PayloadDigest != previous?.PayloadDigest;
                var phoneChanged = phone?.PayloadDigest != previous?.Phone?.PayloadDigest;
                if (mainChanged) _pendingMainApply = main!.ApplyNow ? main.PayloadDigest : null;
                if (phoneChanged) _pendingPhoneApply = phone?.ApplyNow == true ? phone.PayloadDigest : null;
                var accepted = Compose(main, phone, _pendingMainApply != null || _pendingPhoneApply != null);
                SetUnavailable(unavailable);
                if (accepted != null && (previous == null || previous.EffectiveDigest != accepted.EffectiveDigest))
                {
                    Volatile.Write(ref _current, accepted);
                    Notify(Accepted, accepted);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception e) when (IsExpectedFailure(e))
        {
            lock (_gate)
                if (!_disposed) SetUnavailable(true);
        }
    }

    private BusinessPolicySnapshot? Compose(BusinessPolicySnapshot? main, BusinessPhonePolicySnapshot? phone,
        bool applyNow) => (main ?? (phone == null ? null : _embeddedMain))?.WithPhone(phone, applyNow);

    internal void AcknowledgeApplied(BusinessPolicySnapshot applied)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_pendingMainApply == applied.PayloadDigest) _pendingMainApply = null;
            if (_pendingPhoneApply == applied.Phone?.PayloadDigest) _pendingPhoneApply = null;
            if (Current is { } current)
                Volatile.Write(ref _current, current.WithPhone(current.Phone,
                    _pendingMainApply != null || _pendingPhoneApply != null));
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
