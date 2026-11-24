using VPNRouter.Core;
using VPNRouter.Core.Localization;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    private sealed record ConnectionObservation(SourceConnectionFailure Failure, DateTime Utc, string SubscriptionUrl);
    private ConnectionObservation? _sourceConnection;

    internal bool HasSourceConnection => _sourceConnection is { } observation
        && observation.SubscriptionUrl == SelectAssistantSubscription(_settings.App)?.Url;

    internal void SetSourceConnectionObservation(string? code, string? attemptedSubscriptionUrl = null)
    {
        SourceConnectionFailure? failure = code switch
        {
            "phase-a-timeout" => SourceConnectionFailure.StartupTimeout,
            "phase-b-timeout" => SourceConnectionFailure.ReadinessTimeout,
            "engine-stopped" => SourceConnectionFailure.UnexpectedStop,
            "tun-owned" => SourceConnectionFailure.TunBusy,
            _ => null,
        };
        _sourceConnection = failure is { } observed && attemptedSubscriptionUrl != null
            && attemptedSubscriptionUrl == SelectAssistantSubscription(_settings.App)?.Url
            ? new(observed, DateTime.UtcNow, attemptedSubscriptionUrl)
            : null;
        OnPropertyChanged(nameof(HasSourceConnection));
    }

    internal async Task<string> DiagnoseAssistantSourceAsync(bool connection, Func<string, CancellationToken, Task<bool>> consent, CancellationToken ct)
    {
        var url = SelectAssistantSubscription(_settings.App)?.Url;
        if (url == null) return Strings.AssistantSubscription;
        var build = AssistantSourceBuild.Installed();
        if (build == null) return Strings.SourceDiagnosisUnknown;
        var observed = connection ? _sourceConnection : null;
        AssistantCrashEvidence? crash = null;
        string preview;
        if (connection)
        {
            if (observed == null || observed.SubscriptionUrl != url) return Strings.SourceConnectionExpired;
            preview = Strings.SourceConnectionConsent(observed.Failure, observed.Utc);
        }
        else
        {
            var report = await Task.Run(() => AssistantCrashEvidence.ReadLatestReport(AppPaths.DataDir, ct), ct);
            ct.ThrowIfCancellationRequested();
            if (report == null) return Strings.SourceDiagnosisNoReport;
            if (report.SourceIdentity != build.ReportIdentity) return Strings.SourceDiagnosisMismatch;
            crash = report.Evidence;
            if (crash is { Fault: AssistantCrashFault.Unknown, Area: AssistantCrashArea.Unknown, Signal: AssistantCrashSignal.None })
                return Strings.SourceDiagnosisInsufficient;
            preview = Strings.SourceDiagnosisConsent(report);
        }
        if (SelectAssistantSubscription(_settings.App)?.Url != url) return Strings.AssistantSubscription;
        if (!await consent(preview, ct)) return Strings.SourceDiagnosisDeclined;
        ct.ThrowIfCancellationRequested();
        if (SelectAssistantSubscription(_settings.App)?.Url != url) return Strings.AssistantSubscription;
        if (connection && !ReferenceEquals(_sourceConnection, observed)) return Strings.SourceConnectionExpired;

        var reply = await SubscriptionAssistantClient.Shared.DiagnoseSourceAsync(url, build, crash, IsRussian, ct, observed?.Failure);
        ct.ThrowIfCancellationRequested();
        if (SelectAssistantSubscription(_settings.App)?.Url != url) return Strings.AssistantSubscription;
        if (connection && !ReferenceEquals(_sourceConnection, observed)) return Strings.SourceConnectionExpired;
        return reply.Status switch
        {
            SourceDiagnosisStatus.Available when reply.Diagnosis != null => Strings.SourceDiagnosisText(reply.Diagnosis),
            SourceDiagnosisStatus.UnknownBuild => Strings.SourceDiagnosisUnknown,
            SourceDiagnosisStatus.InsufficientEvidence => Strings.SourceDiagnosisNoMatch,
            SourceDiagnosisStatus.AccessDenied => Strings.AssistantAccessDenied,
            SourceDiagnosisStatus.RateLimited => Strings.AssistantRateLimited,
            SourceDiagnosisStatus.Unsupported => Strings.AssistantEndpointUnavailable,
            _ => Strings.AssistantUnavailable,
        };
    }
}
