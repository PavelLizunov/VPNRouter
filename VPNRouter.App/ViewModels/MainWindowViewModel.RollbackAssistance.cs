#nullable enable

using System;
using System.Net.NetworkInformation;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    private readonly ConnectionRollbackHistory _rollbackHistory = new();
    private ManualRollbackAttempt? _manualRollbackAttempt;

    private sealed record ManualRollbackAttempt(
        string Id,
        string? ConfigFingerprint,
        string? AssistantSubscriptionUrl,
        string? ActiveConfigIncompatibility);

    private string? BeginManualRollbackAttempt(AppSettings settings)
    {
        _manualRollbackAttempt = null;
        SetSourceConnectionObservation(null);
        try
        {
            var fingerprint = RollbackArtifactPolicy.IsSupportedHost()
                ? ConnectionRollbackHistory.ComputeConfigFingerprint(settings)
                : null;
            _manualRollbackAttempt = new ManualRollbackAttempt(
                Guid.NewGuid().ToString("N"), fingerprint,
                SelectAssistantSubscription(settings.App)?.Url,
                fingerprint == null ? null : RollbackConfigCompatibility.CheckActiveConfig(
                    settings, VerifiedRecoveryVersion()));
            return _manualRollbackAttempt.Id;
        }
        catch (Exception ex)
        {
            // Optional recovery evidence must not prevent the requested connection.
            _logger.Debug("[VM] Connection recovery evidence unavailable ({ErrorType})", ex.GetType().Name);
            return null;
        }
    }

    private void CompleteManualRollbackSuccess(string? attemptId)
    {
        if (string.IsNullOrEmpty(attemptId) ||
            _manualRollbackAttempt is not { } attempt ||
            attempt.Id != attemptId)
            return;

        _manualRollbackAttempt = null;
        SetSourceConnectionObservation(null);
        if (attempt.ConfigFingerprint == null)
            return;

        var decision = _rollbackHistory.ObserveSuccess(
            attempt.ConfigFingerprint,
            knownRegression: null,
            signedRecoveryVersion: VerifiedRecoveryVersion(),
            activeConfigIncompatibility: attempt.ActiveConfigIncompatibility);
        UpdateVm?.ShowConnectionRollbackSuggestion(decision);
    }

    private void CompleteManualRollbackFailure(
        string? attemptId,
        ConnectionAttemptAssessment assessment)
    {
        if (string.IsNullOrEmpty(attemptId) ||
            _manualRollbackAttempt is not { } attempt ||
            attempt.Id != attemptId)
            return;

        _manualRollbackAttempt = null;
        SetSourceConnectionObservation(assessment.DetailCode, attempt.AssistantSubscriptionUrl);

        if (attempt.ConfigFingerprint == null)
            return;

        if (assessment.Category == ConnectionFailureCategory.Technical &&
            !NetworkInterface.GetIsNetworkAvailable())
        {
            assessment = new ConnectionAttemptAssessment(
                ConnectionFailureCategory.NetworkUnavailable, "network-unavailable");
        }

        var decision = _rollbackHistory.ObserveAttempt(
            attempt.Id,
            attempt.ConfigFingerprint,
            assessment,
            knownRegression: null,
            signedRecoveryVersion: VerifiedRecoveryVersion(),
            activeConfigIncompatibility: attempt.ActiveConfigIncompatibility);
        UpdateVm?.ShowConnectionRollbackSuggestion(decision);
    }

    private void RefreshRollbackAssistance()
    {
        if (!RollbackArtifactPolicy.IsSupportedHost() || !_rollbackHistory.HasRepeatedFailures())
            return;

        var fingerprint = ConnectionRollbackHistory.ComputeConfigFingerprint(_settings);
        if (fingerprint == null)
        {
            UpdateVm?.ClearConnectionRollbackSuggestion();
            return;
        }
        var decision = _rollbackHistory.Evaluate(
            fingerprint,
            knownRegression: null,
            signedRecoveryVersion: VerifiedRecoveryVersion(),
            activeConfigIncompatibility: ActiveRollbackIncompatibility());
        UpdateVm?.ShowConnectionRollbackSuggestion(decision);
    }

    private string? ActiveRollbackIncompatibility() =>
        RollbackConfigCompatibility.CheckActiveConfig(_settings, VerifiedRecoveryVersion());

    private static string? VerifiedRecoveryVersion()
    {
        var verification = SignedRecoveryManifestTrust.Verification;
        return verification.IsValid ? verification.Manifest?.Version : null;
    }
}
