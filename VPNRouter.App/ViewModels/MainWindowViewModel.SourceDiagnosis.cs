using VPNRouter.Core;
using VPNRouter.Core.Localization;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    internal async Task<string> DiagnoseAssistantSourceAsync(Func<string, CancellationToken, Task<bool>> consent, CancellationToken ct)
    {
        var url = SelectAssistantSubscription(_settings.App)?.Url;
        if (url == null) return Strings.AssistantSubscription;
        var report = await Task.Run(() => AssistantCrashEvidence.ReadLatestReport(AppPaths.DataDir, ct), ct);
        ct.ThrowIfCancellationRequested();
        if (report == null) return Strings.SourceDiagnosisNoReport;
        var build = AssistantSourceBuild.Installed();
        if (build == null || report.SourceIdentity != build.ReportIdentity) return Strings.SourceDiagnosisMismatch;
        if (report.Evidence is { Fault: AssistantCrashFault.Unknown, Area: AssistantCrashArea.Unknown, Signal: AssistantCrashSignal.None })
            return Strings.SourceDiagnosisInsufficient;
        if (SelectAssistantSubscription(_settings.App)?.Url != url) return Strings.AssistantSubscription;
        if (!await consent(Strings.SourceDiagnosisConsent(report), ct)) return Strings.SourceDiagnosisDeclined;
        ct.ThrowIfCancellationRequested();
        if (SelectAssistantSubscription(_settings.App)?.Url != url) return Strings.AssistantSubscription;

        var reply = await SubscriptionAssistantClient.Shared.DiagnoseSourceAsync(url, build, report.Evidence, IsRussian, ct);
        ct.ThrowIfCancellationRequested();
        if (SelectAssistantSubscription(_settings.App)?.Url != url) return Strings.AssistantSubscription;
        return reply.Status switch
        {
            SourceDiagnosisStatus.Available when reply.Diagnosis != null => Strings.SourceDiagnosisText(reply.Diagnosis),
            SourceDiagnosisStatus.UnknownBuild => Strings.SourceDiagnosisUnknown,
            SourceDiagnosisStatus.AccessDenied => Strings.AssistantAccessDenied,
            SourceDiagnosisStatus.RateLimited => Strings.AssistantRateLimited,
            SourceDiagnosisStatus.Unsupported => Strings.AssistantEndpointUnavailable,
            _ => Strings.AssistantUnavailable,
        };
    }
}
