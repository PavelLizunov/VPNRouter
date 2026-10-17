namespace VPNRouter.Headless.Protocol;

public sealed class ProtocolOutputQueue : IAsyncDisposable
{
    private readonly List<ProtocolFrame> _queue = new(ProtocolConstants.MaxOutputQueueFrames);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Stream _output;
    private readonly Action _onBackpressureOrEof;
    private readonly byte[] _newline = [(byte)'\n'];
    private readonly TimeSpan _writeTimeout;

    private TaskCompletionSource<bool>? _queueNotFullTcs;
    private bool _isCompleted;
    private bool _isStalled;
    private bool _disposed;
    private Task? _writerTask;
    private CancellationTokenSource? _writerCts;
    private Task? _inFlightWriteTask;

    public ProtocolOutputQueue(Stream output, Action onBackpressureOrEof, TimeSpan? writeTimeout = null)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _onBackpressureOrEof = onBackpressureOrEof ?? throw new ArgumentNullException(nameof(onBackpressureOrEof));
        _writeTimeout = writeTimeout ?? TimeSpan.FromSeconds(ProtocolConstants.OutputWriteTimeoutSeconds);
    }

    public void Start(CancellationToken cancellationToken)
    {
        _writerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _writerTask = Task.Run(() => WriterLoopAsync(_writerCts.Token), CancellationToken.None);
    }

    public async ValueTask<bool> EnqueueResponseAsync(ProtocolResponseFrame response, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task waitTask;
            lock (_sync)
            {
                if (_isCompleted || _disposed || _isStalled)
                    return false;

                // Responses must not disappear. If queue is full, try to evict an existing state event.
                if (_queue.Count >= ProtocolConstants.MaxOutputQueueFrames)
                {
                    int stateIndex = _queue.FindIndex(f => f is ProtocolStateEventFrame);
                    if (stateIndex >= 0)
                    {
                        _queue.RemoveAt(stateIndex);
                        _queue.Add(response);
                        return true;
                    }
                }

                if (_queue.Count < ProtocolConstants.MaxOutputQueueFrames)
                {
                    _queue.Add(response);
                    _signal.Release();
                    return true;
                }

                // Queue is full of responses. Wait for writer loop to remove a frame (read backpressure).
                if (_queueNotFullTcs == null || _queueNotFullTcs.Task.IsCompleted)
                {
                    _queueNotFullTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                waitTask = _queueNotFullTcs.Task;
            }

            await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public bool EnqueueResponse(ProtocolResponseFrame response)
    {
        lock (_sync)
        {
            if (_isCompleted || _disposed || _isStalled)
                return false;

            if (_queue.Count >= ProtocolConstants.MaxOutputQueueFrames)
            {
                int stateIndex = _queue.FindIndex(f => f is ProtocolStateEventFrame);
                if (stateIndex >= 0)
                {
                    _queue.RemoveAt(stateIndex);
                    _queue.Add(response);
                    return true;
                }

                return false;
            }

            _queue.Add(response);
            _signal.Release();
            return true;
        }
    }

    public bool EnqueueStateEvent(object stateData)
    {
        lock (_sync)
        {
            if (_isCompleted || _disposed || _isStalled)
                return false;

            // Status may coalesce: look for an existing state event in the queue and update it
            for (int i = 0; i < _queue.Count; i++)
            {
                if (_queue[i] is ProtocolStateEventFrame existing)
                {
                    existing.StateData = stateData;
                    return true;
                }
            }

            if (_queue.Count < ProtocolConstants.MaxOutputQueueFrames)
            {
                _queue.Add(new ProtocolStateEventFrame(stateData));
                _signal.Release();
                return true;
            }

            // Queue is full: state is coalesced (dropped since newer state will follow)
            return true;
        }
    }

    public bool EnqueueProgressEvent(object progressData)
    {
        lock (_sync)
        {
            if (_isCompleted || _disposed || _isStalled)
                return false;

            if (_queue.Count < ProtocolConstants.MaxOutputQueueFrames)
            {
                _queue.Add(new ProtocolProgressEventFrame(progressData));
                _signal.Release();
                return true;
            }

            // Queue is full: progress may be dropped under severe backpressure
            return false;
        }
    }

    public void Complete()
    {
        lock (_sync)
        {
            if (_isCompleted)
                return;

            _isCompleted = true;
            _queueNotFullTcs?.TrySetResult(false);
            _queueNotFullTcs = null;
            _signal.Release();
        }
    }

    private async Task WriteFramePayloadAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _output.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await _output.WriteAsync(_newline.AsMemory(), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WriterLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);

                ProtocolFrame? frame = null;
                lock (_sync)
                {
                    if (_isStalled)
                    {
                        break;
                    }

                    if (_queue.Count > 0)
                    {
                        frame = _queue[0];
                        _queue.RemoveAt(0);
                        _queueNotFullTcs?.TrySetResult(true);
                        _queueNotFullTcs = null;
                    }
                    else if (_isCompleted)
                    {
                        break;
                    }
                }

                if (frame != null)
                {
                    byte[] bytes = frame.ToUtf8Bytes();
                    CancellationTokenSource? perFrameCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    Task writeTask;
                    lock (_sync)
                    {
                        if (_isStalled)
                        {
                            perFrameCts.Dispose();
                            break;
                        }

                        var writeToken = perFrameCts.Token;
                        // Some streams block synchronously inside WriteAsync. Keep the
                        // deadline outside that invocation; never launch a second frame
                        // if this sole pending operation does not settle.
                        writeTask = Task.Run(() => WriteFramePayloadAsync(bytes, writeToken));
                        _inFlightWriteTask = writeTask;
                    }

                    bool stallTimedOut = false;
                    try
                    {
                        await writeTask.WaitAsync(_writeTimeout, cancellationToken).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        stallTimedOut = true;
                    }
                    catch
                    {
                        if (writeTask.IsCompleted)
                        {
                            perFrameCts.Dispose();
                        }
                        else
                        {
                            var ctsToDispose = perFrameCts;
                            _ = writeTask.ContinueWith(t =>
                            {
                                _ = t.Exception;
                                try
                                {
                                    ctsToDispose.Dispose();
                                }
                                catch
                                {
                                }
                            }, TaskScheduler.Default);
                        }
                        throw;
                    }

                    if (stallTimedOut)
                    {
                        try
                        {
                            perFrameCts.Cancel();
                        }
                        catch
                        {
                        }

                        lock (_sync)
                        {
                            _isCompleted = true;
                            _isStalled = true;
                            _queueNotFullTcs?.TrySetResult(false);
                            _queueNotFullTcs = null;
                        }

                        var ctsToDispose = perFrameCts;
                        perFrameCts = null;
                        _ = writeTask.ContinueWith(t =>
                        {
                            _ = t.Exception;
                            try
                            {
                                ctsToDispose.Dispose();
                            }
                            catch
                            {
                            }
                        }, TaskScheduler.Default);

                        _onBackpressureOrEof();
                        break;
                    }
                    else
                    {
                        perFrameCts.Dispose();
                        perFrameCts = null;
                        lock (_sync)
                        {
                            if (ReferenceEquals(_inFlightWriteTask, writeTask))
                            {
                                _inFlightWriteTask = null;
                            }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }
        catch (Exception)
        {
            // Stdout write error (broken pipe / EPIPE / EOF): trigger shutdown
            lock (_sync)
            {
                _isCompleted = true;
                _isStalled = true;
                _queueNotFullTcs?.TrySetResult(false);
                _queueNotFullTcs = null;
            }
            _onBackpressureOrEof();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        Complete();

        if (_writerTask != null)
        {
            try
            {
                // Allow writer task a bounded grace period to flush pending frames without deadlocking
                using var drainTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(ProtocolConstants.OutputDrainTimeoutSeconds));
                await _writerTask.WaitAsync(drainTimeoutCts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Writer hung or timed out (output deadlock / broken pipe): force cancel
                _writerCts?.Cancel();
                try
                {
                    using var cancelTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await _writerTask.WaitAsync(cancelTimeoutCts.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            if (_writerTask.IsCompleted)
            {
                _ = _writerTask.Exception; // Observe any fault
                if (_writerCts != null)
                {
                    _writerCts.Cancel();
                    _writerCts.Dispose();
                    _writerCts = null;
                }
                _signal.Dispose();
            }
            else
            {
                // Defer cleanup safely until the pending writer finishes so we do not dispose
                // the semaphore while the writer might still use it.
                var task = _writerTask;
                var signal = _signal;
                var cts = _writerCts;
                _writerCts = null;
                _ = task.ContinueWith(t =>
                {
                    _ = t.Exception; // Observe any fault
                    try
                    {
                        cts?.Dispose();
                    }
                    catch
                    {
                    }
                    try
                    {
                        signal.Dispose();
                    }
                    catch
                    {
                    }
                }, TaskScheduler.Default);
            }
        }
        else
        {
            if (_writerCts != null)
            {
                _writerCts.Cancel();
                _writerCts.Dispose();
                _writerCts = null;
            }
            _signal.Dispose();
        }

        var inFlight = _inFlightWriteTask;
        if (inFlight != null)
        {
            if (inFlight.IsCompleted)
            {
                _ = inFlight.Exception;
            }
            else
            {
                _ = inFlight.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
            }
        }
    }
}
