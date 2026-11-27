using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public partial class VpnEngine
{
    private volatile bool _confirmingCandidate;

    internal Task<bool> ConnectVerifiedFreeConfigAsync(AppSettings candidate, Action commitSelection,
        bool skipVpnConflictCheck, CancellationToken ct)
    {
        bool eligible = candidate.App.ConfigMode == "generated" &&
            candidate.Vless.GetActiveServers() is [{ UseAsSingleOutbound: true } active] &&
            string.Equals(active.Name, candidate.Vless.ActiveServer, StringComparison.OrdinalIgnoreCase);
        return ConnectVerifiedCandidateAsync(candidate, eligible, null, commitSelection, skipVpnConflictCheck, ct);
    }

    private async Task<bool> ConnectVerifiedCandidateAsync(AppSettings candidate, bool eligible,
        AssistantRouteIntent? routeIntent, Action commitSelection, bool skipVpnConflictCheck, CancellationToken ct)
    {
        var policy = EffectivePolicy;
        using var scope = SingBoxRuntimePolicy.EnterScope(policy);
        policy?.Authorize(SingBoxRuntimeOperation.Start);
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!eligible) return false;

            var previous = _failoverSettingsContext;
            var previousImport = _healthMonitor?.AcceptedImportJson;
            var previousHandle = _singBox?.OwnedProcessHandle;
            bool wasRunning = HasLiveOrStartingSingBox();
            if (routeIntent != null && !IsAssistantSessionCurrent(routeIntent.Session))
                return RejectVerifiedCandidate("session-changed");
            if (wasRunning && (previous == null || previousHandle is not { HasExited: false }))
                return RejectVerifiedCandidate("previous-session-unconfirmed");

            bool confirmed = false;
            _confirmingCandidate = true;
            try
            {
                if (wasRunning)
                {
                    // Strict generation resolves required rule sets before the owned process is replaced.
                    if (!await ApplyGatedAsync(candidate, ct, forceRestart: true, verifySelection: true).ConfigureAwait(false))
                        return RejectVerifiedCandidate("replacement");
                    _warmupConfirmed = false;
                }
                else
                {
                    await StartSessionGatedAsync(candidate, ct, skipVpnConflictCheck,
                        deferActiveProfileSave: routeIntent != null).ConfigureAwait(false);
                }

                var handle = _singBox?.OwnedProcessHandle;
                var tunnel = AcceptedTunnel;
                if (handle == null || tunnel == null || _sessionCts == null)
                    return RejectVerifiedCandidate("startup");

                bool IsCurrent() => !handle.HasExited && ReferenceEquals(handle, _singBox?.OwnedProcessHandle)
                    && ReferenceEquals(tunnel, AcceptedTunnel) && ReferenceEquals(candidate, _failoverSettingsContext);
                if (!IsCurrent()) return RejectVerifiedCandidate("candidate-changed");
                if (routeIntent != null && !AssistantRouteMatches(candidate, routeIntent.Process, routeIntent.Direct))
                    return RejectVerifiedCandidate("app-route");
                if (routeIntent?.ExpectedServerRow is { } expectedRow &&
                    (tunnel.UsesAutoGroup || tunnel.Server.RowKey != expectedRow || tunnel.Server.FeedId != routeIntent.ExpectedFeed))
                {
                    routeIntent.OnRejection?.Invoke(AssistantCandidateRejection.ServerMismatch);
                    return RejectVerifiedCandidate("server-changed");
                }

                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _sessionCts.Token);
                var sanity = _sanityCheck ??= new ConfigSanityCheck(_logger);
                var probe = await ProbeCurrentStartAsync(IsCurrent,
                    token => tunnel.ProbeProxyAsync(sanity, token, preferHttps: routeIntent != null),
                    probeCts.Token).ConfigureAwait(false);
                probeCts.Token.ThrowIfCancellationRequested();
                if (probe == null) return RejectVerifiedCandidate("candidate-changed");
                if (probe.IsDead)
                {
                    routeIntent?.OnRejection?.Invoke(AssistantCandidateRejection.ProxyHealth);
                    return RejectVerifiedCandidate("proxy-health");
                }
                if (routeIntent != null && !AssistantRouteMatches(candidate, routeIntent.Process, routeIntent.Direct))
                    return RejectVerifiedCandidate("app-route");

                if (routeIntent?.VerifyRoute?.Invoke(tunnel) == false)
                    return RejectVerifiedCandidate("route-rules");

                commitSelection();
                confirmed = true;
                _warmupConfirmed = true;
                try { _dnsHardening.EnableLockdownIfConfigured(candidate, _logger); }
                catch (Exception) { _logger?.Warning("[VpnEngine] Deferred DNS lockdown failed"); }
                try { Connected?.Invoke(handle.Pid); }
                catch (Exception) { _logger?.Warning("[VpnEngine] Verified connection notification failed"); }
                return true;
            }
            finally
            {
                if (!confirmed)
                {
                    // Validation can reject the candidate without disturbing the original process.
                    bool originalRetained = previousHandle != null && _singBox != null &&
                        ReferenceEquals(previousHandle, _singBox.OwnedProcessHandle) &&
                        (_singBox.ExactStopUnconfirmed || previousHandle.QueryHasExited() != true);
                    if (!originalRetained)
                    {
                        bool userStopped = _sessionCts?.IsCancellationRequested == true;
                        _sessionCts?.Cancel();
                        EndSession();
                        if (wasRunning && !userStopped)
                        {
                            _confirmingCandidate = false;
                            await StartSessionGatedAsync(previous!, CancellationToken.None, skipVpnConflictCheck,
                                restoreAcceptedImport: true, sessionImportJson: previousImport,
                                deferActiveProfileSave: routeIntent != null).ConfigureAwait(false);
                        }
                    }
                }
            }
        }
        finally
        {
            _confirmingCandidate = false;
            _lifecycleGate.Release();
        }
    }

    private bool RejectVerifiedCandidate(string phase)
    {
        _logger?.Warning("[VpnEngine] Verified candidate rejected ({Phase})", phase);
        return false;
    }
}
