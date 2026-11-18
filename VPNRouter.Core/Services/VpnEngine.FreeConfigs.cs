using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public partial class VpnEngine
{
    private volatile bool _confirmingFreeConfig;

    internal async Task<bool> ConnectVerifiedFreeConfigAsync(AppSettings candidate, Action commitSelection,
        bool skipVpnConflictCheck, CancellationToken ct)
    {
        var policy = EffectivePolicy;
        using var scope = SingBoxRuntimePolicy.EnterScope(policy);
        policy?.Authorize(SingBoxRuntimeOperation.Start);
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (candidate.App.ConfigMode != "generated" ||
                candidate.Vless.GetActiveServers() is not [{ UseAsSingleOutbound: true } active] ||
                !string.Equals(active.Name, candidate.Vless.ActiveServer, StringComparison.OrdinalIgnoreCase))
                return false;

            var previous = _failoverSettingsContext;
            var previousHandle = _singBox?.OwnedProcessHandle;
            bool wasRunning = HasLiveOrStartingSingBox();
            if (wasRunning && (previous == null || previousHandle is not { HasExited: false }))
                return false;

            bool confirmed = false;
            _confirmingFreeConfig = true;
            try
            {
                if (wasRunning)
                {
                    // Strict generation resolves required rule sets before the owned process is replaced.
                    if (!await ApplyGatedAsync(candidate, ct, forceRestart: true, verifySelection: true).ConfigureAwait(false))
                        return false;
                    _warmupConfirmed = false;
                }
                else
                {
                    await StartSessionGatedAsync(candidate, ct, skipVpnConflictCheck).ConfigureAwait(false);
                }

                var handle = _singBox?.OwnedProcessHandle;
                if (handle is not { HasExited: false } || _sessionCts == null) return false;
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _sessionCts.Token);
                var sanity = _sanityCheck ??= new ConfigSanityCheck(_logger);
                var probe = await sanity.ProbeAsync(ParseClashApiPort(candidate.SingBox.ClashApi),
                    candidate.SingBox.ClashApiSecret, probeCts.Token).ConfigureAwait(false);
                probeCts.Token.ThrowIfCancellationRequested();
                if (probe.IsDead || handle.HasExited || !ReferenceEquals(handle, _singBox?.OwnedProcessHandle) ||
                    !ReferenceEquals(candidate, _failoverSettingsContext)) return false;

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
                    bool originalAlive = previousHandle is { HasExited: false } &&
                        ReferenceEquals(previousHandle, _singBox?.OwnedProcessHandle);
                    if (!originalAlive)
                    {
                        bool userStopped = _sessionCts?.IsCancellationRequested == true;
                        _sessionCts?.Cancel();
                        EndSession();
                        if (wasRunning && !userStopped)
                        {
                            _confirmingFreeConfig = false;
                            await StartSessionGatedAsync(previous!, CancellationToken.None, skipVpnConflictCheck).ConfigureAwait(false);
                        }
                    }
                }
            }
        }
        finally
        {
            _confirmingFreeConfig = false;
            _lifecycleGate.Release();
        }
    }
}
