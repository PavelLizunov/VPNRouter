using System.Diagnostics;
using Avalonia.Threading;
using VPNRouter.App.Services;

namespace VPNRouter.App.ViewModels.Internals;

// Window-owned read-only metadata. It neither subscribes to connection state nor
// touches the backing server pool, selection, settings, health probes or reports.
internal sealed class BusinessNodeMetadataController : IDisposable
{
    private readonly Uri _endpoint;
    private readonly Func<Uri, CancellationToken, Task<BusinessNodeSnapshot>> _fetch;
    private readonly Action _changed;
    private readonly Action? _dispose;
    private readonly TimeSpan _refreshInterval;
    private readonly DispatcherTimer _timer;
    private Round? _round;
    private long? _lastAttempt;
    private bool _visible, _disposed;

    internal BusinessNodeSnapshot? LastSnapshot { get; private set; }

    internal BusinessNodeMetadataController(Uri endpoint, Action changed)
        : this(RequireEndpoint(endpoint), new BusinessNodeMetadataClient(), changed) { }

    private BusinessNodeMetadataController(Uri endpoint, BusinessNodeMetadataClient client, Action changed)
        : this(endpoint, client.FetchAsync, changed, client.Dispose) { }

    internal BusinessNodeMetadataController(Uri endpoint,
        Func<Uri, CancellationToken, Task<BusinessNodeSnapshot>> fetch, Action changed,
        Action? dispose = null, TimeSpan? refreshInterval = null)
    {
        _endpoint = RequireEndpoint(endpoint);
        _fetch = fetch;
        _changed = changed;
        _dispose = dispose;
        _refreshInterval = refreshInterval ?? TimeSpan.FromMinutes(5);
        if (_refreshInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(refreshInterval));
        _timer = new DispatcherTimer
        {
            Interval = _refreshInterval < TimeSpan.FromSeconds(30) ? _refreshInterval : TimeSpan.FromSeconds(30)
        };
        _timer.Tick += OnTick;
    }

    internal void SetVisible(bool visible)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed || _visible == visible) return;
        _visible = visible;
        if (!visible)
        {
            _timer.Stop();
            _round?.Cancellation.Cancel();
            return;
        }
        _timer.Start();
        if (LastSnapshot != null) _changed();
        _ = RefreshAsync();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed || !_visible) return;
        // Re-evaluate age from the original snapshot; notification never resets it.
        if (LastSnapshot != null) _changed();
        if (_lastAttempt == null || Stopwatch.GetElapsedTime(_lastAttempt.Value) >= _refreshInterval)
            _ = RefreshAsync();
    }

    internal Task RefreshAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed || !_visible) return Task.CompletedTask;
        if (_round != null) return _round.Completion.Task;
        var round = new Round();
        _round = round;
        _lastAttempt = Stopwatch.GetTimestamp();
        _ = RunRoundAsync(round);
        return round.Completion.Task;
    }

    private async Task RunRoundAsync(Round round)
    {
        BusinessNodeSnapshot? snapshot = null;
        try
        {
            snapshot = await _fetch(_endpoint, round.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (round.Cancellation.IsCancellationRequested) { }
        catch
        {
            // Keep the last valid snapshot and its age. No endpoint or raw failure
            // is logged or passed through to the window.
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var cancelled = round.Cancellation.IsCancellationRequested;
                if (ReferenceEquals(_round, round)) _round = null;
                try
                {
                    if (!_disposed && _visible && !cancelled)
                    {
                        if (snapshot != null) LastSnapshot = snapshot;
                        _changed();
                    }
                }
                finally
                {
                    round.Cancellation.Dispose();
                    round.Completion.TrySetResult();
                    // A show/context swap may have cancelled work that ignores its
                    // token. It must finish before the replacement starts.
                    if (cancelled && !_disposed && _visible) _ = RefreshAsync();
                }
            });
        }
    }

    private static Uri RequireEndpoint(Uri endpoint)
    {
        if (endpoint == null || !endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps ||
            endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0 ||
            !BusinessNodeSnapshot.TryNormalizeHost(endpoint.IdnHost, out _))
            throw new InvalidDataException("Node metadata service must use HTTPS.");
        return endpoint;
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _visible = false;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _round?.Cancellation.Cancel();
        _dispose?.Invoke();
    }

    private sealed class Round
    {
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
