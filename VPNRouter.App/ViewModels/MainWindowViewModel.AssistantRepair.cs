using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Strings = VPNRouter.Core.Localization.Strings;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    // This entry point is user-initiated. Model-driven controls cannot run repair trials.
    internal async Task<string> RepairAssistantVpnAsync(CancellationToken ct)
    {
        if (IsServiceManagedVpn || _displayExternalTunnel) return Strings.AssistantServiceManaged;
        if (!CanRunAssistantChange()) return Strings.AssistantBusy;
        ct.ThrowIfCancellationRequested();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromMinutes(2));
        ct = budget.Token;

        var snapshot = ReadAssistantSnapshot(repairMtu: true);
        if (snapshot == null) return Strings.AssistantRepairInvalidSettings;
        if (!_engine.IsAssistantSessionCurrent(snapshot.Session))
            return Strings.AssistantSettingsChanged;
        var settings = snapshot.Settings;
        var app = settings.App;
        bool knowsInclude = app.RoutingAppsIncludeInitialized || app.RoutingAppsInclude.Count > 0
            || _engine.HasAssistantProcessScan;
        bool hasInclude = !knowsInclude
            || ConfigGenerator.ResolveEffectiveAppProcesses(_engine.MonitoredProcesses, settings).Count > 0;
        string? routingIssue = AssistantRepairPolicy.FindRoutingIssue(settings, hasInclude) switch
        {
            AssistantRepairRoutingIssue.CustomConfiguration => Strings.AssistantRepairCustom,
            AssistantRepairRoutingIssue.AutoRouteDisabled => Strings.AssistantRepairAutoRoute,
            AssistantRepairRoutingIssue.EmptyInclude => Strings.AssistantRepairEmptyInclude,
            _ => null,
        };
        if (routingIssue != null) return routingIssue;

        if (snapshot.Session is { } session)
        {
            if (_engine.SingBoxPid is not { } pid) return Strings.AssistantSettingsChanged;
            var isCurrent = _engine.CaptureReadinessGuard(pid);
            if (!isCurrent()) return Strings.AssistantSettingsChanged;
            using var sanity = new ConfigSanityCheck(_logger);
            var probe = await session.ProbeProxyAsync(sanity, ct, preferHttps: true);
            ct.ThrowIfCancellationRequested();
            if (!isCurrent() || !AssistantSnapshotStillCurrent(snapshot))
                return Strings.AssistantSettingsChanged;
            return probe.IsDead ? Strings.AssistantRepairRunningUnreachable : Strings.AssistantRepairReachable;
        }

        var baseline = SettingsLoader.Copy(settings);
        var normalizedMtu = snapshot.NormalizedMtu;
        var result = await ApplyAssistantSettingsAsync(baseline, snapshot, null, false, ct,
            keepSavedSettings: normalizedMtu == null);
        if (result.Error == null) return RepairSuccess(result, normalizedMtu);
        if (result.Rejection != AssistantCandidateRejection.ProxyHealth)
        {
            if (result.Error == Strings.AssistantApplyFailed)
                return VlessServersResolver.DescribeEmptyReason(settings) != null
                    ? Strings.AssistantRepairNoServers : Strings.AssistantRepairStartupFailed;
            return result.Error;
        }

        int? trialMtu = normalizedMtu;
        if (OperatingSystem.IsWindows())
        {
            var mtuProbe = await Task.Run(() => HealthCheck.ProbePathMtuPayload(ct), ct);
            if (!CanContinueRepair(snapshot, ct)) return Strings.AssistantSettingsChanged;
            trialMtu = AssistantRepairPolicy.SuggestMtu(settings.Tun, mtuProbe) ?? normalizedMtu;
            if (trialMtu is int smaller && smaller != baseline.Tun.Mtu)
            {
                var trial = SettingsLoader.Copy(settings);
                trial.Tun.Mtu = smaller;
                result = await ApplyAssistantSettingsAsync(trial, snapshot, null, false, ct);
                if (result.Error == null) return RepairSuccess(result, smaller);
                if (result.Rejection != AssistantCandidateRejection.ProxyHealth) return result.Error;
            }
        }

        var alternates = AssistantRepairPolicy.AlternateServers(settings);
        if (alternates.Count == 0) return Strings.AssistantRepairNoAutomaticServer;
        var verifier = new VlessDeepVerifier(_logger);
        foreach (var server in alternates)
        {
            if (!CanContinueRepair(snapshot, ct)) return Strings.AssistantSettingsChanged;
            var measured = await verifier.VerifyAsync(server.Entry, measureBandwidth: false, ct);
            if (!CanContinueRepair(snapshot, ct)) return Strings.AssistantSettingsChanged;
            if (!measured.Ok) continue;

            var candidate = SettingsLoader.Copy(settings);
            if (trialMtu is int mtu) candidate.Tun.Mtu = mtu;
            candidate.App.ActiveSubscriptionId = server.FeedId;
            candidate.App.ActiveSubscriptionServer = server.Entry.Name;
            candidate.App.ActiveSubscriptionRow = SubscriptionEntry.RowKey(server.Entry);
            result = await ApplyAssistantSettingsAsync(candidate, snapshot, null, false, ct, repairServer: server);
            if (result.Error == null)
                return result.SettingsChangedAfterCommit ? Strings.AssistantSettingsChanged : Strings.AssistantRepairAlternate(trialMtu);
            if (result.Rejection is not (AssistantCandidateRejection.ProxyHealth or AssistantCandidateRejection.ServerMismatch)) return result.Error;
        }

        return Strings.AssistantRepairNoReachableServer;
    }

    private bool CanContinueRepair(AssistantSnapshot snapshot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return CanRunAssistantChange() && AssistantSnapshotStillCurrent(snapshot)
            && _engine.IsAssistantSessionCurrent(snapshot.Session);
    }

    private static string RepairSuccess(AssistantChangeResult result, int? mtu) =>
        result.SettingsChangedAfterCommit ? Strings.AssistantSettingsChanged
            : mtu is int value ? Strings.AssistantRepairMtu(value) : Strings.AssistantRepairReachable;
}
