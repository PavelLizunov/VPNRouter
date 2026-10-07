namespace VPNRouter.Headless.Protocol;

public sealed class ProtocolServer : IAsyncDisposable
{
    private readonly IProtocolHandler _handler;
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly ProtocolDispatcher _dispatcher;
    private readonly TimeSpan? _outputWriteTimeout;
    private ProtocolOutputQueue? _outputQueue;

    public ProtocolServer(IProtocolHandler handler, Stream input, Stream output, TimeSpan? outputWriteTimeout = null)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _outputWriteTimeout = outputWriteTimeout;
        _dispatcher = new ProtocolDispatcher(_handler);
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        using var serverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var outputCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _outputQueue = new ProtocolOutputQueue(_output, () =>
        {
            // Backpressure, write stall, or stdout broken pipe: trigger shutdown
            try
            {
                serverCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }, _outputWriteTimeout);

        void OnStateChanged(object state) => _outputQueue.EnqueueStateEvent(state);
        void OnProgressChanged(object progress)
        {
            if (_outputQueue != null)
            {
                _dispatcher.HandleProgress(progress, _outputQueue);
            }
        }

        _handler.StateChanged += OnStateChanged;
        _handler.ProgressChanged += OnProgressChanged;

        _outputQueue.Start(outputCts.Token);

        var reader = new ProtocolLineReader(_input);
        var inFlightTasks = new HashSet<Task>();
        var inFlightLock = new object();

        try
        {
            while (!serverCts.IsCancellationRequested)
            {
                var lineResult = await reader.ReadLineAsync(serverCts.Token).ConfigureAwait(false);

                if (lineResult.Status == LineReadStatus.Eof)
                {
                    // On EOF on standard input: cancel server and active operations immediately
                    serverCts.Cancel();
                    _dispatcher.CancelActiveOperation();
                    break;
                }

                if (lineResult.Status == LineReadStatus.Oversized)
                {
                    await _outputQueue.EnqueueResponseAsync(ProtocolResponseFrame.CreateError(
                        null,
                        "payload_too_large",
                        RouterException.GetSafeMessage("payload_too_large")), serverCts.Token).ConfigureAwait(false);
                    continue;
                }

                ReadOnlyMemory<byte> lineMemory = lineResult.Memory;
                if (lineMemory.IsEmpty)
                {
                    continue;
                }

                string? extractedId = null;
                ProtocolRequest request;
                try
                {
                    request = ProtocolParser.ParseRequest(lineMemory, out extractedId);
                }
                catch (RouterException rx)
                {
                    await _outputQueue.EnqueueResponseAsync(ProtocolResponseFrame.CreateError(
                        extractedId,
                        rx.Code,
                        rx.SafeMessage), serverCts.Token).ConfigureAwait(false);
                    continue;
                }
                catch (Exception)
                {
                    await _outputQueue.EnqueueResponseAsync(ProtocolResponseFrame.CreateError(
                        extractedId,
                        "invalid_request",
                        RouterException.GetSafeMessage("invalid_request")), serverCts.Token).ConfigureAwait(false);
                    continue;
                }

                // Handler completion releases the dispatcher gate before response
                // enqueue finishes. Bound that separate population too; otherwise
                // paced asynchronous requests can accumulate outside the queue.
                bool transportFull;
                lock (inFlightLock)
                {
                    transportFull = inFlightTasks.Count >= 1 + ProtocolConstants.MaxUrgentOperations;
                }
                if (transportFull)
                {
                    await _outputQueue.EnqueueResponseAsync(ProtocolResponseFrame.CreateError(
                        request.Id, "busy", RouterException.GetSafeMessage("busy")), serverCts.Token).ConfigureAwait(false);
                    continue;
                }

                // Synchronously invoke async dispatcher so busy check occurs before first await
                var dispatchTask = _dispatcher.DispatchAsync(request, serverCts.Token);

                if (dispatchTask.IsCompleted)
                {
                    // Synchronously completed (e.g. busy rejection, cancel, synchronous snapshot/disconnect, or instant error)
                    ProtocolResponseFrame response;
                    try
                    {
                        response = await dispatchTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        continue;
                    }
                    catch (Exception)
                    {
                        response = ProtocolResponseFrame.CreateError(request.Id, "internal_error", RouterException.GetSafeMessage("internal_error"));
                    }

                    await _outputQueue.EnqueueResponseAsync(response, serverCts.Token).ConfigureAwait(false);
                }
                else
                {
                    // Truly asynchronous admitted in-flight task (ordinary operation or async disconnect/snapshot)
                    // Bounded task tracking: at most 1 ordinary + bounded urgent
                    Task? task = null;
                    lock (inFlightLock)
                    {
                        task = Task.Run(async () =>
                        {
                            try
                            {
                                var response = await dispatchTask.ConfigureAwait(false);
                                await _outputQueue.EnqueueResponseAsync(response, serverCts.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                            }
                            catch (Exception)
                            {
                                if (!serverCts.IsCancellationRequested)
                                {
                                    try
                                    {
                                        await _outputQueue.EnqueueResponseAsync(
                                            ProtocolResponseFrame.CreateError(request.Id, "internal_error", RouterException.GetSafeMessage("internal_error")),
                                            serverCts.Token).ConfigureAwait(false);
                                    }
                                    catch
                                    {
                                    }
                                }
                            }
                            finally
                            {
                                lock (inFlightLock)
                                {
                                    inFlightTasks.Remove(task!);
                                }
                            }
                        }, CancellationToken.None);
                        inFlightTasks.Add(task);
                    }
                }
            }

            // On clean EOF or loop exit: wait with bounded deadline for any in-flight requests to settle
            Task[] pending;
            lock (inFlightLock)
            {
                pending = inFlightTasks.ToArray();
            }
            if (pending.Length > 0)
            {
                try
                {
                    using var inFlightDrainCts = new CancellationTokenSource(TimeSpan.FromSeconds(ProtocolConstants.InFlightDrainTimeoutSeconds));
                    await Task.WhenAll(pending).WaitAsync(inFlightDrainCts.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Clean shutdown triggered
        }
        finally
        {
            _handler.StateChanged -= OnStateChanged;
            _handler.ProgressChanged -= OnProgressChanged;

            _dispatcher.CancelActiveOperation();

            // Ensure any remaining tasks are awaited with bounded deadline
            Task[] remaining;
            lock (inFlightLock)
            {
                remaining = inFlightTasks.ToArray();
            }
            if (remaining.Length > 0)
            {
                try
                {
                    using var remainingCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await Task.WhenAll(remaining).WaitAsync(remainingCts.Token).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            await TeardownAsync().ConfigureAwait(false);
        }

        return 0;
    }

    private readonly object _teardownSync = new();
    private Task? _teardownTask;

    private Task TeardownAsync()
    {
        lock (_teardownSync)
        {
            _teardownTask ??= PerformTeardownAsync();
            return _teardownTask;
        }
    }

    private async Task PerformTeardownAsync()
    {
        // Initiate bounded handler disposal without waiting for output drain
        var handlerDisposalTask = DisposeHandlerBoundedAsync();

        var outputDrainTask = Task.Run(async () =>
        {
            if (_outputQueue != null)
            {
                await _outputQueue.DisposeAsync().ConfigureAwait(false);
            }
        });

        await Task.WhenAll(handlerDisposalTask, outputDrainTask).ConfigureAwait(false);
    }

    private async Task DisposeHandlerBoundedAsync()
    {
        // The implementation may block before returning its ValueTask.
        var pending = Task.Run(async () => await _handler.DisposeAsync().ConfigureAwait(false));
        try
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(ProtocolConstants.HandlerDisposalTimeoutSeconds)).ConfigureAwait(false);
        }
        catch
        {
            // Keep fault observation even if an uncooperative handler outlives us.
            _ = pending.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await TeardownAsync().ConfigureAwait(false);
    }
}
