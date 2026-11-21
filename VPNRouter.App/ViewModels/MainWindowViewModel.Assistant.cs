using System.Text.RegularExpressions;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Strings = VPNRouter.Core.Localization.Strings;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    private AssistantUndo? _assistantUndo;
    internal bool HasAssistantUndo => _assistantUndo != null;
    internal bool HasAssistantSubscription => Subscriptions.Any(s =>
        s.Enabled && SubscriptionAssistantClient.IsEligible(s.Url));

    private sealed record AssistantUndo(RoutingSelection Previous, string Revision,
        bool WasConnected, AcceptedTunnelInfo Session);
    private sealed record AssistantSnapshot(AppSettings Settings, string Revision, int RoutingRevision,
        AcceptedTunnelInfo? Session);
    private sealed record AssistantChangeResult(string? Error, AcceptedTunnelInfo? Session = null);

    private sealed record RoutingSelection(string Mode, string AppsMode, bool Initialized, string[] Include,
        string Profile)
    {
        internal static RoutingSelection Capture(AppSettings settings) =>
            new(settings.App.RoutingMode, settings.App.RoutingAppsMode, settings.App.RoutingAppsIncludeInitialized,
                [.. (settings.App.RoutingAppsInclude ?? [])], settings.ActiveProfile);

        internal void ApplyTo(AppSettings settings)
        {
            settings.App.RoutingMode = Mode;
            settings.App.RoutingAppsMode = AppsMode;
            settings.App.RoutingAppsIncludeInitialized = Initialized;
            settings.App.RoutingAppsInclude = [.. Include];
            settings.ActiveProfile = Profile;
        }
    }

    private sealed class AssistantSettingsChangedException : Exception { }

    private static SubscriptionEntry? SelectAssistantSubscription(AppConfig settings)
    {
        var eligible = settings.Subscriptions
            .Where(s => s.Enabled && SubscriptionAssistantClient.IsEligible(s.Url))
            .ToArray();
        return eligible.FirstOrDefault(s => s.Id == settings.ActiveSubscriptionId)
            ?? (eligible.Length == 1 ? eligible[0] : null);
    }

    internal static string? ResolveAssistantApp(string goal)
    {
        if (!Regex.IsMatch(goal, @"(?<![\p{L}\p{N}_])(?:chrome|хром)(?![\p{L}\p{N}_])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return null;

        if (OperatingSystem.IsWindows()) return "chrome.exe";
        return OperatingSystem.IsMacOS() ? "Google Chrome" : "chrome";
    }

    internal async Task<string> RunAssistantGoalAsync(string goal, CancellationToken ct)
    {
        if (!CanRunAssistantChange()) return Strings.AssistantBusy;
        var snapshot = ReadAssistantSnapshot();
        if (snapshot == null || !_engine.IsAssistantSessionCurrent(snapshot.Session))
            return Strings.AssistantSettingsChanged;

        var saved = snapshot.Settings;
        var subscription = SelectAssistantSubscription(saved.App);
        if (subscription == null) return Strings.AssistantSubscription;
        if (saved.App.ConfigMode is not ("generated" or "subscribe") || saved.App.CustomRules.Any(rule => rule.Enabled))
            return Strings.AssistantRuleConflict;

        var target = ResolveAssistantApp(goal);
        var plan = await SubscriptionAssistantClient.Shared.PlanAsync(subscription.Url, goal, target != null, IsRussian, ct);
        if (plan.Status != AssistantPlanStatus.RouteOnlyApp || target == null)
            return AssistantPlanMessage(plan.Status);

        ct.ThrowIfCancellationRequested();
        if (!CanRunAssistantChange() || !AssistantSnapshotStillCurrent(snapshot)
            || !_engine.IsAssistantSessionCurrent(snapshot.Session))
            return Strings.AssistantSettingsChanged;

        var previous = RoutingSelection.Capture(saved);
        var profile = previous.Mode == "split" ? previous.Profile : SimpleSplitProfile;
        var desired = new RoutingSelection("split", "include", true, [target], profile);
        if (snapshot.Session?.RoutesOnlyApp(target) == true && previous.Mode == desired.Mode
            && previous.AppsMode == desired.AppsMode && previous.Initialized
            && previous.Include.SequenceEqual(desired.Include, StringComparer.Ordinal)
            && _engine.AssistantRouteMatches(saved, target)
            && _engine.SingBoxPid is { } pid && _engine.DescribeReadiness(pid) == null)
            return Strings.AssistantAlreadySet;

        var candidate = SettingsLoader.Copy(saved);
        desired.ApplyTo(candidate);
        var result = await ApplyAssistantSettingsAsync(candidate, snapshot, target, restoreWithoutStart: false, ct);
        if (result.Error != null) return result.Error;
        if (result.Session is not { } session) return Strings.AssistantApplyFailed;

        _assistantUndo = new(previous, SettingsLoader.GetSubmittedRevision(candidate), snapshot.Session != null, session);
        return Strings.AssistantApplied;
    }

    internal async Task<string> UndoAssistantGoalAsync(CancellationToken ct)
    {
        if (_assistantUndo is not { } undo || !CanRunAssistantChange()) return Strings.AssistantBusy;
        var snapshot = ReadAssistantSnapshot();
        if (snapshot == null || snapshot.Revision != undo.Revision
            || (snapshot.Session != null && !ReferenceEquals(snapshot.Session, undo.Session))
            || !_engine.IsAssistantSessionCurrent(snapshot.Session))
        {
            _assistantUndo = null;
            return Strings.AssistantSettingsChanged;
        }

        var candidate = SettingsLoader.Copy(snapshot.Settings);
        undo.Previous.ApplyTo(candidate);

        var result = await ApplyAssistantSettingsAsync(candidate, snapshot, null,
            restoreWithoutStart: snapshot.Session == null || !undo.WasConnected, ct);
        if (result.Error != null) return result.Error;

        _assistantUndo = null;
        return Strings.AssistantUndone;
    }

    private bool CanRunAssistantChange() => !_disposed && !SafeMode.Enabled && !IsConnecting && !IsApplying
        && !_isReconnecting && !IsServiceManagedVpn && !HasPendingAppChanges && !_displayExternalTunnel
        && (!IsConnected || _engine.HasActiveTunnel);

    private AssistantSnapshot? ReadAssistantSnapshot()
    {
        if (!File.Exists(AppPaths.ConfigYamlPath)) return null;
        var yaml = File.ReadAllText(AppPaths.ConfigYamlPath);
        var saved = SettingsLoader.ParseLiveCandidate(yaml);
        return saved == null ? null : new(saved, SettingsLoader.GetRevision(yaml),
            Volatile.Read(ref _routingSettingsRevision), _engine.AcceptedTunnel);
    }

    private bool AssistantSnapshotStillCurrent(AssistantSnapshot snapshot) =>
        !_disposed && !SafeMode.Enabled && ReadAssistantRevision() == snapshot.Revision
        && Volatile.Read(ref _routingSettingsRevision) == snapshot.RoutingRevision;

    private static string? ReadAssistantRevision() =>
        File.Exists(AppPaths.ConfigYamlPath) ? SettingsLoader.GetRevision(File.ReadAllText(AppPaths.ConfigYamlPath)) : null;

    private async Task<AssistantChangeResult> ApplyAssistantSettingsAsync(AppSettings candidate,
        AssistantSnapshot snapshot, string? target, bool restoreWithoutStart, CancellationToken ct)
    {
        var previousPid = _engine.SingBoxPid;
        var wasConnected = IsConnected;
        AcceptedTunnelInfo? appliedSession = null;
        string? savedRevision = null;
        int savedRoutingRevision = 0;
        IsApplying = true;
        try
        {
            if (snapshot.Session == null && !restoreWithoutStart)
                await PrepareColdStartAsync();

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(Internals.TwoPhaseStartCoordinator.DefaultPhaseABudget
                + Internals.TwoPhaseStartCoordinator.DefaultPhaseBBudget);
            budget.Token.ThrowIfCancellationRequested();

            bool applied;
            if (restoreWithoutStart)
            {
                applied = await Task.Run(() => _engine.RestoreAssistantSettingsAsync(snapshot.Session,
                    () => Commit(candidate, budget.Token), RestoreAfterFailedStop, budget.Token));
            }
            else
            {
                var runtime = VpnEngine.CopySessionSettings(candidate);
                applied = await Task.Run(() => _engine.ConnectVerifiedAppRoutingAsync(runtime,
                    new AssistantRouteIntent(snapshot.Session, target),
                    () => Commit(runtime, budget.Token), _skipVpnConflictThisSession, budget.Token));
            }

            if (!applied) return new(Strings.AssistantApplyFailed);
            if (_disposed || ReadAssistantRevision() != savedRevision
                || Volatile.Read(ref _routingSettingsRevision) != savedRoutingRevision)
                return new(Strings.AssistantSettingsChanged);

            _settings = candidate;
            LoadSettingsIntoUI();
            HasPendingAppChanges = false;
            return new(null, appliedSession);
        }
        catch (AssistantSettingsChangedException)
        {
            return new(Strings.AssistantSettingsChanged);
        }
        catch (TunOwnershipException)
        {
            return new(Strings.AssistantTunnelBusy);
        }
        catch (ConflictingVpnException conflict)
        {
            _lastConflicts = conflict.Conflicts;
            var first = _lastConflicts.FirstOrDefault();
            ConflictingVpnWarningText = first != null
                ? Strings.ConflictOtherVpnDetectedMessage(first.ProcessName, first.Pid)
                : Strings.ConflictOtherVpnDetectedTitle;
            return new(Strings.ConflictOtherVpnDetectedTitle);
        }
        finally
        {
            if (!_disposed)
            {
                IsApplying = false;
                IsConnected = _engine.SingBoxPid is { } pid && (_engine.DescribeReadiness(pid) == null
                    || (wasConnected && previousPid == pid && snapshot.Session != null
                        && _engine.IsAssistantSessionCurrent(snapshot.Session)));
                ConnectButtonText = IsConnected ? Strings.StopVPN : Strings.StartVPN;
                if (IsConnected)
                {
                    StartSubRefreshTimer();
                    RestoreConnectedStatus();
                }
                else
                {
                    StopSubRefreshTimer();
                    RefreshActiveIndicator();
                }
            }
        }

        void Commit(AppSettings verified, CancellationToken operationToken)
        {
            // The lifecycle gate is held here. Window disposal takes that gate on the UI thread.
            lock (SettingsLoader.FileWriteLock)
            {
                operationToken.ThrowIfCancellationRequested();
                if (!AssistantSnapshotStillCurrent(snapshot))
                    throw new AssistantSettingsChangedException();

                // Startup can sanitize the runtime profile without changing persisted preferences.
                candidate.ActiveProfile = verified.ActiveProfile;
                savedRevision = SettingsLoader.GetSubmittedRevision(candidate);
                BackupConfigFile();
                _settingsStore.Save(candidate, AppPaths.ConfigYamlPath);
                savedRoutingRevision = Interlocked.Increment(ref _routingSettingsRevision);
                appliedSession = restoreWithoutStart ? null : _engine.AcceptedTunnel;
            }
        }

        void RestoreAfterFailedStop()
        {
            lock (SettingsLoader.FileWriteLock)
            {
                if (_disposed || SafeMode.Enabled || savedRevision == null || ReadAssistantRevision() != savedRevision
                    || Volatile.Read(ref _routingSettingsRevision) != savedRoutingRevision)
                    throw new AssistantSettingsChangedException();

                _settingsStore.Save(snapshot.Settings, AppPaths.ConfigYamlPath);
                Interlocked.Increment(ref _routingSettingsRevision);
            }
        }
    }

    private static string AssistantPlanMessage(AssistantPlanStatus status) => status switch
    {
        AssistantPlanStatus.AppSelectionNeeded => Strings.AssistantAppNeeded,
        AssistantPlanStatus.UnsupportedGoal => Strings.AssistantUnsupportedGoal,
        AssistantPlanStatus.AccessDenied => Strings.AssistantAccessDenied,
        AssistantPlanStatus.RateLimited => Strings.AssistantRateLimited,
        AssistantPlanStatus.InvalidRequest => Strings.AssistantInvalidGoal,
        _ => Strings.AssistantUnavailable,
    };
}
