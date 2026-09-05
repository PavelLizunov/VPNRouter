using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.App.Localization;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    // ── Engine events ──

    /// <summary>
    /// 2026-06-09: AutoFailover surfaced a user-facing message — either it
    /// switched servers after a dead-config probe, or (the rectuspc case) the
    /// active server is unreachable and there's no candidate to fail over to.
    /// The VPN process is still "running", so we don't flip IsConnected; we
    /// overwrite the connection status line with the warning so the user
    /// doesn't stare at a silent "Connected" while no traffic flows. Persists
    /// until the next state transition (the engine's StatusChanged fires only
    /// on transitions, not on healthy periodic ticks).
    /// </summary>
    private void OnAutoFailoverMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        Dispatcher.UIThread.Post(() =>
        {
            var text = "⚠ " + message;
            StatusText = text;                 // classic/advanced status line
            // Simple Mode (the default UI) does NOT bind StatusText — it shows
            // SimpleStatusTitle/Description. Surface the same alert through the
            // Simple status card so a silent dead "Connected" reads as a warning
            // instead of a green "Protected" (rectuspc, v2.41.2-r3).
            _lastConnectionAlert = text;
            RaiseSimpleAlertProps();
            _logger?.Warning("[VM] AutoFailover surfaced to user: {Message}", message);
        });
    }

    // W1.3: drive the "True split active" badge from the driver's engaged↔disengaged transitions.
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
                // NIGHT-07: legacy Connected*/VPN Router is running cannot SET IsConnected
                // from false; only refresh display if already true from typed Connected event.
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

    // ── Commands ──

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
                // v2.31.6-r20 — symmetric Stop. The pre-r20 path was a single
                // _engine.Stop() call that only affected the GUI's own engine.
                // If the Windows Service was the actual owner of sing-box (or
                // an older crashed GUI left orphans), _engine._singBox was
                // null and Stop became a no-op while the real sing-box kept
                // running. RuntimeStatusDetector then re-flipped IsConnected
                // back to true within 1-2 seconds — user reports
                // "press disconnect, it turns back on after a second".
                //
                // Mirror the cleanup the Connect-branch already does (kill
                // orphan sing-box + stop Windows Service) so Stop guarantees
                // the tunnel actually goes down regardless of who started it.
                await Task.Run(() =>
                {
                    try { _engine.Stop(); }
                    catch (Exception ex) { _logger.Debug(ex, "[VM] _engine.Stop"); }

                    // v2.31.10-r2: pass respectTunLock:false — user clicked
                    // Stop, so we explicitly INTEND to take down whoever
                    // is running sing-box (even Service-spawned). Default
                    // TunLock-aware path is for App startup; here it would
                    // turn the Stop button into a no-op when Service held
                    // the lock.
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
                // v2.20.0: clear the freshly-connected guard so a later poll
                // can faithfully reflect whatever state sing-box ends up in.
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

            // Ensure clean state: stop any existing VPN, kill orphans,
            // stop Windows Service. This guarantees the TUN lock is free.
            await Task.Run(() =>
            {
                try
                {
                    // Stop our own engine if it's somehow still running
                    if (_engine.IsRunning)
                        _engine.Stop();
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[VM] Pre-start engine stop");
                }

                // v2.31.10-r2: pass respectTunLock:false — user clicked
                // Connect, so we explicitly INTEND to free the TUN lock
                // (kill whatever is currently holding it, including
                // Service-spawned sing-box) before our own engine tries
                // to acquire it. Without this, default TunLock-aware
                // skip would leave the Service-spawned sing-box alive
                // and the next sc-stop wouldn't reach it via this VM.
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

            // Subscribe mode: aggregate enabled subscriptions → feed into VLESS engine path
            var aggregatedServers = _settings.App.Subscriptions
                .Where(s => s.Enabled)
                .SelectMany(s => s.Servers)
                .ToList();
            if (IsSubscribeMode && aggregatedServers.Count > 0)
            {
                _settings.Vless.Servers = aggregatedServers;
                _settings.Vless.ActiveServer = _settings.App.ActiveSubscriptionServer;
                // v2.30.2-r3 Bug 2A fix #2: same fix as r2's
                // ReconnectAsync.Subscription branch — do NOT force
                // ConfigMode=generated. The initial-connect path here
                // had the same bug-for-bug indicator gate problem:
                // RefreshActiveIndicator() reads ConfigMode and gates
                // SubscriptionServers list highlighting on
                // ConfigMode=="subscribe". Forcing to "generated"
                // killed the green dot on the Subscriptions list even
                // though the engine connected correctly.
                //
                // Caught during in-app smoke test on r2 — clicking
                // Запустить VPN button on a sub server connected fine
                // ("Подключено [full] → de-01 443 main-brat") but the
                // row indicator stayed dark. Same fix as r2 reconnect.
                //
                // Engine still uses Vless.Servers + Vless.ActiveServer
                // we just wrote. Resolver re-aggregates idempotently
                // when ConfigMode=subscribe — same content, same
                // active. Net: identical engine behaviour, correct UI.
                _logger?.Information(
                    "[VM] ToggleConnectionAsync.Connect.Subscription: aggregated {N} servers, ActiveServer={A}, ConfigMode preserved=subscribe",
                    aggregatedServers.Count, _settings.Vless.ActiveServer);
            }

            // macOS: ensure sudo access (one-time password prompt)
            if (OperatingSystem.IsMacOS())
                await Task.Run(EnsureMacSudoAccess);

            try
            {
                // v2.35.2 Stage 2 (PinkuDani 2026-05-21) — two-phase start
                // timer. Closes the original Fix #2 spec deferred until the
                // typed VpnEngine.Connected event landed in Stage 1
                // (commit b012fe6). Replaces the pre-Stage-2 single 60s
                // CTS+10s polling pattern with:
                //
                //   * Phase A budget (60s) — wait for SingBoxStarted event.
                //     If we hit the budget, sing-box never spawned (real
                //     hang in DeployAndSetupFirewall / TunAdapterDiagnostics
                //     / wintun launch); Stop with Phase A diagnostic.
                //   * Phase B budget (20s) — wait for Connected event
                //     (TUN warm-up gstatic probe success). If we hit the
                //     budget, sing-box is running but TUN never confirmed;
                //     Stop with Phase B diagnostic (wintun driver issue or
                //     upstream firewall blocking the probe).
                //
                // The pre-Stage-2 60s comment block (Win10 LTSC NetAdapter
                // PowerShell module pay) is now Phase A's budget. Phase B's
                // 20s is sized at 4x the happy-path warmup probe (~5s on
                // healthy installs, 15 attempts × 1s loop in
                // ScheduleWarmupProbe). The pre-Stage-2 IsRunning 10s
                // polling fallback is gone — Connected event is the
                // unambiguous "actually routing" signal.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(
                    Internals.TwoPhaseStartCoordinator.DefaultPhaseABudget.TotalSeconds +
                    Internals.TwoPhaseStartCoordinator.DefaultPhaseBBudget.TotalSeconds));
                // v2.32.1-r5 (Bug-r10-B) + reconnect fix (2026-06-15): session-
                // scoped opt-out from ConflictingVpnDetector, set by
                // IgnoreVpnConflictCommand. KEPT for the session (NOT reset here)
                // so the subscription/server-switch reconnect + AutoFailover honour
                // it too — else a removed-config reconnect re-throws
                // ConflictingVpnException and the VPN can't come back. A fresh
                // re-detect happens on the next app launch.
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
                    // Phase A + B both passed — sing-box up AND TUN warmup
                    // probe succeeded. Surface await on startTask in case
                    // a late exception was buffered (rare; defence pin).
                    try { await startTask; } catch { /* event-side success
                        is the authoritative signal; startTask exception
                        post-Connected is a non-event race */ }
                    IsConnected = true;
                    IsConnecting = false;
                    _lastSuccessfulConnectAt = DateTime.UtcNow;
                    ConnectButtonText = Strings.StopVPN;
                    StartSubRefreshTimer();
                    RefreshActiveIndicator();
                    RestoreConnectedStatus();
                    // Bug-r9-E: clear any stale conflict banner after a
                    // successful start (e.g. user dismissed the other VPN
                    // and retried — pre-r9-E the banner would linger).
                    ConflictingVpnWarningText = string.Empty;
                }
                else if (outcome == Internals.TwoPhaseStartOutcome.StartTaskCompleted)
                {
                    // StartAsync returned BEFORE SingBoxStarted fired.
                    // Surface any exception (TunOwnershipException,
                    // ConflictingVpnException, etc.) by awaiting the task.
                    // If it returned cleanly, OnEngineStatus will eventually
                    // flip IsConnected when the engine emits a status event.
                    await startTask;
                    // Audit batch-1 #2 residual: without this reset a clean
                    // return with no follow-up status event left the UI stuck
                    // on the "Connecting..." spinner forever. IsConnected
                    // itself stays with OnEngineStatus (typed-Connected is the
                    // only success signal); we only release the busy state.
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
                else // Cancelled
                {
                    // Outer CTS tripped (likely because both Phase A and
                    // Phase B budgets summed up have expired). Map to the
                    // same diagnostic as the dominant phase — Phase A's
                    // is the conservative default (start never happened).
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
                // Bug-r9-E (2026-05-11) — surface the named conflicting
                // VPN as a dismissible header banner so the user knows
                // exactly which app to close. Pre-r9-E this surfaced as
                // the cryptic wintun "Cannot create a file when that
                // file already exists" through the generic catch below.
                // v2.32.1-r4 (Bug-r10-A): also capture conflicts into
                // _lastConflicts so KillConflictingVpnCommand can act
                // on them without re-running detection (which races
                // with the user closing the other VPN themselves).
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
                // Stage 2 (2026-05-21): the coordinator's normal Phase A /
                // Phase B paths now produce explicit outcomes; this catch
                // only fires if a deeper StartAsync call surfaces an OCE
                // after the coordinator already saw StartTaskCompleted, or
                // the outer CTS race itself. Mirrors the Phase A diagnostic
                // since "no signal at all" is conservatively a Phase A
                // class of failure.
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
                // NIGHT-07: preserve green ONLY if already typed ready IsConnected;
                // otherwise take the stop/failed path so engine.IsRunning never fabricates connected.
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
