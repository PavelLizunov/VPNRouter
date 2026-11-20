using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.App.Localization;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    internal AppSettings CaptureConnectionSettings() => VpnEngine.CopySessionSettings(_settings);

    private void OnAutoFailoverMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        Dispatcher.UIThread.Post(() =>
        {
            var text = "⚠ " + message;
            StatusText = text;
            _lastConnectionAlert = text;
            RaiseSimpleAlertProps();
            _logger.Warning("[VM] Displayed automatic failover notification");
        });
    }

    private void OnTrueSplitEngagedChanged(bool engaged) =>
        Dispatcher.UIThread.Post(() => IsTrueSplitActive = engaged);

    private void OnTrueSplitStateChanged(TrueSplitState state, string reason) =>
        Dispatcher.UIThread.Post(() =>
        {
            TrueSplitStatusText = state switch
            {
                TrueSplitState.Active => Strings.TrueSplitActive,
                TrueSplitState.DriverMissing => Strings.TrueSplitMissing,
                TrueSplitState.Starting => Strings.TrueSplitStarting,
                TrueSplitState.Fallback => FormatTrueSplitFallback(reason),
                _ => Strings.TrueSplitNotApplicable,
            };
            IsTrueSplitActive = state is TrueSplitState.Active;
            IsTrueSplitProblem = state is TrueSplitState.DriverMissing or TrueSplitState.Fallback;
            _logger.Information("[VM] TrueSplit state={State}", state);
        });

    private void MarkTrueSplitServiceManagedIfNeeded()
    {
        if (!IsSplitTunnel || !IsRoutingAppsModeExclude) return;
        IsTrueSplitActive = false;
        IsTrueSplitProblem = true;
        TrueSplitStatusText = Strings.TrueSplitServiceManaged;
    }

    private static string FormatTrueSplitFallback(string reason)
    {
        if (reason.Contains("needs a resolvable app path", StringComparison.OrdinalIgnoreCase))
            return Strings.TrueSplitNoRunningApps;
        if (reason.Contains("err=5", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("MULLVADSPLITTUNNEL", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("0x80320009", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(reason) ? Strings.TrueSplitDeviceBusy : reason;
        if (!string.IsNullOrWhiteSpace(reason))
            return $"{Strings.TrueSplitFallback} {reason}";
        return Strings.TrueSplitFallback;
    }

    private void OnEngineStatus(string status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (status.StartsWith("Connected") || status.StartsWith("VPN Router is running"))
            {
                if (!IsConnected) return;

                ConnectButtonText = Strings.StopVPN;
                StartSubRefreshTimer();
                OnConnectedForProblems(true);
                RestoreConnectedStatus();
            }
            else if (status == "Stopped")
            {
                if (IsConnecting) return;

                IsConnected = false;
                IsConnecting = false;
                ConnectButtonText = Strings.StartVPN;
                StatusText = Strings.NotConnected;
                StopSubRefreshTimer();
                RefreshActiveIndicator();
                HasPendingAppChanges = false;
            }
            else
            {
                if (IsConnected && status.StartsWith("Applied (", StringComparison.Ordinal))
                {
                    OnConnectedForProblems(true);
                    RestoreConnectedStatus();
                    return;
                }
                StatusText = status;
            }
        });
    }

    private void OnEngineConnected(int pid)
    {
        if (_disposed) return;
        var readinessGuard = _engine.CaptureReadinessGuard(pid);

        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            if (!readinessGuard()) return;
            if (IsConnecting || _isReconnecting) return;

            _displayExternalTunnel = false;
            IsConnected = true;
            ConnectButtonText = Strings.StopVPN;
            StartSubRefreshTimer();
            RestoreConnectedStatus();
        });
    }

    [RelayCommand]
    private async Task RestartTrueSplitAsync()
    {
        if (!IsConnected || !_engine.IsRunning) return;
        SaveSettings();
        _settings = _settingsStore.Load(AppPaths.ConfigYamlPath);
        var capturedSettings = CaptureConnectionSettings();
#if PLATFORM_WINDOWS
        await Task.Run(() =>
        {
            try
            {
                if (!VPNRouter.App.Services.WindowsServiceHelper.IsRunning()) return;
                var result = VPNRouter.App.Services.WindowsServiceHelper.Stop();
                if (result.Success)
                    _logger.Information("[VM] TrueSplit retry stopped VPNRouter Service before re-engage");
                else
                    _logger.Warning("[VM] TrueSplit retry could not stop VPNRouter Service");
            }
            catch (Exception)
            {
                _logger.Debug("[VM] TrueSplit retry service-stop probe failed");
            }
        });
#endif
        await Task.Run(() => _engine.RestartTrueSplitAsync(capturedSettings, CancellationToken.None));
    }

    // The window lost track of a tunnel that is running (a routing change restarts sing-box). Show the truth instead of
    // letting the next press of "Connect" stop it.
    private void AdoptRunningEngine()
    {
        _logger.Information("[VM] Engine is running while the window showed Not connected - showing Connected");
        _displayExternalTunnel = false;
        IsConnected = true;
        ConnectButtonText = Strings.StopVPN;
        StartSubRefreshTimer();
        RestoreConnectedStatus();
    }

    [RelayCommand]
    private Task ToggleConnectionAsync() => ToggleConnectionCoreAsync(null);

    private async Task ToggleConnectionCoreAsync(AppSettings? submitted)
    {
        var toggle = ConnectionSyncPolicy.DecideToggle(
            IsConnected,
            inTransition: IsConnecting || IsApplying || _isReconnecting,
            inProcessEngineRunning: _engine.HasActiveTunnel);
        if (toggle == ToggleAction.AdoptRunningEngine)
        {
            AdoptRunningEngine();
            return;
        }

        if (toggle == ToggleAction.Ignore)
        {
            _logger.Debug(
                "[VM] ToggleConnectionAsync ignored - transition already in progress (IsConnecting={IsConnecting}, IsApplying={IsApplying}, IsReconnecting={IsReconnecting})",
                IsConnecting,
                IsApplying,
                _isReconnecting);
            return;
        }

        if (toggle == ToggleAction.Stop)
        {
            if (submitted != null) return;
            SmpErrorText = string.Empty;
            IsConnecting = true;
            StatusText = Strings.Stopping;
            try
            {
                await Task.Run(() =>
                {
                    _engine.Stop();

                    try { OrphanCleanup.KillOrphans(logger: null, respectTunLock: false); }
                    catch (Exception ex) { _logger.Debug(ex, "[VM] OrphanCleanup on stop"); }

#if PLATFORM_WINDOWS
                    try { TryStopVpnRouterService(); }
                    catch (Exception ex) { _logger.Debug(ex, "[VM] sc stop on disconnect"); }
#endif
                });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[VM] Error during Stop");
                IsConnecting = false;
                IsConnected = _engine.HasActiveTunnel;
                ConnectButtonText = IsConnected ? Strings.StopVPN : Strings.StartVPN;
                StatusText = Strings.TunnelStopUnconfirmed;
                SmpErrorText = Strings.TunnelStopUnconfirmed;
                return;
            }
            IsConnected = false;
            IsConnecting = false;
            ConnectButtonText = Strings.StartVPN;
            StatusText = Strings.NotConnected;
            _lastSuccessfulConnectAt = DateTime.MinValue;
            return;
        }

        var startOverride = submitted == null ? null : SimpleTunnelStart;
#if PLATFORM_WINDOWS
        if (startOverride == null && VPNRouter.App.Services.WindowsServiceHelper.IsRunning()
            && TunOwnershipLock.IsOwnedByAnyone())
        {
            DetectServiceManagedVpn();
            if (IsConnected)
            {
                _logger.Information("[VM] Connect adopted Windows Service-owned VPN instead of starting a parallel engine");
                return;
            }
        }
#endif

        {
            SmpErrorText = string.Empty;
            IsConnecting = true;
            StatusText = Strings.Starting;
            ConnectButtonText = Strings.Starting;

            if (submitted == null)
            {
                SaveSettings();
                _settings = _settingsStore.Load(AppPaths.ConfigYamlPath);

                var aggregatedServers = _settings.App.Subscriptions
                    .Where(s => s.Enabled)
                    .SelectMany(s => s.Servers)
                    .ToList();
                if (IsSubscribeMode && aggregatedServers.Count > 0)
                {
                    _settings.Vless.Servers = aggregatedServers;
                    _settings.Vless.ActiveServer = _settings.App.ActiveSubscriptionServer;
                    _logger.Information(
                        "[VM] Connecting with {Count} aggregated subscription servers", aggregatedServers.Count);
                }
            }

            try
            {
                var capturedSettings = submitted ?? CaptureConnectionSettings();
                var skipConflictCheck = _skipVpnConflictThisSession;
                if (startOverride == null)
                    await PrepareColdStartAsync();

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(
                    Internals.TwoPhaseStartCoordinator.DefaultPhaseABudget.TotalSeconds +
                    Internals.TwoPhaseStartCoordinator.DefaultPhaseBBudget.TotalSeconds));
                var start = startOverride ?? RunTwoPhaseStartAsync;
                var (startTask, outcome) = await start(capturedSettings, cts.Token, skipConflictCheck);

                if (outcome == Internals.TwoPhaseStartOutcome.Connected)
                {
                    try { await startTask; } catch {
 }
                    _displayExternalTunnel = false;
                    IsConnected = true;
                    IsConnecting = false;
                    _lastSuccessfulConnectAt = DateTime.UtcNow;
                    ConnectButtonText = Strings.StopVPN;
                    StartSubRefreshTimer();
                    RestoreConnectedStatus();
                    ConflictingVpnWarningText = string.Empty;
                }
                else if (outcome == Internals.TwoPhaseStartOutcome.StartTaskCompleted)
                {
                    await startTask;
                    IsConnecting = false;
                    _logger.Warning("[VM] StartAsync returned without firing SingBoxStarted — leaving state to OnEngineStatus");
                }
                else if (outcome == Internals.TwoPhaseStartOutcome.PhaseATimeout)
                {
                    _logger.Error("[VM] Phase A (sing-box launch) timed out after {N}s — sing-box never reported started. Possible cause: slow firewall rule creation, missing NetAdapter PowerShell module (Windows 10 LTSC / Server SKUs), or pre-start TUN cleanup hang. Stopping engine.",
                        (int)Internals.TwoPhaseStartCoordinator.DefaultPhaseABudget.TotalSeconds);
                    await AbortStartAsync(Strings.StartTimeoutPhaseA);
                    return;
                }
                else if (outcome == Internals.TwoPhaseStartOutcome.PhaseBTimeout)
                {
                    _logger.Error("[VM] Phase B (TUN warm-up) timed out after {N}s — sing-box started but Connected event never fired. Possible cause: wintun driver issue, network interface gone, or warmup probe blocked. Stopping engine.",
                        (int)Internals.TwoPhaseStartCoordinator.DefaultPhaseBBudget.TotalSeconds);
                    await AbortStartAsync(Strings.StartTimeoutPhaseB);
                    return;
                }
                else
                {
                    _logger.Error("[VM] Two-phase start cancelled by outer CTS");
                    await AbortStartAsync(Strings.StartTimeoutPhaseA);
                    return;
                }
            }
            catch (TunOwnershipException)
            {
                _logger.Warning("[VM] TUN adapter owned by another VPNRouter instance");
                try { await Task.Run(() => _engine.Stop()); } catch { }
                IsConnected = false;
                IsConnecting = false;
                StatusText = IsRussian
                    ? "VPN адаптер занят. Попробуйте ещё раз."
                    : "TUN adapter busy. Try again.";
                ConnectButtonText = Strings.StartVPN;
                return;
            }
            catch (VPNRouter.Core.Services.ConflictingVpnException cvex)
            {
                _logger.Warning(
                    "[VM] Conflicting VPN detected: {Count} processes ({First})",
                    cvex.Conflicts.Count,
                    cvex.Conflicts.Count > 0 ? cvex.Conflicts[0].ProcessName : "<empty>");
                try { await Task.Run(() => _engine.Stop()); } catch { }
                IsConnecting = false;
                IsConnected = false;
                _lastConflicts = cvex.Conflicts;
                var first = cvex.Conflicts.Count > 0 ? cvex.Conflicts[0] : null;
                ConflictingVpnWarningText = first != null
                    ? Strings.ConflictOtherVpnDetectedMessage(first.ProcessName, first.Pid)
                    : cvex.Message;
                StatusText = Strings.ConflictOtherVpnDetectedTitle;
                ConnectButtonText = Strings.StartVPN;
                return;
            }
            catch (OperationCanceledException)
            {
                _logger.Error("[VM] OperationCanceledException out of two-phase start path — treating as Phase A timeout. Stopping engine.");
                await AbortStartAsync(Strings.StartTimeoutPhaseA);
                return;
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to start VPN ({ExceptionType})", ex.GetType().Name);
                IsConnecting = false;
                if (IsConnected && _engine.IsRunning)
                {
                    ConnectButtonText = Strings.StopVPN;
                    _logger.Warning("[VM] start path threw but engine is running and already typed ready — keeping connected status instead of a stale 'Failed to start VPN'");
                }
                else
                {
                    try { await Task.Run(() => _engine.Stop()); } catch { }
                    IsConnected = false;
                    StatusText = $"{Strings.FailedStartVpn} {ex.Message}";
                    ConnectButtonText = Strings.StartVPN;
                }
                return;
            }
        }
    }


#if PLATFORM_WINDOWS
    private static bool TryStopVpnRouterService()
    {
        return Services.WindowsServiceHelper.IsRunning() && Services.WindowsServiceHelper.Stop().Success;
    }
#endif

    private async Task PrepareColdStartAsync()
    {
#if PLATFORM_WINDOWS
        await Task.Run(() =>
        {
            // A cold start may clean up processes only while the TUN is unclaimed.
            if (TunOwnershipLock.ProbeOwnership() != TunOwnershipStatus.Free) return;
            try { OrphanCleanup.KillOrphans(); } catch { }
            try { if (TryStopVpnRouterService()) Thread.Sleep(2000); }
            catch { }
        });
#endif
        if (OperatingSystem.IsMacOS())
            await Task.Run(EnsureMacSudoAccess);
    }

    private async Task<(Task StartTask, Internals.TwoPhaseStartOutcome Outcome)> RunTwoPhaseStartAsync(
        AppSettings capturedSettings, CancellationToken ct, bool skipConflictCheck)
    {
        var startTask = Task.Run(
            () => _engine.StartAsync(capturedSettings, ct, skipConflictCheck),
            ct);

        var outcome = await Internals.TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: handler =>
            {
                void Wrapper(int pid) => handler(pid);
                _engine.SingBoxStarted += Wrapper;
                return () => _engine.SingBoxStarted -= Wrapper;
            },
            subscribeConnected: handler =>
            {
                void Wrapper(int pid) => handler(pid);
                _engine.Connected += Wrapper;
                return () => _engine.Connected -= Wrapper;
            },
            cancellationToken: ct);
        return (startTask, outcome);
    }

    private async Task AbortStartAsync(string statusText)
    {
        SmpErrorText = statusText;
        StatusText = statusText;
        try { await Task.Run(() => _engine.Stop()); } catch { }
        IsConnecting = false;
        IsConnected = false;
        StatusText = statusText;
        ConnectButtonText = Strings.StartVPN;
    }

    private System.Collections.Generic.IReadOnlyList<VPNRouter.Core.Services.ConflictingVpnDetector.ConflictingProcessInfo>
        _lastConflicts = System.Array.Empty<VPNRouter.Core.Services.ConflictingVpnDetector.ConflictingProcessInfo>();

    [RelayCommand]
    private void RefreshConflictingVpn()
    {
        var conflicts = VPNRouter.Core.Services.ConflictingVpnDetector
            .DetectConflictingVpnProcesses(_logger);
        _lastConflicts = conflicts;
        if (conflicts.Count == 0)
        {
            ConflictingVpnWarningText = string.Empty;
            return;
        }
        var first = conflicts[0];
        ConflictingVpnWarningText =
            Strings.ConflictOtherVpnDetectedMessage(first.ProcessName, first.Pid);
    }

    private bool _skipVpnConflictThisSession;

    [RelayCommand]
    private async Task IgnoreVpnConflictAndConnectAsync()
    {
        _skipVpnConflictThisSession = true;
        ConflictingVpnWarningText = string.Empty;
        _logger.Information("[VM] User opted to ignore VPN conflict — retrying Connect with bypass");
        if (!IsConnected && !IsConnecting)
        {
            await ToggleConnectionAsync();
        }
    }

    [RelayCommand]
    private async Task KillConflictingVpnAsync()
    {
        if (_lastConflicts.Count == 0)
        {
            RefreshConflictingVpn();
            if (_lastConflicts.Count == 0) return;
        }

        var killed = 0;
        var failed = 0;
        foreach (var info in _lastConflicts)
        {
            try
            {
                using var proc = System.Diagnostics.Process.GetProcessById(info.Pid);
                proc.Kill();
                try { await proc.WaitForExitAsync(System.Threading.CancellationToken.None); } catch { }
                killed++;
                _logger.Information("[VM] Killed conflicting VPN: {Name} (PID {Pid})",
                    info.ProcessName, info.Pid);
            }
            catch (System.ArgumentException)
            {
                killed++;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.Warning(ex,
                    "[VM] Failed to kill conflicting VPN {Name} (PID {Pid}) — likely needs admin rights / protected process",
                    info.ProcessName, info.Pid);
            }
        }

        RefreshConflictingVpn();

        try { ForceRefreshRuntimeStatus(); } catch { }

        if (_lastConflicts.Count == 0)
        {
            ConflictingVpnWarningText = string.Empty;
            _logger.Information("[VM] Conflict cleared ({Killed} killed, {Failed} failed)",
                killed, failed);
        }
        else if (failed > 0)
        {
            ConflictingVpnWarningText =
                Strings.ConflictKillPartialFailure(killed, failed);
        }
    }

    private void RefreshActiveIndicator()
    {
        var active = IsConnected ? SmpConfigServer : null;
        foreach (var row in Servers) row.IsActive = ReferenceEquals(row, active);
        foreach (var row in SubscriptionServers) row.IsActive = ReferenceEquals(row, active);
        ActiveServerChanged?.Invoke(active);
    }

    private void DetectServiceManagedVpn()
    {
        try
        {
            var singboxRunning = VPNRouter.Core.Services.RuntimeStatusDetector.IsVpnRunning();
            if (!singboxRunning) return;

            var tunOwned = TunOwnershipLock.IsOwnedByAnyone();
            if (!tunOwned)
            {
                return;
            }

            MarkConnectedViaService();
            _logger.Information("[VM] Detected a running VPN outside the desktop engine (sing-box alive + TUN owned)");
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[VM] DetectServiceManagedVpn failed");
        }
    }

    private bool IsServiceManagedVpn => IsConnected && !(_engine?.HasActiveTunnel ?? false);

    private void RestoreConnectedStatus()
    {
        if (!IsConnected) return;
        SyncStatsTunnel();
        RefreshActiveIndicator();
        OnPropertyChanged(nameof(SimpleStatusDescription));
        OnPropertyChanged(nameof(SmpActiveServerLine));
        OnPropertyChanged(nameof(SimpleActiveOutboundLine));
        OnPropertyChanged(nameof(SimpleActiveOutboundIsSuspect));
        OnPropertyChanged(nameof(SimpleActiveOutboundNormalVisible));
        OnPropertyChanged(nameof(SimpleActiveOutboundSuspectVisible));

        if (DisplayedTunnel is not { } tunnel)
        {
            StatusText = Strings.SmpStatusConnectedNoDetails;
            return;
        }
        var configLabel = tunnel.ConfigMode switch { "subscribe" => "subscribe", "generated" => "manual", _ => "custom" };
        var (serverName, serverIp) = AcceptedServerLabel(tunnel);
        StatusText = Strings.Connected($"{configLabel}/{tunnel.RoutingMode}", serverName, serverIp);
    }

    private bool _isReconnecting;

    private enum ReconnectIntent
    {
        Follow,
        ManualVless,
        Subscription,
        CustomConfig
    }

    private void WarnServiceManagedReconnect(string newServerName)
    {
        MarkRoutingSettingsChanged();
        try { SaveSettings(); }
        catch (Exception ex)
        {
            _logger.Warning("[VM] Service-managed selection save failed ({ErrorType})", ex.GetType().Name);
            StatusText = Strings.ApplyFailed;
            return;
        }
        StatusText = IsRussian
            ? $"Выбран {newServerName}. Нажмите «Применить», чтобы подтвердить переключение в службе."
            : $"Selected {newServerName}. Click Apply to confirm the switch with the service.";
    }

    private async Task ReconnectAsync(string configName, ReconnectIntent intent = ReconnectIntent.Follow)
    {
        if (_isReconnecting) return;
        if (IsServiceManagedVpn)
        {
            WarnServiceManagedReconnect(configName);
            return;
        }
        _isReconnecting = true;
        IsConnecting = true;
        StatusText = IsRussian
            ? $"Переключение на {configName}..."
            : $"Switching to {configName}...";

        _logger.Information(
            "[VM] Reconnecting (intent={Intent}, vlessMode={VlessMode}, subscribeMode={SubscribeMode})",
            intent, IsVlessMode, IsSubscribeMode);

        bool startingCold = false;
        try
        {
            if (intent == ReconnectIntent.ManualVless)
            {
                IsSubscribeMode = false;
                IsVlessMode = true;
            }
            else if (intent == ReconnectIntent.Subscription)
            {
                IsSubscribeMode = true;
                IsVlessMode = false;
            }
            else if (intent == ReconnectIntent.CustomConfig)
            {
                IsSubscribeMode = false;
                IsVlessMode = false;
            }

            SaveSettings();
            _settings = _settingsStore.Load(AppPaths.ConfigYamlPath);

            _logger.Information(
                "[VM] Reloaded reconnect settings with {Count} configured servers",
                _settings.Vless.Servers?.Count ?? 0);

            var aggregated = _settings.App.Subscriptions
                .Where(s => s.Enabled)
                .SelectMany(s => s.Servers)
                .ToList();

            if (intent == ReconnectIntent.ManualVless)
            {
                _settings.App.ConfigMode = "generated";
                _settings.Vless.Servers = Servers.Select(s => s.ToEntry()).ToList();
                _settings.Vless.ActiveServer = configName;
                _logger.Information(
                    "[VM] Reconnecting with {Count} manual servers", _settings.Vless.Servers.Count);
            }
            else if ((intent == ReconnectIntent.Subscription || (intent == ReconnectIntent.Follow && IsSubscribeMode))
                     && aggregated.Count > 0)
            {
                _settings.Vless.Servers = aggregated;
                _settings.Vless.ActiveServer = _settings.App.ActiveSubscriptionServer;
                _logger.Information(
                    "[VM] Reconnecting with {Count} aggregated subscription servers", aggregated.Count);
            }

            var capturedSettings = CaptureConnectionSettings();
            var skipConflictCheck = _skipVpnConflictThisSession;
            if (_engine.HasActiveTunnel)
            {
                _logger.Information("[VM] ReconnectAsync applying new config via ApplyAsync(forceRestart=true)");
                var applied = await Task.Run(() => _engine.ApplyAsync(
                    capturedSettings,
                    CancellationToken.None,
                    forceRestart: true));
                if (applied)
                {
                    RestoreConnectedStatus();
                    return;
                }

                if (_engine.HasActiveTunnel)
                {
                    // Generation can reject new routing rules while the previous tunnel is healthy.
                    StatusText = Strings.ApplyFailed;
                    return;
                }
                _logger.Warning("[VM] ReconnectAsync ApplyAsync returned false and sing-box stopped; retrying startup");
            }

            if (!await Task.Run(_engine.TryPrepareColdReconnect))
            {
                StatusText = Strings.ApplyFailed;
                return;
            }
            startingCold = true;
            const int maxRetries = 3;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(
                        Internals.TwoPhaseStartCoordinator.DefaultPhaseABudget.TotalSeconds +
                        Internals.TwoPhaseStartCoordinator.DefaultPhaseBBudget.TotalSeconds));
                    var (startTask, outcome) = await RunTwoPhaseStartAsync(capturedSettings, cts.Token, skipConflictCheck);

                    if (outcome == Internals.TwoPhaseStartOutcome.PhaseATimeout)
                    {
                        _logger.Error("[VM] Reconnect: Phase A (sing-box launch) timed out after {N}s",
                            (int)Internals.TwoPhaseStartCoordinator.DefaultPhaseABudget.TotalSeconds);
                        try { await Task.Run(() => _engine.Stop()); } catch { }
                        IsConnected = false;
                        StatusText = Strings.StartTimeoutPhaseA;
                        ConnectButtonText = Strings.StartVPN;
                        return;
                    }
                    if (outcome == Internals.TwoPhaseStartOutcome.PhaseBTimeout)
                    {
                        _logger.Error("[VM] Reconnect: Phase B (TUN warm-up) timed out after {N}s",
                            (int)Internals.TwoPhaseStartCoordinator.DefaultPhaseBBudget.TotalSeconds);
                        try { await Task.Run(() => _engine.Stop()); } catch { }
                        IsConnected = false;
                        StatusText = Strings.StartTimeoutPhaseB;
                        ConnectButtonText = Strings.StartVPN;
                        return;
                    }
                    await startTask;
                    break;
                }
                catch (TunOwnershipException) when (attempt < maxRetries)
                {
                    _logger.Warning("[VM] Reconnect: TUN lock stolen by service, retry {A}/{M}", attempt, maxRetries);
                    await Task.Delay(ServiceReleaseRetryDelayMs);
                }
            }

            try { RefreshActiveIndicator(); }
            catch (Exception) { _logger.Debug("[VM] Reconnect: RefreshActiveIndicator failed"); }
        }
        catch (OperationCanceledException)
        {
            if (!startingCold && _engine.HasActiveTunnel)
            {
                StatusText = Strings.ApplyFailed;
                return;
            }
            _logger.Error("[VM] Reconnect timed out");
            try { await Task.Run(() => _engine.Stop()); } catch { }
            IsConnected = false;
            StatusText = IsRussian
                ? "Таймаут переключения. Попробуйте снова."
                : "Switch timed out. Try again.";
            ConnectButtonText = Strings.StartVPN;
        }
        catch (TunOwnershipException)
        {
            IsConnected = false;
            StatusText = IsRussian
                ? "VPN адаптер занят другим экземпляром"
                : "TUN adapter owned by another instance";
            ConnectButtonText = Strings.StartVPN;
        }
        catch (Exception ex)
        {
            _logger.Error("[VM] Reconnect failed ({ExceptionType})", ex.GetType().Name);
            IsConnected = _engine.HasActiveTunnel;
            StatusText = $"{Strings.FailedStartVpn} {ex.Message}";
            ConnectButtonText = Strings.StartVPN;
        }
        finally
        {
            IsConnecting = false;
            _isReconnecting = false;
        }
    }
}
