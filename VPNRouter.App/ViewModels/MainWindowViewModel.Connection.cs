using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.App.Localization;
using VPNRouter.Core;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    private void OnAutoFailoverMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        Dispatcher.UIThread.Post(() =>
        {
            var text = "⚠ " + message;
            StatusText = text;
            _lastConnectionAlert = text;
            RaiseSimpleAlertProps();
            _logger.Warning("[VM] AutoFailover surfaced to user: {Message}", message);
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
            _logger.Information("[VM] TrueSplit state={State}: {Reason}", state, reason);
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
                OnIsConnectedChanged(true);
                RefreshActiveIndicator();
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
                    OnIsConnectedChanged(true);
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

            IsConnected = true;
            ConnectButtonText = Strings.StopVPN;
            StartSubRefreshTimer();
            RefreshActiveIndicator();
            RestoreConnectedStatus();
        });
    }

    [RelayCommand]
    private async Task RestartTrueSplitAsync()
    {
        if (!IsConnected || !_engine.IsRunning) return;
#if PLATFORM_WINDOWS
        await Task.Run(() =>
        {
            try
            {
                if (!VPNRouter.App.Services.WindowsServiceHelper.IsRunning()) return;
                var result = VPNRouter.App.Services.WindowsServiceHelper.Stop();
                if (result.Success)
                    _logger.Information("[VM] TrueSplit retry stopped VPNRouter Service before re-engage: {Message}", result.Message);
                else
                    _logger.Warning("[VM] TrueSplit retry could not stop VPNRouter Service: {Message}", result.Message);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[VM] TrueSplit retry service-stop probe failed");
            }
        });
#endif
        SaveSettings();
        _settings = _settingsStore.Load(AppPaths.ConfigYamlPath);
        await Task.Run(() => _engine.RestartTrueSplitAsync(_settings, CancellationToken.None));
    }

    [RelayCommand]
    private async Task ToggleConnectionAsync()
    {
        if (IsConnecting || IsApplying || _isReconnecting)
        {
            _logger.Debug(
                "[VM] ToggleConnectionAsync ignored - transition already in progress (IsConnecting={IsConnecting}, IsApplying={IsApplying}, IsReconnecting={IsReconnecting})",
                IsConnecting,
                IsApplying,
                _isReconnecting);
            return;
        }

        if (IsConnected || _engine.IsRunning)
        {
            IsConnecting = true;
            StatusText = Strings.Stopping;
            try
            {
                await Task.Run(() =>
                {
                    try { _engine.Stop(); }
                    catch (Exception ex) { _logger.Debug(ex, "[VM] _engine.Stop"); }

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
            }
            finally
            {
                IsConnected = false;
                IsConnecting = false;
                ConnectButtonText = Strings.StartVPN;
                StatusText = Strings.NotConnected;
                _lastSuccessfulConnectAt = DateTime.MinValue;
            }
            return;
        }

#if PLATFORM_WINDOWS
        if (VPNRouter.App.Services.WindowsServiceHelper.IsRunning()
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
            IsConnecting = true;
            StatusText = Strings.Starting;
            ConnectButtonText = Strings.Starting;

            await Task.Run(() =>
            {
                try
                {
                    if (_engine.IsRunning)
                        _engine.Stop();
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[VM] Pre-start engine stop");
                }

                try { OrphanCleanup.KillOrphans(logger: null, respectTunLock: false); } catch { }

#if PLATFORM_WINDOWS
                try { if (TryStopVpnRouterService()) Thread.Sleep(2000); }
                catch { }
#endif
            });

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
                    "[VM] ToggleConnectionAsync.Connect.Subscription: aggregated {N} servers, ActiveServer={A}, ConfigMode preserved=subscribe",
                    aggregatedServers.Count, _settings.Vless.ActiveServer);
            }

            if (OperatingSystem.IsMacOS())
                await Task.Run(EnsureMacSudoAccess);

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(
                    Internals.TwoPhaseStartCoordinator.DefaultPhaseABudget.TotalSeconds +
                    Internals.TwoPhaseStartCoordinator.DefaultPhaseBBudget.TotalSeconds));
                var skipConflictCheck = _skipVpnConflictThisSession;

                var (startTask, outcome) = await RunTwoPhaseStartAsync(cts.Token, () => skipConflictCheck);

                if (outcome == Internals.TwoPhaseStartOutcome.Connected)
                {
                    try { await startTask; } catch {
 }
                    IsConnected = true;
                    IsConnecting = false;
                    _lastSuccessfulConnectAt = DateTime.UtcNow;
                    ConnectButtonText = Strings.StopVPN;
                    StartSubRefreshTimer();
                    RefreshActiveIndicator();
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
                _logger.Error(ex, "Failed to start VPN");
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
        var psi = new System.Diagnostics.ProcessStartInfo(WindowsServiceCommand.GetSystemScPath())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("stop");
        psi.ArgumentList.Add("VPNRouter");
        using var proc = System.Diagnostics.Process.Start(psi);
        proc?.WaitForExit(5000);
        return proc is { HasExited: true, ExitCode: 0 };
    }
#endif

    private async Task<(Task StartTask, Internals.TwoPhaseStartOutcome Outcome)> RunTwoPhaseStartAsync(
        CancellationToken ct, Func<bool> skipConflictCheck)
    {
        var startTask = Task.Run(
            () => _engine.StartAsync(_settings, ct, skipConflictCheck()),
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
        var activeIp = _engine?.ActiveServerAddress;

        var configMode = _settings?.App?.ConfigMode ?? "generated";
        var isManualMode = configMode.Equals("generated", StringComparison.OrdinalIgnoreCase);
        var isSubscribeMode = configMode.Equals("subscribe", StringComparison.OrdinalIgnoreCase);

        var manualActiveName = _settings?.Vless?.ActiveServer;
        var subscriptionActiveName = _settings?.App?.ActiveSubscriptionServer;

        ServerViewModel? active = null;

        foreach (var s in Servers)
        {
            var isActive = isManualMode
                && IsConnected
                && !string.IsNullOrEmpty(activeIp)
                && IsRowActive(s, activeIp, manualActiveName);
            s.IsActive = isActive;
            if (isActive) active = s;
        }

        var autoSelect = isSubscribeMode && AutoSelectBestServer;
        foreach (var s in SubscriptionServers)
        {
            bool isActive;
            if (autoSelect)
                isActive = IsConnected
                    && _autoSelectedServer is not null
                    && ReferenceEquals(s, _autoSelectedServer);
            else
                isActive = isSubscribeMode
                    && IsConnected
                    && !string.IsNullOrEmpty(activeIp)
                    && IsRowActive(s, activeIp, subscriptionActiveName);
            s.IsActive = isActive;
            if (isActive) active = s;
        }

        ActiveServerChanged?.Invoke(active);
    }

    private static bool IsRowActive(ServerViewModel row, string activeIp, string? activeName)
    {
        if (row.Server != activeIp)
            return false;

        if (string.IsNullOrWhiteSpace(activeName))
            return true;

        return string.Equals(row.Name, activeName, StringComparison.OrdinalIgnoreCase);
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

            IsConnected = true;
            ConnectButtonText = Strings.StopVPN;
            var configuredMode = _settings.App.ConfigMode ?? "generated";
            var configLabel = configuredMode.Equals("subscribe", StringComparison.OrdinalIgnoreCase)
                ? "subscribe"
                : configuredMode.Equals("generated", StringComparison.OrdinalIgnoreCase) ? "manual" : "custom";
            var tunnelLabel = IsSplitTunnel ? "split" : "full";
            var mode = $"{configLabel}/{tunnelLabel}";
            StatusText = IsRussian
                ? $"Подключено через службу [{mode}]"
                : $"Connected via service [{mode}]";
            MarkTrueSplitServiceManagedIfNeeded();
            StartSubRefreshTimer();
            _logger.Information("[VM] Detected VPN running via service (sing-box alive + TUN owned)");
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[VM] DetectServiceManagedVpn failed");
        }
    }

    private bool IsServiceManagedVpn => IsConnected && !(_engine?.IsRunning ?? false);

    private void RestoreConnectedStatus()
    {
        if (!IsConnected) return;
        var (serverName, serverIp) = DeriveConnectedServerLabel();

        var configuredMode = _settings.App.ConfigMode ?? "generated";
        var configLabel = configuredMode.Equals("subscribe", StringComparison.OrdinalIgnoreCase)
            ? "subscribe"
            : configuredMode.Equals("generated", StringComparison.OrdinalIgnoreCase) ? "manual" : "custom";
        var tunnelLabel = IsSplitTunnel ? "split" : "full";
        var modeLabel = $"{configLabel}/{tunnelLabel}";

        StatusText = Strings.Connected(modeLabel, serverName, serverIp);
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
        try { SaveSettings(); } catch { }
        StatusText = IsRussian
            ? $"Выбран {newServerName}. VPN управляется службой — остановите и запустите VPN, чтобы переключиться."
            : $"Selected {newServerName}. VPN is managed by the service — Stop and Start VPN to switch.";
        _logger.Information("[VM] Service-managed VPN: selection '{Name}' saved; user must Stop+Start to apply", newServerName);
    }

    private async Task ReconnectAsync(string configName, ReconnectIntent intent = ReconnectIntent.Follow)
    {
        if (_isReconnecting) return;
        _isReconnecting = true;
        IsConnecting = true;
        StatusText = IsRussian
            ? $"Переключение на {configName}..."
            : $"Switching to {configName}...";

        _logger.Information(
            "[VM] ReconnectAsync target={Target} intent={Intent} ConfigMode={CM} IsVlessMode={V} IsSubscribeMode={S}",
            configName, intent,
            _settings.App.ConfigMode, IsVlessMode, IsSubscribeMode);

        try
        {
            var applyInPlace = _engine.IsRunning;
            if (!applyInPlace)
            {
                await Task.Run(() => _engine.Stop());
            }

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
                "[VM] ReconnectAsync after Save+Reload: ConfigMode={CM} VlessActive={VA} SubActive={SA} VlessServers={N}",
                _settings.App.ConfigMode,
                _settings.Vless.ActiveServer,
                _settings.App.ActiveSubscriptionServer,
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
                    "[VM] ReconnectAsync.ManualVless: forced ConfigMode=generated, Vless.Servers={N}, ActiveServer={A}",
                    _settings.Vless.Servers.Count, configName);
            }
            else if ((intent == ReconnectIntent.Subscription || (intent == ReconnectIntent.Follow && IsSubscribeMode))
                     && aggregated.Count > 0)
            {
                _settings.Vless.Servers = aggregated;
                _settings.Vless.ActiveServer = _settings.App.ActiveSubscriptionServer;
                _logger.Information(
                    "[VM] ReconnectAsync.Subscription: aggregated {N} servers, ActiveServer={A}, ConfigMode preserved=subscribe",
                    aggregated.Count, _settings.Vless.ActiveServer);
            }

            if (applyInPlace)
            {
                _logger.Information("[VM] ReconnectAsync applying new config via ApplyAsync(forceRestart=true)");
                var applied = await Task.Run(() => _engine.ApplyAsync(
                    _settings,
                    CancellationToken.None,
                    forceRestart: true));
                if (applied)
                {
                    RestoreConnectedStatus();
                    try { RefreshActiveIndicator(); }
                    catch (Exception ex) { _logger.Debug(ex, "[VM] Reconnect: RefreshActiveIndicator failed"); }
                    return;
                }

                _logger.Warning("[VM] ReconnectAsync ApplyAsync returned false; falling back to Stop+Start");
                await Task.Run(() => _engine.Stop());
            }

            const int maxRetries = 3;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(
                        Internals.TwoPhaseStartCoordinator.DefaultPhaseABudget.TotalSeconds +
                        Internals.TwoPhaseStartCoordinator.DefaultPhaseBBudget.TotalSeconds));
                    var (startTask, outcome) = await RunTwoPhaseStartAsync(cts.Token, () => _skipVpnConflictThisSession);

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
            catch (Exception ex) { _logger.Debug(ex, "[VM] Reconnect: RefreshActiveIndicator failed"); }
        }
        catch (OperationCanceledException)
        {
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
            _logger.Error(ex, "[VM] Reconnect failed");
            IsConnected = false;
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
