using Avalonia.Controls;
using VPNRouter.App.Services;
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
    internal Func<string, string, bool, AssistantObservation, CancellationToken, Task<AssistantStep>> StepAssistantAsync { get; init; }
        = SubscriptionAssistantClient.Shared.StepAsync;
    internal Func<string, string?> ResolveAssistantApplication { get; init; } = AssistantApplicationTarget.ResolveExact;
    internal bool IsInvokingAssistantControl { get; private set; }

    private sealed record AssistantUndo(RoutingSelection Previous, RoutingSelection Applied, string Revision,
        bool WasConnected, AcceptedTunnelInfo? Session);
    private sealed record AssistantSnapshot(AppSettings Settings, string Revision, int RoutingRevision,
        AcceptedTunnelInfo? Session, int? NormalizedMtu = null);
    private sealed record AssistantChangeResult(string? Error, bool SettingsChangedAfterCommit = false,
        AssistantCandidateRejection? Rejection = null);

    private sealed record RoutingSelection(string Mode, string AppsMode, bool Initialized, string[] Include, string[] Exclude,
        string Profile, bool BypassRussianTraffic)
    {
        internal static RoutingSelection Capture(AppSettings settings) =>
            new(settings.App.RoutingMode, settings.App.RoutingAppsMode, settings.App.RoutingAppsIncludeInitialized,
                [.. (settings.App.RoutingAppsInclude ?? [])], [.. (settings.App.RoutingAppsExclude ?? [])], settings.ActiveProfile, settings.App.BypassRussianTraffic);

        internal bool Matches(AppSettings settings) =>
            Mode == settings.App.RoutingMode && AppsMode == settings.App.RoutingAppsMode
            && Initialized == settings.App.RoutingAppsIncludeInitialized && Profile == settings.ActiveProfile
            && BypassRussianTraffic == settings.App.BypassRussianTraffic
            && Exclude.SequenceEqual(settings.App.RoutingAppsExclude ?? [], StringComparer.Ordinal)
            && Include.SequenceEqual(settings.App.RoutingAppsInclude ?? [], StringComparer.Ordinal);

        internal void ApplyTo(AppSettings settings)
        {
            settings.App.RoutingMode = Mode;
            settings.App.RoutingAppsMode = AppsMode;
            settings.App.RoutingAppsIncludeInitialized = Initialized;
            settings.App.RoutingAppsInclude = [.. Include];
            settings.App.RoutingAppsExclude = [.. Exclude];
            settings.ActiveProfile = Profile;
            settings.App.BypassRussianTraffic = BypassRussianTraffic;
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

    internal async Task<string> DiagnoseAssistantCrashAsync(CancellationToken ct)
    {
        var evidence = await Task.Run(() => AssistantCrashEvidence.ReadLatest(AppPaths.DataDir), ct);
        if (evidence == null) return Strings.AssistantNoCrash;
        var subscription = SelectAssistantSubscription(_settings.App);
        if (subscription == null) return Strings.AssistantSubscription;

        var result = await StepAssistantAsync(subscription.Url, "Analyze the last VPNRouter crash", IsRussian,
            new AssistantObservation(IsConnected, IsConnecting, IsSimpleMode, [], null, evidence), ct);
        ct.ThrowIfCancellationRequested();
        return result is { Status: AssistantStepStatus.Diagnosis, Text: { } summary }
            ? summary
            : AssistantStepMessage(result.Status);
    }

    internal async Task<string> RunAssistantGoalAsync(string goal, Window window, Control source,
        Func<string, CancellationToken, Task<bool>> confirm, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(goal) || goal.Length > 384 || goal.Any(char.IsControl))
            return Strings.AssistantInvalidGoal;
        if (IsServiceManagedVpn || _displayExternalTunnel) return Strings.AssistantServiceManaged;
        if (!CanRunAssistantChange()) return Strings.AssistantBusy;

        var snapshot = ReadAssistantSnapshot();
        if (snapshot == null || !_engine.IsAssistantSessionCurrent(snapshot.Session))
            return Strings.AssistantSettingsChanged;
        var subscription = SelectAssistantSubscription(snapshot.Settings.App);
        if (subscription == null) return Strings.AssistantSubscription;

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromMinutes(2));
        var token = budget.Token;
        var state = new AssistantObservation(IsConnected, false, IsSimpleMode, [], null);
        if (AssistantIntent.Parse(goal) is not { } intent)
            return await SuggestAssistantCommandAsync(goal, subscription.Url, snapshot, state, token);

        var settings = snapshot.Settings.App;
        var controlName = intent.Control;
        string? process = null;
        AppSettings? route = null;
        bool direct = intent.Control == "RouteApplicationDirect";
        if (intent.Application is { } app)
        {
            if (settings.ConfigMode is not ("generated" or "subscribe") || settings.CustomRules.Any(rule => rule.Enabled))
                return Strings.AssistantRuleConflict;
            process = ResolveAssistantApplication(app);
            if (process == null) return Strings.AssistantAppNeeded;
            if (PrepareAssistantAppRoute(snapshot, process, direct, out route) is { } reason) return reason;
            controlName += AssistantApplicationTarget.IsDefaultBrowserRequest(app) ? "ForDefaultBrowser" : "ForResolvedTarget";
        }
        if (controlName == "SetStrictDns" && settings.ConfigMode is not ("generated" or "subscribe")
            || controlName == "OpenTools" && !IsToolsAvailable) return Strings.AssistantUnsupportedGoal;

        bool? current = controlName switch
        {
            "SetVpnConnected" => IsConnected,
            "SetStrictDns" => settings.StrictDns,
            "SetFlushDnsOnStart" => settings.FlushDnsOnStart,
            _ => null,
        };
        if (intent.Desired is bool desired && current == desired)
            return controlName == "SetVpnConnected" ? VerifiedVpnStatus(desired) : Strings.AssistantNoChanges;

        var control = new AssistantControl(1, controlName, current.HasValue ? "toggle" : "press", current);
        var observation = state with { Controls = [control] };
        var step = await StepAssistantAsync(subscription.Url, goal, IsRussian, observation, token);
        token.ThrowIfCancellationRequested();
        if (!StillCurrent()) return Strings.AssistantSettingsChanged;
        if (step.Status != AssistantStepStatus.Act) return AssistantStepMessage(step.Status);
        if (step.Control != control || step.Toggle != intent.Desired || step.Text != null)
            return Strings.AssistantProposalMismatch;

        string description = Strings.AssistantChangeDescription(intent.Control, intent.Desired, process,
            observation.Connected);
        if (route != null)
        {
            if (route.ActiveProfile != snapshot.Settings.ActiveProfile || route.App.RoutingMode != settings.RoutingMode)
                description += Strings.AssistantSplitProfileChange;
            if (settings.BypassRussianTraffic && !route.App.BypassRussianTraffic) description += Strings.AssistantRegionalBypassChange;
        }
        bool accepted = await confirm(description, token);
        token.ThrowIfCancellationRequested();
        if (!accepted) return Strings.AssistantNoChanges;
        if (!StillCurrent() || (intent.Application is { } requested
            && ResolveAssistantApplication(requested) != process))
            return Strings.AssistantSettingsChanged;

        if (process != null && route != null)
        {
            if (PrepareAssistantAppRoute(snapshot, process, direct, out var latest) is { } reason) return reason;
            if (!RoutingSelection.Capture(route).Matches(latest)) return Strings.AssistantSettingsChanged;
            return await ApplyAssistantAppRouteAsync(snapshot, latest, process, direct, token);
        }
        return await ExecuteAssistantStateAsync(snapshot, control, intent.Desired, token);

        bool StillCurrent() => CanRunAssistantChange() && AssistantSnapshotStillCurrent(snapshot)
            && _engine.IsAssistantSessionCurrent(snapshot.Session) && IsConnected == observation.Connected
            && IsSimpleMode == observation.Simple && ReferenceEquals(TopLevel.GetTopLevel(source), window)
            && ReferenceEquals(source.DataContext, this);
    }

    private static readonly string[] AssistantSections =
        ["OpenServerSettings", "OpenSubscriptions", "OpenNetworkSettings", "OpenApplications", "OpenTools", "OpenFreeConfigurations"];

    private async Task<string> ExecuteAssistantStateAsync(AssistantSnapshot snapshot, AssistantControl target,
        bool? value, CancellationToken token)
    {
        if (!CanRunAssistantChange()) return Strings.AssistantBusy;
        int tab = Array.IndexOf(AssistantSections, target.Name);
        if (target.Kind == "press" && tab >= 0)
        {
            IsInvokingAssistantControl = true;
            try
            {
                IsSimpleMode = false;
                SelectedTabIndex = tab;
            }
            finally { IsInvokingAssistantControl = false; }
            token.ThrowIfCancellationRequested();
            return !IsSimpleMode && SelectedTabIndex == tab ? Strings.AssistantPageOpened : Strings.AssistantSettingsChanged;
        }
        if (value is not bool desired) return Strings.AssistantUnavailable;
        if (target.Name == "SetVpnConnected")
        {
            if (IsConnected != desired) await ToggleConnectionCoreAsync(null, token);
            token.ThrowIfCancellationRequested();
            return VerifiedVpnStatus(desired);
        }

        bool strict = target.Name == "SetStrictDns";
        if (!strict && target.Name != "SetFlushDnsOnStart") return Strings.AssistantUnavailable;
        var candidate = SettingsLoader.Copy(snapshot.Settings);
        if (strict) candidate.App.StrictDns = desired;
        else candidate.App.FlushDnsOnStart = desired;
        if ((strict ? snapshot.Settings.App.StrictDns : snapshot.Settings.App.FlushDnsOnStart) != desired)
        {
            var result = await ApplyAssistantSettingsAsync(candidate, snapshot, null, snapshot.Session == null, token, preference: target.Name);
            if (result.Error != null) return result.Error;
        }
        var persisted = ReadAssistantSnapshot()?.Settings.App;
        return persisted != null && (strict ? persisted.StrictDns : persisted.FlushDnsOnStart) == desired
            ? Strings.AssistantPreferenceSaved : Strings.AssistantSettingsChanged;
    }

    private string VerifiedVpnStatus(bool desired)
    {
        bool verified = desired
            ? IsConnected && _engine.AcceptedTunnel != null && _engine.SingBoxPid is { } pid && _engine.DescribeReadiness(pid) == null
            : !IsConnected && !SmpStopUnconfirmed && _engine.IsAssistantSessionCurrent(null);
        return verified
            ? (desired ? Strings.AssistantVpnActive : Strings.AssistantVpnStopped)
            : (desired ? Strings.FailedStartVpn : Strings.TunnelStopUnconfirmed);
    }

    internal static bool TryConfigureAssistantRoute(AppSettings candidate, string target, bool direct,
        IEnumerable<string> monitored)
    {
        var app = candidate.App;
        bool full = string.Equals(app.RoutingMode, "full", StringComparison.OrdinalIgnoreCase);
        // The first active exclusion would also activate every saved Full-mode exclusion.
        if (direct && full && app.RoutingAppsExclude?.Any(name => !string.IsNullOrWhiteSpace(name)
            && !string.Equals(name, target, StringComparison.OrdinalIgnoreCase)) == true)
            return false;

        if (full) candidate.ActiveProfile = SimpleSplitProfile;
        if (!direct)
        {
            app.RoutingAppsMode = "include";
            app.RoutingAppsIncludeInitialized = true;
            app.RoutingAppsInclude = [target];
            app.BypassRussianTraffic = false;
        }
        else if (full || string.Equals(app.RoutingAppsMode, "exclude", StringComparison.OrdinalIgnoreCase))
        {
            app.RoutingAppsMode = "exclude";
            app.RoutingAppsExclude ??= [];
            if (!app.RoutingAppsExclude.Contains(target, StringComparer.Ordinal))
            {
                app.RoutingAppsExclude.RemoveAll(name => string.Equals(name, target, StringComparison.OrdinalIgnoreCase));
                app.RoutingAppsExclude.Add(target);
            }
        }
        else
        {
            var included = ConfigGenerator.ResolveEffectiveAppProcesses(monitored, candidate);
            app.RoutingAppsInclude = included.Where(name => !string.Equals(name, target,
                StringComparison.OrdinalIgnoreCase)).ToList();
            app.RoutingAppsIncludeInitialized = true;
        }
        app.RoutingMode = "split";
        return true;
    }

    private string? PrepareAssistantAppRoute(AssistantSnapshot snapshot, string target, bool direct, out AppSettings candidate)
    {
        var saved = snapshot.Settings;
        candidate = SettingsLoader.Copy(saved);
        // An uninitialized include list is supplied by the runtime scan. Without
        // that scan, freezing an empty list could silently drop unrelated routes.
        if (direct && saved.App.RoutingMode == "split" && saved.App.RoutingAppsMode == "include"
            && !saved.App.RoutingAppsIncludeInitialized && (saved.App.RoutingAppsInclude?.Count ?? 0) == 0
            && !_engine.HasAssistantProcessScan)
            return Strings.AssistantRouteScanNeeded;
        if (!TryConfigureAssistantRoute(candidate, target, direct, _engine.MonitoredProcesses))
            return Strings.AssistantDirectFullConflict;
        bool offline = direct && snapshot.Session == null;
        if (RoutingSelection.Capture(candidate).Matches(saved) && (offline ||
            (_engine.AssistantRouteMatches(saved, target, direct) &&
            _engine.SingBoxPid is { } pid && _engine.DescribeReadiness(pid) == null)))
            return Strings.AssistantAlreadySet;
        return null;
    }

    private async Task<string> ApplyAssistantAppRouteAsync(AssistantSnapshot snapshot, AppSettings candidate,
        string target, bool direct, CancellationToken ct)
    {
        bool offline = direct && snapshot.Session == null;
        var result = await ApplyAssistantSettingsAsync(candidate, snapshot, target, offline, ct, direct: direct);
        if (result.Error != null) return result.Error;
        if (offline) return result.SettingsChangedAfterCommit ? Strings.AssistantSettingsChanged : Strings.AssistantPreferenceSaved;
        return result.SettingsChangedAfterCommit ? Strings.AssistantAppliedWithNewerSettings : Strings.AssistantApplied;
    }

    internal async Task<string> UndoAssistantGoalAsync(CancellationToken ct)
    {
        if (IsServiceManagedVpn || _displayExternalTunnel) return Strings.AssistantServiceManaged;
        if (_assistantUndo is not { } undo || !CanRunAssistantChange(allowPendingChanges: true))
            return Strings.AssistantBusy;
        var snapshot = ReadAssistantSnapshot();
        if (snapshot == null
            || (snapshot.Session != null && !ReferenceEquals(snapshot.Session, undo.Session))
            || !_engine.IsAssistantSessionCurrent(snapshot.Session))
        {
            _assistantUndo = null;
            return Strings.AssistantSettingsChanged;
        }

        bool hasNewerSettings = snapshot.Revision != undo.Revision || HasPendingAppChanges;
        bool keepLaterRouting = hasNewerSettings && !undo.Applied.Matches(snapshot.Settings);
        if (keepLaterRouting && snapshot.Session == null)
        {
            _assistantUndo = null;
            return Strings.AssistantSettingsChanged;
        }

        var candidate = SettingsLoader.Copy(snapshot.Settings);
        if (!keepLaterRouting) undo.Previous.ApplyTo(candidate);

        var result = await ApplyAssistantSettingsAsync(candidate, snapshot, null,
            restoreWithoutStart: snapshot.Session == null || !undo.WasConnected || hasNewerSettings, ct,
            keepSavedSettings: keepLaterRouting);
        if (result.Error != null) return result.Error;

        _assistantUndo = null;
        return keepLaterRouting || result.SettingsChangedAfterCommit
            ? Strings.AssistantUndoPreservedNewerSettings : Strings.AssistantUndone;
    }

    private bool CanRunAssistantChange(bool allowPendingChanges = false) =>
        !_disposed && !SafeMode.Enabled && !IsConnecting && !IsApplying
        && !_isReconnecting && !IsServiceManagedVpn && (allowPendingChanges || !HasPendingAppChanges) && !_displayExternalTunnel
        && (!IsConnected || _engine.HasActiveTunnel);

    private AssistantSnapshot? ReadAssistantSnapshot(bool repairMtu = false)
    {
        string yaml;
        try { yaml = File.ReadAllText(AppPaths.ConfigYamlPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        int? normalizedMtu = null;
        var saved = repairMtu ? SettingsLoader.ParseRepairCandidate(yaml, out normalizedMtu)
            : SettingsLoader.ParseLiveCandidate(yaml);
        return saved == null ? null : new(saved, SettingsLoader.GetRevision(yaml),
            Volatile.Read(ref _routingSettingsRevision), _engine.AcceptedTunnel, normalizedMtu);
    }

    private bool AssistantSnapshotStillCurrent(AssistantSnapshot snapshot) =>
        !_disposed && !SafeMode.Enabled && ReadAssistantRevision() == snapshot.Revision
        && Volatile.Read(ref _routingSettingsRevision) == snapshot.RoutingRevision;

    private static string? ReadAssistantRevision()
    {
        try { return SettingsLoader.GetRevision(File.ReadAllText(AppPaths.ConfigYamlPath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private async Task<AssistantChangeResult> ApplyAssistantSettingsAsync(AppSettings candidate,
        AssistantSnapshot snapshot, string? target, bool restoreWithoutStart, CancellationToken ct,
        bool keepSavedSettings = false, bool direct = false, string? preference = null,
        AssistantRepairServer? repairServer = null)
    {
        bool saveOnly = preference == "SetFlushDnsOnStart";
        var previousPid = _engine.SingBoxPid;
        var wasConnected = IsConnected;
        AcceptedTunnelInfo? appliedSession = null;
        string? savedRevision = null;
        int savedRoutingRevision = 0;
        IsApplying = true;
        try
        {
            if (snapshot.Session == null && !restoreWithoutStart && !saveOnly)
                await PrepareColdStartAsync();

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(Internals.TwoPhaseStartCoordinator.DefaultPhaseABudget
                + Internals.TwoPhaseStartCoordinator.DefaultPhaseBBudget);
            budget.Token.ThrowIfCancellationRequested();

            bool applied;
            AssistantCandidateRejection? rejection = null;
            if (saveOnly)
            {
                applied = await Task.Run(() => _engine.SaveAssistantPreferenceAsync(snapshot.Session,
                    () => Commit(candidate, budget.Token), budget.Token));
            }
            else if (restoreWithoutStart)
            {
                applied = await Task.Run(() => _engine.RestoreAssistantSettingsAsync(snapshot.Session,
                    () => Commit(candidate, budget.Token), RestoreAfterFailedStop, budget.Token));
            }
            else
            {
                var runtime = VpnEngine.CopySessionSettings(candidate);
                string? expectedRow = null;
                if (repairServer != null)
                {
                    var feed = runtime.App.Subscriptions.FirstOrDefault(item => item.Enabled && item.Id == repairServer.FeedId);
                    var row = feed?.FindExactRow(runtime.App.ActiveSubscriptionServer, runtime.App.ActiveSubscriptionRow);
                    if (row == null) return new(Strings.AssistantSettingsChanged);
                    row.PinForRepair();
                    expectedRow = runtime.App.ActiveSubscriptionRow;
                }
                applied = await Task.Run(() => _engine.ConnectVerifiedAppRoutingAsync(runtime,
                    new AssistantRouteIntent(snapshot.Session, target, direct, expectedRow, repairServer?.FeedId,
                        reason => rejection = reason),
                    () => Commit(runtime, budget.Token), _skipVpnConflictThisSession, budget.Token));
            }

            if (!applied) return new(Strings.AssistantApplyFailed, Rejection: rejection);

            if (target != null && savedRevision is { } revision)
                _assistantUndo = new(RoutingSelection.Capture(snapshot.Settings), RoutingSelection.Capture(candidate),
                    revision, snapshot.Session != null, appliedSession);

            // Commit already checked the submitted revision under the lifecycle and file locks.
            // Later edits are a separate operation, so retain this session's Undo and do not publish stale controls.
            bool fileChangedAfterCommit = ReadAssistantRevision() != savedRevision;
            bool settingsChangedAfterCommit = fileChangedAfterCommit
                || Volatile.Read(ref _routingSettingsRevision) != savedRoutingRevision;
            if (!_disposed && !settingsChangedAfterCommit)
            {
                _settings = candidate;
                if (preference == "SetFlushDnsOnStart") FlushDnsOnStart = candidate.App.FlushDnsOnStart;
                else if (preference == "SetStrictDns") StrictDns = candidate.App.StrictDns;
                else LoadSettingsIntoUI();
                HasPendingAppChanges = false;
            }
            else if (!_disposed && appliedSession != null)
            {
                // A later Apply must read the user's newer saved configuration, not the assistant's inputs.
                if (fileChangedAfterCommit && ReadAssistantSnapshot() is { } newer)
                {
                    bool simpleMode = IsSimpleMode;
                    _settings = newer.Settings;
                    LoadSettingsIntoUI();
                    IsSimpleMode = simpleMode;
                }
                HasPendingAppChanges = true;
            }
            return new(null, settingsChangedAfterCommit);
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning("DeepSeek setup failed during Apply or Undo ({ExceptionType})", ex.GetType().Name);
            return new(Strings.AssistantApplyFailed);
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

                if (keepSavedSettings)
                {
                    savedRevision = snapshot.Revision;
                    savedRoutingRevision = snapshot.RoutingRevision;
                    return;
                }

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
            if (keepSavedSettings) return;

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

    private static string AssistantStepMessage(AssistantStepStatus status) => status switch
    {
        AssistantStepStatus.Finish => Strings.AssistantUnconfirmed,
        AssistantStepStatus.Clarify => Strings.AssistantUnsupportedGoal,
        AssistantStepStatus.AccessDenied => Strings.AssistantAccessDenied,
        AssistantStepStatus.EndpointUnavailable => Strings.AssistantEndpointUnavailable,
        AssistantStepStatus.RateLimited => Strings.AssistantRateLimited,
        AssistantStepStatus.InvalidRequest => Strings.AssistantInvalidGoal,
        _ => Strings.AssistantUnavailable,
    };
}
