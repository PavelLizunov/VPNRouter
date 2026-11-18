using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public partial class VpnEngine
{
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
            await StartAsyncInternal(captured, ct, _skipVpnConflictCheck).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Warning(
                "[VpnEngine] Pre-start failover restart threw inside StartAsyncInternal ({ExceptionType})", ex.GetType().Name);
            if (!IsRunning)
            {
                try { TeardownInternal(); } catch { }
            }
            return false;
        }
    }

    internal async Task<bool> ExecuteProbeFailoverRestartAsync(
        AppSettings captured,
        CancellationToken probeCt,
        long? expectedGeneration = null)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        bool validatingLiveReplacement = false;
        try
        {
            if ((expectedGeneration.HasValue && expectedGeneration.Value != _failoverGeneration) ||
                (_failoverSettingsContext is not null && !ReferenceEquals(_failoverSettingsContext, captured)))
            {
                _logger?.Information(
                    "[VpnEngine] Failover restart aborted — captured settings or generation do not match active failover context (stale failover intent)");
                return false;
            }

            validatingLiveReplacement = HasActiveTunnel;
            if (!validatingLiveReplacement)
                TeardownInternal();

            var session = _sessionCts;
            if (session == null || session.IsCancellationRequested)
            {
                _logger?.Information(
                    "[VpnEngine] Failover restart aborted — session cancelled (user disconnect)");
                return false;
            }
            var settings = captured;
            Action? retireAfterValidation = null;
            if (validatingLiveReplacement)
            {
                // Resolution can normalize subscription rows and runtime settings in place.
                settings = SettingsLoader.Copy(captured);
                settings.BusinessPolicy = captured.BusinessPolicy;
                retireAfterValidation = () =>
                {
                    validatingLiveReplacement = false;
                    TeardownInternal();
                };
            }
            await StartAsyncInternal(settings, session.Token, _skipVpnConflictCheck, retireAfterValidation).ConfigureAwait(false);
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
        catch (Exception ex) when (validatingLiveReplacement)
        {
            _logger?.Warning("[VpnEngine] Rejected automatic replacement before teardown ({ExceptionType})", ex.GetType().Name);
            OnStatus("Automatic failover rejected before tunnel replacement.");
            return false;
        }
        catch (Exception ex)
        {
            if (!HasActiveTunnel)
            {
                _logger?.Warning(
                    "[VpnEngine] Failover restart failed to bring up replacement, tearing down partial state ({ExceptionType})", ex.GetType().Name);
                try { TeardownInternal(); } catch { }
            }
            else
            {
                _logger?.Warning(
                    "[VpnEngine] Failover restart threw after sing-box came up, leaving live tunnel for Stop/Dispose ({ExceptionType})", ex.GetType().Name);
            }
            throw;
        }
        finally
        {
            try { _lifecycleGate.Release(); } catch (ObjectDisposedException) { }
        }
    }
}
