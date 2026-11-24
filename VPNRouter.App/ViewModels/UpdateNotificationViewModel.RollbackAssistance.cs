#nullable enable

using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.Core;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.UpdateSources;
using CoreStrings = VPNRouter.Core.Localization.Strings;

namespace VPNRouter.App.ViewModels;

public partial class UpdateNotificationViewModel
{
    private RollbackAssistDecision? _rollbackDecision;
    private UpdateSourceInfo? _assistedListedRelease;
    private RollbackHandoffResult? _rollbackHandoffStatus;
    private bool _rollbackAssistanceFlow;

    internal Func<Task>? RunLocalDiagnostics { get; set; }
    internal Func<string, bool> MatchesRollbackConfig { get; set; } = _ => false;
    internal Func<bool> IsRollbackHostSupported { get; set; } = () => RollbackArtifactPolicy.IsSupportedHost();
    internal Func<string?> ActiveConfigIncompatibility { get; set; } = () => null;
    internal Func<string, SignedRecoveryManifest, bool> VerifyStagedManifest { get; set; } =
        RollbackArtifactPolicy.StagedTreeMatchesManifest;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRollbackAssistanceChoiceVisible))]
    private bool _isRollbackAssistanceVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRollbackAssistanceChoiceVisible))]
    private bool _isRollbackAssistanceBusy;

    [ObservableProperty] private string _rollbackAssistanceTitle = string.Empty;
    [ObservableProperty] private string _rollbackAssistanceBody = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRollbackAssistanceConfidence))]
    private string _rollbackAssistanceConfidenceText = string.Empty;
    [ObservableProperty] private string _rollbackAssistancePrimaryText = string.Empty;
    [ObservableProperty] private bool _isRollbackAssistanceDiagnosticsOnly;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRollbackAssistanceError))]
    private string _rollbackAssistanceError = string.Empty;
    [ObservableProperty] private bool _isRollbackStatusVisible;
    [ObservableProperty] private string _rollbackStatusText = string.Empty;

    public bool IsAssistedRollbackConfirmationVisible => _rollbackAssistanceFlow && IsRollbackConfirmationVisible;
    public bool IsHistoryRollbackConfirmationVisible => !_rollbackAssistanceFlow && IsRollbackConfirmationVisible;

    public bool IsRollbackAssistanceChoiceVisible =>
        IsRollbackAssistanceVisible && !IsRollbackConfirmationVisible && !IsRollbackAssistanceBusy && !IsDownloading;

    public string RollbackAssistanceConfirmationText => SelectedRollback == null
        ? string.Empty
        : CoreStrings.RollbackAssistanceConfirmation(AppVersion.Version, SelectedRollback.Version);

    public bool HasRollbackAssistanceError => RollbackAssistanceError.Length > 0;
    public bool HasRollbackAssistanceConfidence => RollbackAssistanceConfidenceText.Length > 0;
    public string RollbackAssistanceSecondaryText => _rollbackDecision?.Kind == RollbackAssistKind.NeedsPriorVersionAttestation
        ? CoreStrings.RollbackAssistanceAttestationNoOrUnsure
        : CoreStrings.RollbackAssistanceNotNow;
    public string RollbackAssistanceInstallText => CoreStrings.RollbackAssistanceInstall;
    public string RollbackAssistanceBusyText => CoreStrings.RollbackAssistanceBusy;
    public string RollbackAssistanceStatusDismissText => CoreStrings.RollbackAssistanceStatusDismiss;
    public string RollbackAssistanceProvenanceText => CoreStrings.RollbackAssistanceProvenance;

    internal void ShowConnectionRollbackSuggestion(RollbackAssistDecision decision)
    {
        if (_rollbackAssistanceFlow)
            CancelRollback();
        _rollbackDecision = decision;
        if (!decision.ShouldShow)
        {
            ClearConnectionRollbackSuggestion();
            return;
        }

        RollbackAssistanceError = string.Empty;
        IsRollbackAssistanceVisible = true;
        ApplyRollbackAssistanceTexts();
    }

    internal void ClearConnectionRollbackSuggestion()
    {
        if (_rollbackAssistanceFlow)
            CancelRollback();
        _rollbackDecision = null;
        IsRollbackAssistanceVisible = false;
        RollbackAssistanceError = string.Empty;
    }

    [RelayCommand]
    private async Task RunRollbackAssistancePrimaryAsync()
    {
        if (IsDownloading || IsRollbackAssistanceBusy || IsRollbackConfirmationVisible || _rollbackDecision == null)
            return;

        if (_rollbackDecision.Kind == RollbackAssistKind.NeedsPriorVersionAttestation)
        {
            AnswerPriorVersionAttestation(worked: true);
            return;
        }

        if (_rollbackDecision.Kind == RollbackAssistKind.NeedsLocalDiagnostics)
        {
            if (RunLocalDiagnostics == null)
            {
                RollbackAssistanceError = CoreStrings.RollbackAssistanceUnavailable;
                return;
            }
            await RunLocalDiagnostics();
            return;
        }

        await LoadAssistedRollbackAsync();
    }

    [RelayCommand]
    private void DismissRollbackAssistance()
    {
        if (_rollbackDecision == null)
            return;

        if (_rollbackDecision.Kind == RollbackAssistKind.NeedsPriorVersionAttestation)
        {
            AnswerPriorVersionAttestation(worked: null);
            return;
        }

        _rollbackHistory?.Dismiss(_rollbackDecision.ConfigFingerprint);
        ClearConnectionRollbackSuggestion();
    }

    private void AnswerPriorVersionAttestation(bool? worked)
    {
        if (_rollbackDecision is not { } decision ||
            decision.Kind != RollbackAssistKind.NeedsPriorVersionAttestation ||
            string.IsNullOrWhiteSpace(decision.PreviousWorkingVersion))
            return;

        if (!IsCurrentRecoveryDecision(decision))
        {
            RollbackAssistanceError = CoreStrings.RollbackAssistanceExpired;
            return;
        }
        _rollbackHistory?.RecordPriorVersionAttestation(
            decision.PreviousWorkingVersion,
            decision.ConfigFingerprint,
            worked);

        var refreshed = _rollbackHistory?.Evaluate(
            decision.ConfigFingerprint,
            knownRegression: null,
            signedRecoveryVersion: TrustedRecoveryVersion(),
            activeConfigIncompatibility: ActiveConfigIncompatibility());
        if (refreshed != null)
            ShowConnectionRollbackSuggestion(refreshed);
    }

    private static string? TrustedRecoveryVersion()
    {
        var verification = SignedRecoveryManifestTrust.Verification;
        return verification.IsValid ? verification.Manifest?.Version : null;
    }

    [RelayCommand]
    private void DismissRollbackStatus()
    {
        _rollbackHandoffStatus = null;
        IsRollbackStatusVisible = false;
        _rollbackHistory?.AcknowledgeHandoffResult();
    }

    private bool IsCurrentRecoveryDecision(RollbackAssistDecision decision) =>
        ReferenceEquals(decision, _rollbackDecision) && MatchesRollbackConfig(decision.ConfigFingerprint);

    private async Task LoadAssistedRollbackAsync()
    {
        var decision = _rollbackDecision;
        if (decision?.Kind != RollbackAssistKind.OfferRollback || !IsCurrentRecoveryDecision(decision))
        {
            RollbackAssistanceError = CoreStrings.RollbackAssistanceExpired;
            return;
        }
        if (!IsRollbackHostSupported())
        {
            RollbackAssistanceError = CoreStrings.RollbackAssistanceUnavailable;
            return;
        }

        var incompatibility = ActiveConfigIncompatibility();
        if (!string.IsNullOrWhiteSpace(incompatibility))
        {
            RollbackAssistanceError = CoreStrings.RollbackAssistanceIncompatibleBody(incompatibility);
            return;
        }

        var verification = SignedRecoveryManifestTrust.Verification;
        if (!verification.IsValid || verification.Manifest is not { } manifest)
        {
            RollbackAssistanceError = CoreStrings.RollbackAssistanceManifestUnavailable;
            return;
        }

        SelectedRollback = null;
        IsRollbackConfirmationVisible = false;
        IsRollbackAssistanceBusy = true;
        RollbackAssistanceError = string.Empty;
        try
        {
            var includeCandidates = _settings.IsExperimental;
            var releases = await _updateSource.ListStableAsync(StableHistoryLimit);
            if (!ReferenceEquals(decision, _rollbackDecision)) return;
            if (!MatchesRollbackConfig(decision.ConfigFingerprint))
            {
                RollbackAssistanceError = CoreStrings.RollbackAssistanceExpired;
                return;
            }
            incompatibility = ActiveConfigIncompatibility();
            if (!string.IsNullOrWhiteSpace(incompatibility))
            {
                RollbackAssistanceError = CoreStrings.RollbackAssistanceIncompatibleBody(incompatibility);
                return;
            }
            var listed = releases.FirstOrDefault(release =>
                string.Equals(release.Version, manifest.Version, StringComparison.Ordinal));

            var verdict = RollbackArtifactPolicy.CreateSignedRecoveryArtifact(
                AppVersion.Version,
                includeCandidates,
                listed,
                isWindows: true);
            if (!verdict.Eligible || verdict.Artifact is not { } artifact)
            {
                RollbackAssistanceError = RollbackArtifactErrorText(verdict);
                return;
            }

            _assistedListedRelease = listed;
            _rollbackAssistanceFlow = true;
            SelectedRollback = artifact;
            IsRollbackConfirmationVisible = true;
            RollbackAssistanceError = string.Empty;
            OnPropertyChanged(nameof(RollbackAssistanceConfirmationText));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[UpdateVm] Assisted rollback history load failed");
            if (ReferenceEquals(decision, _rollbackDecision))
                RollbackAssistanceError = CoreStrings.RollbackAssistanceNoCompatible;
        }
        finally
        {
            IsRollbackAssistanceBusy = false;
        }
    }

    private string RollbackArtifactErrorText(RollbackArtifactVerdict verdict)
    {
        return verdict.ReasonCode switch
        {
            "signed-manifest-invalid" or "manifest-platform" or "package-metadata" or "package-url"
                => CoreStrings.RollbackAssistanceManifestUnavailable,
            "revoked" or "security-floor" => CoreStrings.RollbackAssistanceRevoked,
            "unsupported-host" => CoreStrings.RollbackAssistanceUnavailable,
            _ => CoreStrings.RollbackAssistanceNoCompatible,
        };
    }

    private void CompleteRollbackHandoff()
    {
        var result = _rollbackHistory?.CompleteHandoff(AppVersion.Version);
        if (result is not { HadPending: true })
            return;

        _rollbackHandoffStatus = result;
        IsRollbackStatusVisible = true;
        ApplyRollbackStatusText();
    }

    private void RefreshRollbackLocalization()
    {
        ApplyRollbackAssistanceTexts();
        ApplyRollbackStatusText();
        OnPropertyChanged(nameof(RollbackAssistanceConfirmationText));
        OnPropertyChanged(nameof(RollbackAssistanceSecondaryText));
        OnPropertyChanged(nameof(RollbackAssistanceInstallText));
        OnPropertyChanged(nameof(RollbackAssistanceBusyText));
        OnPropertyChanged(nameof(RollbackAssistanceStatusDismissText));
        OnPropertyChanged(nameof(RollbackAssistanceProvenanceText));
    }

    private void ApplyRollbackAssistanceTexts()
    {
        if (_rollbackDecision is not { } decision || !decision.ShouldShow)
        {
            RollbackAssistanceTitle = string.Empty;
            RollbackAssistanceBody = string.Empty;
            RollbackAssistanceConfidenceText = string.Empty;
            RollbackAssistancePrimaryText = string.Empty;
            IsRollbackAssistanceDiagnosticsOnly = false;
            return;
        }

        var incompatible = decision.ReasonCode.StartsWith(
            "active-config-incompatible:", StringComparison.Ordinal)
            ? decision.ReasonCode["active-config-incompatible:".Length..]
            : null;
        IsRollbackAssistanceDiagnosticsOnly = decision.Kind == RollbackAssistKind.NeedsLocalDiagnostics;
        var attestation = decision.Kind == RollbackAssistKind.NeedsPriorVersionAttestation;
        RollbackAssistanceTitle = attestation
            ? CoreStrings.RollbackAssistanceAttestationTitle
            : IsRollbackAssistanceDiagnosticsOnly
                ? CoreStrings.RollbackAssistanceDiagnosticsTitle
                : CoreStrings.RollbackAssistanceTitle;
        RollbackAssistancePrimaryText = attestation
            ? CoreStrings.RollbackAssistanceAttestationYes
            : IsRollbackAssistanceDiagnosticsOnly
                ? CoreStrings.RollbackAssistanceRunDiagnostics
                : CoreStrings.RollbackAssistanceViewPrevious;

        RollbackAssistanceBody = incompatible != null
            ? CoreStrings.RollbackAssistanceIncompatibleBody(incompatible)
            : (decision.Kind switch
        {
            RollbackAssistKind.NeedsPriorVersionAttestation when decision.PreviousWorkingVersion != null =>
                CoreStrings.RollbackAssistanceAttestationBody(decision.ConsecutiveFailures,
                    decision.CurrentVersion, decision.PreviousWorkingVersion),
            RollbackAssistKind.NeedsLocalDiagnostics =>
                CoreStrings.RollbackAssistanceDiagnosticsBody(decision.ConsecutiveFailures, decision.CurrentVersion),
            _ when decision.ReasonCode == "known-regression" && decision.PreviousWorkingVersion != null =>
                CoreStrings.RollbackAssistanceBodyKnownRegression(decision.ConsecutiveFailures,
                    decision.CurrentVersion, decision.PreviousWorkingVersion),
            _ when decision.ReasonCode == "attested-previous-install" && decision.PreviousWorkingVersion != null =>
                CoreStrings.RollbackAssistanceBodyAttested(decision.ConsecutiveFailures,
                    decision.CurrentVersion, decision.PreviousWorkingVersion),
            _ when decision.PreviousWorkingVersion != null =>
                CoreStrings.RollbackAssistanceBody(decision.ConsecutiveFailures,
                    decision.CurrentVersion, decision.PreviousWorkingVersion),
            _ => CoreStrings.RollbackAssistanceDiagnosticsBody(
                decision.ConsecutiveFailures, decision.CurrentVersion),
        });

        RollbackAssistanceConfidenceText = decision.Confidence switch
        {
            RollbackConfidence.High => CoreStrings.RollbackAssistanceConfidenceHigh,
            RollbackConfidence.Medium => CoreStrings.RollbackAssistanceConfidenceMedium,
            _ => string.Empty,
        };
        OnPropertyChanged(nameof(RollbackAssistanceSecondaryText));
    }

    private void ApplyRollbackStatusText()
    {
        if (_rollbackHandoffStatus is not { } status)
        {
            RollbackStatusText = string.Empty;
            return;
        }

        RollbackStatusText = status.Success
            ? CoreStrings.RollbackAssistanceCompleted(status.TargetVersion)
            : CoreStrings.RollbackAssistanceNotConfirmed(status.TargetVersion, status.ObservedVersion);
    }
}
