using VPNRouter.App.Services;
using VPNRouter.Core.Services;
using Strings = VPNRouter.Core.Localization.Strings;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    private CancellationTokenSource? _ninaZapretRequest;

    internal async Task<string> RunNinaZapretAsync(
        string command, IProgress<string> progress, CancellationToken ct)
    {
        if (_disposed || SafeMode.Enabled || _ninaZapretRequest != null)
            return Strings.NinaZapretBusy;

        string? subscription = null;
        if (command == NinaZapretClient.Discord)
        {
            if (!IsZapretMagicButtonEnabled || IsApplying || _isReconnecting)
                return Strings.NinaZapretBusy;
            if (ZapretEnabled || IsZapretRunning() || ProcessQuery.AnyAlive("winws"))
                return Strings.NinaZapretEngineConflict;
            if (IsConnected || IsConnecting || _displayExternalTunnel)
                return Strings.NinaZapretVpnActive;
            if (ReadAssistantSnapshot() is not { } snapshot) return Strings.AssistantSettingsChanged;
            subscription = SelectAssistantSubscription(snapshot.Settings.App)?.Url;
            if (subscription == null) return Strings.AssistantSubscription;
        }

        using var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ninaZapretRequest = request;
        OnPropertyChanged(nameof(IsZapretMagicButtonEnabled));
        try
        {
            var result = await NinaZapretClient.RunAsync(command, subscription, progress, request.Token);
            return result.Code switch
            {
                "repaired" when result.Profile is { } profile => Strings.NinaZapretRepaired(profile),
                "already_working" => Strings.NinaZapretAlreadyWorking,
                "no_improvement" => Strings.NinaZapretNoImprovement,
                "cancelled" => Strings.NinaZapretCancelled,
                "stopped" => Strings.NinaZapretStopped,
                "access_denied" => Strings.AssistantAccessDenied,
                "invalid_subscription" => Strings.AssistantSubscription,
                "assistant_not_deployed" => Strings.NinaZapretAssistantNotDeployed,
                "assistant_request_rejected" => Strings.NinaZapretAssistantRequestRejected,
                "assistant_unavailable" => Strings.AssistantUnavailable,
                "quota_exceeded" or "rate_limited" => Strings.AssistantRateLimited,
                "engine_conflict" => Strings.NinaZapretEngineConflict,
                "service_disabled" => Strings.NinaZapretServiceDisabled,
                "service_unavailable" => Strings.NinaZapretServiceUnavailable,
                "permission_denied" => Strings.NinaZapretPermissionDenied,
                "unsupported_runtime" => Strings.NinaZapretRuntimeUnsupported,
                "rollback_failed" => Strings.NinaZapretRollbackFailed,
                "network_changed" => Strings.NinaZapretNetworkChanged,
                "network_unknown" => Strings.NinaZapretNetworkUnknown,
                "controls_failed" => Strings.NinaZapretControlsFailed,
                "busy" => Strings.NinaZapretBusy,
                _ => Strings.NinaZapretFailed,
            };
        }
        finally
        {
            _ninaZapretRequest = null;
            OnPropertyChanged(nameof(IsZapretMagicButtonEnabled));
        }
    }
}
