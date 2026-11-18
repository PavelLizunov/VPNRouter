using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public partial class VpnEngine
{
    internal static async Task<ProbeResult?> ProbeCurrentStartAsync(Func<bool> isCurrent,
        Func<CancellationToken, Task<ProbeResult>> probe, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || !isCurrent()) return null;
        var result = await probe(ct).ConfigureAwait(false);
        return !ct.IsCancellationRequested && isCurrent() ? result : null;
    }

    internal static bool ShouldAutoFailoverAfterProbe(
        bool probeIsDead, bool probeCancelled, bool warmupConfirmed)
        => probeIsDead && !probeCancelled && !warmupConfirmed;

    internal void ResetFailoverContext(AppSettings settings)
    {
        _failoverGeneration++;
        _failoverSettingsContext = settings;
        _failover = null;
    }

    internal async Task<bool> ExecuteFailoverRestartAsync(
        AppSettings captured,
        CancellationToken ct,
        long? expectedGeneration = null)
    {
        if (_postStartPhase)
            return await ExecuteProbeFailoverRestartAsync(captured, ct, expectedGeneration).ConfigureAwait(false);

        if ((expectedGeneration.HasValue && expectedGeneration.Value != _failoverGeneration) ||
            (_failoverSettingsContext is not null && !ReferenceEquals(_failoverSettingsContext, captured)))
        {
            _logger?.Information(
                "[VpnEngine] Pre-start failover restart aborted — captured settings or generation do not match active failover context (stale failover intent)");
            return false;
        }

        try
        {
            await StartAsyncInternal(captured, ct, _skipVpnConflictCheck,
                restoreAcceptedImport: true, sessionImportJson: _healthMonitor?.AcceptedImportJson).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Warning(
                "[VpnEngine] Pre-start failover restart threw inside StartAsyncInternal ({ExceptionType})", ex.GetType().Name);
            return false;
        }
    }

    internal async Task<FailoverOutcome?> HandlePostStartFailureAsync(AutoFailoverEngine failover, string reason,
        AppSettings captured, long generation, IProcessHandle? probeChild, CancellationToken ct)
    {
        try { await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        try
        {
            // Apply can roll back to a different child without changing the accepted settings or generation.
            bool CanReplace() => !ct.IsCancellationRequested && (probeChild == null || IsCurrentProbeChild(probeChild));
            if (!CanReplace() || generation != _failoverGeneration ||
                !ReferenceEquals(captured, _failoverSettingsContext) || failover.IsCurrentIntent?.Invoke() != true)
                return null;

            return await failover.HandleDeadConfigAsync(reason, ct,
                token => RestartFailoverGatedAsync(captured, token, generation, probeChild), CanReplace).ConfigureAwait(false);
        }
        finally
        {
            try { _lifecycleGate.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private bool IsCurrentProbeChild(IProcessHandle child) =>
        !_warmupConfirmed && ReferenceEquals(child, _singBox?.OwnedProcessHandle) && child.QueryHasExited() == false;

    internal async Task<bool> ExecuteProbeFailoverRestartAsync(
        AppSettings captured,
        CancellationToken probeCt,
        long? expectedGeneration = null)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try { return await RestartFailoverGatedAsync(captured, probeCt, expectedGeneration, null).ConfigureAwait(false); }
        finally
        {
            try { _lifecycleGate.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task<bool> RestartFailoverGatedAsync(AppSettings captured, CancellationToken probeCt,
        long? expectedGeneration, IProcessHandle? probeChild)
    {
        bool preserveCurrentSession = true;

        void RetireCurrentTunnel()
        {
            probeCt.ThrowIfCancellationRequested();
            if (probeChild != null && !IsCurrentProbeChild(probeChild))
                throw new OperationCanceledException("The originating probe is no longer current.");
            if (_singBox is { } current)
            {
                bool Retire() => current.TryRetireForReplacement(probeChild, probeCt);
                bool retired = _healthMonitor?.TryRetireForFailover(Retire) ?? Retire();
                if (!retired)
                    throw new InvalidOperationException("The current tunnel stop was not confirmed.");
            }
            preserveCurrentSession = false;
            TeardownInternal();
        }

        try
        {
            if (probeCt.IsCancellationRequested || (probeChild != null && !IsCurrentProbeChild(probeChild)) ||
                (expectedGeneration.HasValue && expectedGeneration.Value != _failoverGeneration) ||
                (_failoverSettingsContext is not null && !ReferenceEquals(_failoverSettingsContext, captured)))
            {
                _logger?.Information(
                    "[VpnEngine] Failover restart aborted — captured settings or generation do not match active failover context (stale failover intent)");
                return false;
            }

            var acceptedImport = _healthMonitor?.AcceptedImportJson;
            bool validateBeforeRetiring = HasLiveOrStartingSingBox();
            if (!validateBeforeRetiring)
                RetireCurrentTunnel();

            var session = _sessionCts;
            if (session == null || session.IsCancellationRequested)
            {
                _logger?.Information(
                    "[VpnEngine] Failover restart aborted — session cancelled (user disconnect)");
                return false;
            }
            var settings = captured;
            Action? retireAfterValidation = null;
            if (validateBeforeRetiring)
            {
                // Resolution can normalize subscription rows and runtime settings in place.
                settings = CopySessionSettings(captured);
                retireAfterValidation = RetireCurrentTunnel;
            }
            await StartAsyncInternal(settings, session.Token, _skipVpnConflictCheck, retireAfterValidation,
                restoreAcceptedImport: true, sessionImportJson: acceptedImport).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger?.Information(
                "[VpnEngine] Failover restart cancelled mid-flight by user disconnect — not resurrecting");
            return false;
        }
        catch (ObjectDisposedException)
        {
            _logger?.Information(
                "[VpnEngine] Failover restart aborted — engine disposed during shutdown");
            return false;
        }
        catch (Exception ex) when (preserveCurrentSession)
        {
            _logger?.Warning("[VpnEngine] Rejected automatic replacement before teardown ({ExceptionType})", ex.GetType().Name);
            OnStatus("Automatic failover rejected before tunnel replacement.");
            return false;
        }
    }
}
