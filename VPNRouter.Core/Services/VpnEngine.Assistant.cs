using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

internal sealed record AssistantRouteIntent(AcceptedTunnelInfo? Session, string? OnlyProcess);

public partial class VpnEngine
{
    internal async Task<bool> ConnectVerifiedAppRoutingAsync(AppSettings candidate, AssistantRouteIntent intent,
        Action commitSelection, bool skipVpnConflictCheck, CancellationToken ct)
    {
        bool eligible = candidate.App.ConfigMode is "generated" or "subscribe";
        // Detach a successful cold start from the assistant request's lifetime.
        using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        return await ConnectVerifiedCandidateAsync(candidate, eligible, intent, commitSelection,
            skipVpnConflictCheck, requestLifetime.Token).ConfigureAwait(false);
    }

    internal bool IsAssistantSessionCurrent(AcceptedTunnelInfo? session) =>
        ReferenceEquals(AcceptedTunnel, session) &&
        (session == null ? !HasLiveOrStartingSingBox() : HasLiveOrStartingSingBox());

    internal bool AssistantRouteMatches(AppSettings settings, string? onlyProcess) =>
        ActiveAppRoutingFingerprint == ConfigGenerator.ComputeAppRoutingFingerprint(MonitoredProcesses, settings) &&
        (onlyProcess == null || AcceptedTunnel?.RoutesOnlyApp(onlyProcess) == true);

    internal async Task<bool> RestoreAssistantSettingsAsync(AcceptedTunnelInfo? session,
        Action saveRestoredSettings, Action restoreAfterFailedStop, CancellationToken ct)
    {
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsAssistantSessionCurrent(session)) return false;
            ct.ThrowIfCancellationRequested();
            saveRestoredSettings();
            // Recovery can change the runtime after the settings write.
            if (!IsAssistantSessionCurrent(session))
            {
                restoreAfterFailedStop();
                return false;
            }
            if (session == null) return true;

            try
            {
                EndSession();
            }
            catch (InvalidOperationException)
            {
                restoreAfterFailedStop();
                return false;
            }

            _sessionCts?.Cancel();
            return true;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }
}
