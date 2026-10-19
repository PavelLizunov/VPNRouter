using System.Text.Json;

namespace VPNRouter.Headless.Protocol;

public sealed class ProtocolDispatcher
{
    private readonly IProtocolHandler _handler;
    private readonly object _sync = new();
    private string? _activeOperationId;
    private CancellationTokenSource? _activeOperationCts;
    private int _activeUrgentCount;

    public ProtocolDispatcher(IProtocolHandler handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public bool HasActiveOrdinaryOperation
    {
        get
        {
            lock (_sync)
            {
                return _activeOperationId != null;
            }
        }
    }

    public void CancelActiveOperation()
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            cts = _activeOperationCts;
        }

        if (cts != null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void HandleProgress(object progressData, ProtocolOutputQueue? outputQueue)
    {
        if (outputQueue == null)
            return;

        string? activeId;
        lock (_sync)
        {
            activeId = _activeOperationId;
        }

        // If progress data is a dictionary or anonymous object, stamp activeId if needed
        object enriched = progressData;
        if (activeId != null && progressData is not null)
        {
            enriched = EnsureProgressId(progressData, activeId);
        }

        outputQueue.EnqueueProgressEvent(enriched);
    }

    public async Task<ProtocolResponseFrame> DispatchAsync(ProtocolRequest request, CancellationToken serverShutdownToken)
    {
        // 1. Urgent control: cancel
        if (request.Method == "cancel")
        {
            return HandleCancel(request);
        }

        // 2. Urgent control: disconnect
        if (request.Method == "disconnect")
        {
            return await HandleDisconnectAsync(request, serverShutdownToken).ConfigureAwait(false);
        }

        // All non-control methods, including snapshot, are ordinary operations.
        CancellationTokenSource operationCts;
        lock (_sync)
        {
            if (_activeOperationId != null)
            {
                // Busy error: do not queue
                return ProtocolResponseFrame.CreateError(request.Id, "busy", RouterException.GetSafeMessage("busy"));
            }

            _activeOperationId = request.Id;
            operationCts = CancellationTokenSource.CreateLinkedTokenSource(serverShutdownToken);
            _activeOperationCts = operationCts;
        }

        try
        {
            // Admission stays synchronous; only admitted work may occupy a worker.
            // ExecuteAsync may block before returning its Task (local disk/profile work).
            var result = await Task.Run(() => _handler.ExecuteAsync(request.Method, request.Params, operationCts.Token),
                CancellationToken.None).ConfigureAwait(false);
            return ProtocolResponseFrame.CreateSuccess(request.Id, result);
        }
        catch (RouterException rx)
        {
            return ProtocolResponseFrame.CreateError(request.Id, rx.Code, rx.SafeMessage);
        }
        catch (OperationCanceledException) when (operationCts.IsCancellationRequested)
        {
            return ProtocolResponseFrame.CreateError(request.Id, "cancelled", RouterException.GetSafeMessage("cancelled"));
        }
        catch (Exception)
        {
            // Sanitized error: never echo raw exception messages, URLs, or secrets
            return ProtocolResponseFrame.CreateError(request.Id, "internal_error", RouterException.GetSafeMessage("internal_error"));
        }
        finally
        {
            lock (_sync)
            {
                if (_activeOperationId == request.Id)
                {
                    _activeOperationId = null;
                    _activeOperationCts = null;
                }
            }
            operationCts.Dispose();
        }
    }

    private ProtocolResponseFrame HandleCancel(ProtocolRequest request)
    {
        if (request.Params.ValueKind != JsonValueKind.Object ||
            request.Params.EnumerateObject().Any(p => p.Name != "id"))
        {
            return ProtocolResponseFrame.CreateError(request.Id, "invalid_request", RouterException.GetSafeMessage("invalid_request"));
        }
        string? targetId = null;
        if (request.Params.ValueKind == JsonValueKind.Object &&
            request.Params.TryGetProperty("id", out var idElem) &&
            idElem.ValueKind == JsonValueKind.String)
        {
            targetId = idElem.GetString();
        }

        if (targetId == null || !ProtocolRequest.IsValidId(targetId))
        {
            return ProtocolResponseFrame.CreateError(request.Id, "invalid_request", "Missing or invalid 'id' parameter for cancel.");
        }

        CancellationTokenSource? ctsToCancel = null;
        lock (_sync)
        {
            if (_activeOperationId != null && _activeOperationId == targetId)
            {
                ctsToCancel = _activeOperationCts;
            }
        }

        bool wasCancelled = false;
        if (ctsToCancel != null)
        {
            try
            {
                ctsToCancel.Cancel();
                wasCancelled = true;
            }
            catch (ObjectDisposedException)
            {
            }
        }

        var resultDict = new Dictionary<string, object> { ["cancelled"] = wasCancelled };
        return ProtocolResponseFrame.CreateSuccess(request.Id, resultDict);
    }

    private async Task<ProtocolResponseFrame> HandleDisconnectAsync(ProtocolRequest request, CancellationToken serverShutdownToken)
    {
        // Validate before cancelling another operation or claiming an urgent slot.
        if (request.Params.ValueKind != JsonValueKind.Undefined &&
            (request.Params.ValueKind != JsonValueKind.Object || request.Params.EnumerateObject().Any()))
        {
            return ProtocolResponseFrame.CreateError(request.Id, "invalid_request", RouterException.GetSafeMessage("invalid_request"));
        }
        lock (_sync)
        {
            if (_activeUrgentCount >= ProtocolConstants.MaxUrgentOperations)
            {
                return ProtocolResponseFrame.CreateError(request.Id, "busy", RouterException.GetSafeMessage("busy"));
            }
            _activeUrgentCount++;
        }

        // Cancel any active ordinary operation immediately
        CancelActiveOperation();

        try
        {
            var result = await Task.Run(() => _handler.ExecuteAsync("disconnect", request.Params, serverShutdownToken),
                CancellationToken.None).ConfigureAwait(false);
            return ProtocolResponseFrame.CreateSuccess(request.Id, result);
        }
        catch (RouterException rx)
        {
            return ProtocolResponseFrame.CreateError(request.Id, rx.Code, rx.SafeMessage);
        }
        catch (OperationCanceledException) when (serverShutdownToken.IsCancellationRequested)
        {
            return ProtocolResponseFrame.CreateError(request.Id, "cancelled", RouterException.GetSafeMessage("cancelled"));
        }
        catch (Exception)
        {
            return ProtocolResponseFrame.CreateError(request.Id, "internal_error", RouterException.GetSafeMessage("internal_error"));
        }
        finally
        {
            lock (_sync)
            {
                _activeUrgentCount--;
            }
        }
    }

    private static object EnsureProgressId(object progressData, string activeId)
    {
        if (progressData is IDictionary<string, object> dict)
        {
            if (!dict.ContainsKey("id"))
            {
                dict["id"] = activeId;
            }
            return dict;
        }

        // Return progress data directly if not dictionary
        return progressData;
    }
}
