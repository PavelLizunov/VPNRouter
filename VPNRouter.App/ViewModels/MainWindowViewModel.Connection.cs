using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.App.Localization;
using VPNRouter.Core;
using VPNRouter.Core.Models;
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
            _logger?.Warning("[VM] AutoFailover surfaced to user: {Message}", message);
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
            _logger?.Information("[VM] TrueSplit state={State}: {Reason}", state, reason);
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
                    try
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
                    }
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
                try
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
                    if (proc?.ExitCode == 0) Thread.Sleep(2000);
                }
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
                _logger?.Information(
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

                var startTask = Task.Run(
                    () => _engine.StartAsync(_settings, cts.Token, skipConflictCheck),
                    cts.Token);

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
                    cancellationToken: cts.Token);

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
                    try { await Task.Run(() => _engine.Stop()); } catch { }
                    IsConnecting = false;
                    IsConnected = false;
                    StatusText = Strings.StartTimeoutPhaseA;
                    ConnectButtonText = Strings.StartVPN;
                    return;
                }
                else if (outcome == Internals.TwoPhaseStartOutcome.PhaseBTimeout)
                {
                    _logger.Error("[VM] Phase B (TUN warm-up) timed out after {N}s — sing-box started but Connected event never fired. Possible cause: wintun driver issue, network interface gone, or warmup probe blocked. Stopping engine.",
                        (int)Internals.TwoPhaseStartCoordinator.DefaultPhaseBBudget.TotalSeconds);
                    try { await Task.Run(() => _engine.Stop()); } catch { }
                    IsConnecting = false;
                    IsConnected = false;
                    StatusText = Strings.StartTimeoutPhaseB;
                    ConnectButtonText = Strings.StartVPN;
                    return;
                }
                else
                {
                    _logger.Error("[VM] Two-phase start cancelled by outer CTS");
                    try { await Task.Run(() => _engine.Stop()); } catch { }
                    IsConnecting = false;
                    IsConnected = false;
                    StatusText = Strings.StartTimeoutPhaseA;
                    ConnectButtonText = Strings.StartVPN;
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
                try { await Task.Run(() => _engine.Stop()); } catch { }
                IsConnecting = false;
                IsConnected = false;
                StatusText = Strings.StartTimeoutPhaseA;
                ConnectButtonText = Strings.StartVPN;
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

}
