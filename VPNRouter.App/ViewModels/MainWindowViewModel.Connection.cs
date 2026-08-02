using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Platform;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.FreeConfigs;
using VPNRouter.App.Localization;
using VPNRouter.App.ViewModels.FreeConfigs;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    /// <summary>
    /// True when sing-box is running but NOT started by this App instance —
    /// i.e. the Windows Service owns the tunnel. Used by Apply to avoid a
    /// silent-fail call into <see cref="VpnEngine.ApplyAsync"/> (which would
    /// bail immediately because our local engine has no sing-box process).
    /// </summary>
    private bool IsServiceManagedVpn => IsConnected && !(_engine?.IsRunning ?? false);

    [RelayCommand]
    private Task ApplyPendingChangesAsync() => ApplyPendingChangesInternalAsync(forceRestart: false);

    /// <summary>
    /// v2.29.0 — Apps page full-tunnel banner action. When user is in
    /// full-tunnel mode the apps list is irrelevant (all traffic is
    /// routed through VPN regardless of selection); previously the page
    /// silently disabled the entire Grid which read as "broken" to a
    /// Mac tester (2026-04-29 feedback). Now we show a banner with this
    /// command as the action. Flips IsSplitTunnel + persists.
    /// HasPendingAppChanges is set so the user sees the standard Apply
    /// gating without us having to start a tunnel restart unilaterally
    /// — the routing-mode change requires a forceRestart Apply, which
    /// the user kicks off themselves via the Apply bar.
    /// </summary>
    [RelayCommand]
    private void SwitchToSplitTunnel()
    {
        if (IsSplitTunnel) return; // no-op if already split
        IsSplitTunnel = true;
        HasPendingAppChanges = true;
        SaveSettings();
    }

    /// <summary>
    /// v2.20.4: shared Apply pipeline with a <c>forceRestart</c> switch.
    /// Callers changing RoutingMode (split ↔ full) or other structural
    /// sing-box config should pass true — hot-reload doesn't re-do the
    /// TUN routing table, so the user sees no effect if we rely on it.
    /// </summary>
    private async Task ApplyPendingChangesInternalAsync(bool forceRestart)
    {
        if (IsApplying || !IsConnected) return;
        IsApplying = true;
        try
        {
            SaveSettings();
            _settings = _settingsStore.Load(AppPaths.ConfigYamlPath);

            if (IsServiceManagedVpn)
            {
                // v2.18.4: the sing-box process is owned by the Windows
                // Service, so hot-reload via our local engine isn't an
                // option — it has no sing-box to talk to. Pre-v2.18.4 we
                // punted here with a "Stop and Start VPN to apply" hint,
                // which forced the user to click Disconnect + Connect
                // after every Split/Full or server change. Terrible UX.
                //
                // New behaviour: invoke the already-existing
                // ServiceVm.RestartServiceCommand (stop → start cycle).
                // The service re-reads config.yaml via SettingsLoader.Load
                // on boot and spawns sing-box with the freshly-saved
                // RoutingMode / ActiveProfile / subscription picks.
                //
                // Fallback to the old "please restart manually" text only
                // if service isn't available at all (shouldn't happen when
                // IsServiceManagedVpn is true, but belt-and-braces).
                if (ServiceVm.IsAvailable)
                {
                    StatusText = IsRussian
                        ? "Перезапускаю службу с новыми настройками..."
                        : "Restarting service with new settings...";
                    await ServiceVm.RestartServiceCommand.ExecuteAsync(null);
                    HasPendingAppChanges = false;
                    // The 2-second SyncConnectedWithVpnRuntime poll in
                    // RuntimeStatus will pick up the new service state and
                    // refresh StatusText to the "connected via service
                    // [mode]" line. No extra plumbing needed here.
                    return;
                }

                HasPendingAppChanges = false;
                StatusText = IsRussian
                    ? "Настройки сохранены. Остановите и запустите VPN, чтобы они применились (служба перечитает config.yaml при старте)."
                    : "Settings saved. Stop and Start VPN to apply — the service re-reads config.yaml on start.";
                return;
            }

            var ok = await Task.Run(() => _engine.ApplyAsync(_settings, CancellationToken.None, forceRestart));
            if (ok)
            {
                HasPendingAppChanges = false;
                RestoreConnectedStatus();
            }
            else
            {
                StatusText = IsRussian ? "Не удалось применить" : "Apply failed";
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] ApplyPendingChanges failed");
            StatusText = $"{(IsRussian ? "Не удалось применить" : "Apply failed")}: {ex.Message}";
        }
        finally { IsApplying = false; }
    }

    /// <summary>Rebuild the "Connected [mode · tunnel] → server (ip)" status line after Apply.</summary>
    private void RestoreConnectedStatus()
    {
        if (!IsConnected) return;
        var (serverName, serverIp) = DeriveConnectedServerLabel();

        var configLabel = IsSubscribeMode ? "subscribe" : IsVlessMode ? "manual" : "custom";
        var tunnelLabel = IsSplitTunnel ? "split" : "full";
        var modeLabel = $"{configLabel}/{tunnelLabel}";

        StatusText = Strings.Connected(modeLabel, serverName, serverIp);
    }

    /// <summary>
    /// v2.44.1-r6: derive the (name, ip) for the connected-status line, shared by
    /// <see cref="RestoreConnectedStatus"/> + the OnEngineStatus "Connected"
    /// handler so the two can't drift. When AutoSelectBestServer builds a urltest
    /// "proxy" group the active member is chosen by sing-box at runtime, so show
    /// the REAL server resolved from clash_api (<c>_autoSelectedServer</c>,
    /// refreshed by the ConnStats poll) — or a generic auto-select label until
    /// it's known — NOT the stale first-in-list that lit "Germany" while traffic
    /// exited via Iceland (user report 2026-06-23).
    /// </summary>
    private (string? name, string? ip) DeriveConnectedServerLabel()
    {
        var serverIp = _engine.ActiveServerAddress;
        if (IsSubscribeMode)
        {
            return AutoSelectStatus.ResolveSubscribeLabel(
                AutoSelectBestServer,
                _autoSelectedServer is not null,
                _autoSelectedServer?.DisplayName,
                _autoSelectedServer?.Server,
                Strings.AutoSelectStatusLabel,
                (SelectedSubscriptionServer ?? SubscriptionServers.FirstOrDefault())?.DisplayName,
                serverIp);
        }
        if (IsVlessMode)
            return ((SelectedServer ?? Servers.FirstOrDefault())?.DisplayName, serverIp);
        var c = CustomConfigs.FirstOrDefault(x => x.IsActive) ?? SelectedCustomConfig ?? CustomConfigs.FirstOrDefault();
        return (c?.Name, serverIp);
    }

    /// <summary>
    /// One-time: create /etc/sudoers.d/vpnrouter via osascript on UI thread
    /// so the admin password dialog appears properly.
    ///
    /// <para>v2.28.6-r6: two bug fixes for the "sudo: a password is required"
    /// failure on macOS that left users unable to start the VPN:</para>
    /// <list type="number">
    /// <item><b>Escape spaces in path</b>. The default install path is
    /// <c>/Users/$USER/Library/Application Support/VPNRouter/bin/sing-box</c>
    /// — sudoers' <c>Cmnd_Spec</c> grammar requires spaces to be escaped
    /// with a backslash, otherwise the rule is malformed and sudo silently
    /// falls back to password prompt → fails because no terminal.</item>
    /// <item><b>Add <c>*</c> wildcard for arguments</b>. Without it, the rule
    /// only matches a bare <c>sudo sing-box</c> call with NO arguments —
    /// but we always invoke <c>sudo sing-box run -c &lt;path&gt;</c>. With
    /// the wildcard, any argument list is allowed.</item>
    /// </list>
    /// <para>For users who already have a broken sudoers file from
    /// v2.28.6-r1..r5 or older, the marker comment <c>SudoersFormatMarker</c>
    /// flags whether the current rewrite has been applied; if absent, we
    /// rewrite (which means the user gets a one-time osascript prompt
    /// after upgrading).</para>
    /// </summary>
    private const string SudoersFormatMarker = "# vpnrouter v2.41.0-r6 sudoers (sing-box + pkill + networksetup DNS + pfctl kill-switch)";

    private void EnsureMacSudoAccess()
    {
        const string sudoersPath = "/etc/sudoers.d/vpnrouter";

        // v2.28.6-r6: check the file's CONTENT, not just existence — older
        // releases wrote a malformed file (spaces unescaped, no args
        // wildcard) that exists on disk but doesn't grant NOPASSWD for
        // our actual sudo invocation.
        // v2.41.0-r5: authority is a USER-readable marker we write after a
        // confirmed grant — NOT the /etc/sudoers.d file. That file is
        // 0440 root:wheel; a normal admin user can't read it, so the old
        // File.ReadAllText(sudoersPath) threw UnauthorizedAccessException and
        // forced the "one-time" osascript prompt on EVERY connect. Reading our
        // own marker never throws, so the prompt fires at most once per marker
        // version (on first connect after an upgrade that bumps it).
        var sudoersMarkerPath = Path.Combine(AppPaths.DataDir, "macos-sudoers.marker");
        bool needsRewrite = true;
        try
        {
            if (File.Exists(sudoersMarkerPath) &&
                File.ReadAllText(sudoersMarkerPath).Contains(SudoersFormatMarker, StringComparison.Ordinal))
            {
                needsRewrite = false;
            }
            else if (File.Exists(sudoersPath))
            {
                // Best-effort: if we CAN read the root file (dev box / wheel
                // member), honour its marker too. Unreadable (the common 0440
                // case) → swallow and fall through to one rewrite, which then
                // writes the user marker so later launches take the fast path.
                try
                {
                    if (File.ReadAllText(sudoersPath).Contains(SudoersFormatMarker, StringComparison.Ordinal))
                        needsRewrite = false;
                }
                catch { /* 0440 unreadable → needsRewrite stays true */ }
            }
        }
        catch { needsRewrite = true; }
        if (!needsRewrite) return;

        StatusText = IsRussian ? "Настройка sudo (один раз)..." : "Setting up sudo (one-time)...";

        // v2.28.6-r6: escape spaces in the binary path for sudoers
        // Cmnd_Spec syntax. Add ` *` wildcard so any arguments
        // (`run -c <path>`) are allowed under NOPASSWD.
        var user = Environment.UserName;
        var singbox = AppPaths.SingBoxExePath;
        var singboxEscaped = singbox.Replace(" ", "\\ ");
        var tmpFile = Path.Combine(Path.GetTempPath(), "vpnrouter-sudoers");
        File.WriteAllText(tmpFile,
            $"{SudoersFormatMarker}\n" +
            $"{user} ALL=(root) NOPASSWD: {singboxEscaped} *\n" +
            $"{user} ALL=(root) NOPASSWD: /usr/bin/pkill *\n" +
            // Fix #1 (v2.41.0 r3): macOS DNS-leak hardening needs to repoint the
            // primary service's resolver to the TUN gateway + flush the cache.
            $"{user} ALL=(root) NOPASSWD: /usr/sbin/networksetup *\n" +
            $"{user} ALL=(root) NOPASSWD: /usr/bin/dscacheutil *\n" +
            $"{user} ALL=(root) NOPASSWD: /usr/bin/killall -HUP mDNSResponder\n" +
            // r6: pf kill-switch (block_on_vpn_fail) loads/flushes a global
            // egress-block ruleset via pfctl in full-tunnel mode.
            $"{user} ALL=(root) NOPASSWD: /sbin/pfctl *\n");

        // Write a helper script
        var helperScript = Path.Combine(Path.GetTempPath(), "vpnrouter-setup.sh");
        File.WriteAllText(helperScript,
            $"#!/bin/bash\ncp \"{tmpFile}\" {sudoersPath}\nchmod 0440 {sudoersPath}\nchown root:wheel {sudoersPath}\nrm -f \"{tmpFile}\" \"{helperScript}\"\n");
        File.SetUnixFileMode(helperScript,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        // Exact same osascript format that works for sing-box launch
        var cmd = $"\\\"{helperScript}\\\"";
        var psi = new ProcessStartInfo("/usr/bin/osascript")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add($"do shell script \"{cmd}\" with administrator privileges");

        _logger.Information("Running osascript for sudo setup...");
        var proc = System.Diagnostics.Process.Start(psi);
        if (proc == null)
        {
            _logger.Error("Failed to start osascript");
            return;
        }

        var stderr = proc.StandardError.ReadToEnd();
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(60000);
        var osascriptExit = proc.HasExited ? proc.ExitCode : -1;

        _logger.Information("osascript exit={Exit} stdout={Out} stderr={Err}",
            osascriptExit, stdout, stderr);
        proc.Dispose();

        // r9 (claude-code audit P1): only mark the grant configured when osascript
        // actually SUCCEEDED (exit 0 = user approved + helper installed the current
        // grants) AND a non-interactive probe of the newest grant works. A stale
        // /etc/sudoers.d/vpnrouter from an old version makes File.Exists true even
        // after a CANCELLED or FAILED prompt — so File.Exists alone would falsely
        // write the marker, skip the prompt forever, and let runtime `sudo -n`
        // calls (networksetup / pfctl) fail silently later.
        if (osascriptExit != 0)
        {
            _logger.Warning("sudoers setup: osascript exit {Exit} (cancelled/failed) — NOT writing marker; will re-prompt next time", osascriptExit);
            return;
        }
        if (!File.Exists(sudoersPath))
        {
            _logger.Warning("Failed to configure sudoers (file absent after a successful osascript?)");
            return;
        }
        if (!ProbeSudoGrant())
        {
            _logger.Warning("sudoers setup: pfctl grant probe failed after osascript — NOT writing marker; will re-prompt");
            return;
        }

        _logger.Information("Passwordless sudo configured + probed");
        try { File.WriteAllText(sudoersMarkerPath, SudoersFormatMarker); }
        catch (Exception ex) { _logger.Warning(ex, "Failed to write sudoers marker — may re-prompt next launch"); }
    }

    /// <summary>
    /// Non-interactive probe that the pfctl NOPASSWD grant (the newest entry in
    /// the r6 sudoers template) is actually active. <c>sudo -n /sbin/pfctl -s
    /// info</c> exits 0 only when the grant is installed; a missing grant makes
    /// sudo fail fast. macOS-only path.
    /// </summary>
    private static bool ProbeSudoGrant()
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/sudo")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("/sbin/pfctl");
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add("info");
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return false;
            p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

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
            StatusText = status;

            if (status.StartsWith("Connected") || status.StartsWith("VPN Router is running"))
            {
                IsConnected = true;
                IsConnecting = false;
                ConnectButtonText = Strings.StopVPN;
                StartSubRefreshTimer();
                RefreshActiveIndicator();
                // Use engine's actual runtime state — not stale ViewModel cache.
                // This prevents "status says 104 but actually running 194" mismatch.
                // v2.44.1-r6: shared with RestoreConnectedStatus — also resolves
                // the REAL urltest member when AutoSelectBestServer is on (the
                // autostart "says Germany, exits Iceland" report 2026-06-23).
                var (serverName, serverIp) = DeriveConnectedServerLabel();
                var modeLabel = IsSplitTunnel ? "split" : "full";
                StatusText = Strings.Connected(modeLabel, serverName, serverIp);
            }
            else if (status == "Stopped")
            {
                IsConnected = false;
                IsConnecting = false;
                ConnectButtonText = Strings.StartVPN;
                StatusText = Strings.NotConnected;
                StopSubRefreshTimer();
                RefreshActiveIndicator();
                HasPendingAppChanges = false;
            }
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
        if (IsConnecting || _isReconnecting)
        {
            _logger.Debug(
                "[VM] ToggleConnectionAsync ignored - connection transition already in progress (IsConnecting={IsConnecting}, IsReconnecting={IsReconnecting})",
                IsConnecting,
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
                        var psi = new System.Diagnostics.ProcessStartInfo("sc.exe", "stop VPNRouter")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        };
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
                    var psi = new System.Diagnostics.ProcessStartInfo("sc.exe", "stop VPNRouter")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
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
                // v2.44.1-r2 (user report 2026-06-22): a late-phase throw
                // (post-start probe / AutoFailover re-entry) AFTER sing-box
                // already came up must NOT leave "Failed to start VPN" on screen
                // while the tunnel is actually running. Trust the engine's real
                // state: if it's running, keep the connected status (the 2 s
                // runtime-status poll + any later OnEngineStatus("Connected")
                // reconcile the rest); only show the failure when genuinely down.
                if (_engine.IsRunning)
                {
                    IsConnected = true;
                    ConnectButtonText = Strings.StopVPN;
                    _logger.Warning("[VM] start path threw but engine is running — keeping connected status instead of a stale 'Failed to start VPN'");
                }
                else
                {
                    StatusText = $"{Strings.FailedStartVpn} {ex.Message}";
                    ConnectButtonText = Strings.StartVPN;
                }
                return;
            }
        }
    }

    private bool _isReconnecting;

    /// <summary>
    /// v2.30.2-r1: tells <see cref="ReconnectAsync"/> which mode the
    /// reconnect is FOR. The legacy single-arg call defaulted to "follow
    /// VM flags", which could leave ConfigMode stuck on "subscribe" if
    /// the user clicked a manual VLESS row after sub-tab peeking. The
    /// explicit hint lets the reconnect path force the correct mode
    /// regardless of stale flag state.
    /// </summary>
    private enum ReconnectIntent
    {
        /// <summary>Follow VM flags (legacy behaviour).</summary>
        Follow,
        /// <summary>User clicked a manual VLESS server in the Servers list.</summary>
        ManualVless,
        /// <summary>User clicked a subscription server in the Subscriptions tab.</summary>
        Subscription,
        /// <summary>User clicked a custom config in the Custom sub-tab.</summary>
        CustomConfig
    }

    // Subscribe: selecting a subscription server = choosing which to route through.
    partial void OnSelectedSubscriptionServerChanged(ServerViewModel? value)
    {
        if (_isLoadingUI || value == null || _isReconnecting) return;
        // v2.30.2-r1 diag: trace every subscription-row selection.
        _logger?.Information(
            "[VM] OnSelectedSubscriptionServerChanged name={N} ip={Ip} IsConnected={C} IsSubscribeMode={S} IsConnecting={IC}",
            value.DisplayName, value.Server, IsConnected, IsSubscribeMode, IsConnecting);
        if (IsConnected && IsSubscribeMode && !IsConnecting)
        {
            if (IsServiceManagedVpn) { WarnServiceManagedReconnect(value.DisplayName); return; }
            _ = ReconnectAsync(value.DisplayName, ReconnectIntent.Subscription);
        }
    }

    // VLESS: selecting a server = choosing which server to route through.
    partial void OnSelectedServerChanged(ServerViewModel? value)
    {
        if (_isLoadingUI || value == null || _isReconnecting) return;

        // v2.30.2-r1 diag: trace every manual-row selection.
        _logger?.Information(
            "[VM] OnSelectedServerChanged name={N} ip={Ip} IsConnected={C} IsVlessMode={V} IsSubscribeMode={S} IsConnecting={IC}",
            value.DisplayName, value.Server, IsConnected, IsVlessMode, IsSubscribeMode, IsConnecting);
        // If connected in VLESS mode → reconnect with newly selected server
        if (IsConnected && IsVlessMode && !IsConnecting)
        {
            if (IsServiceManagedVpn) { WarnServiceManagedReconnect(value.DisplayName); return; }
            _ = ReconnectAsync(value.DisplayName, ReconnectIntent.ManualVless);
        }
    }

    // Auto-activate config when selected in the list (left-click = switch).
    // If VPN is already running, auto-reconnect with the new config.
    partial void OnSelectedCustomConfigChanged(CustomConfigViewModel? value)
    {
        if (_isLoadingUI || value == null) return;
        if (value.IsActive) return; // already active, no-op
        if (_isReconnecting) return; // don't re-enter during reconnect

        SetActiveCustomConfig(value);

        // If connected in custom mode → reconnect with new config
        if (IsConnected && !IsVlessMode && !IsConnecting)
        {
            if (IsServiceManagedVpn) { WarnServiceManagedReconnect(value.Name); return; }
            _ = ReconnectAsync(value.Name, ReconnectIntent.CustomConfig);
        }
    }

    /// <summary>
    /// Service-managed VPN can't be reconnected from the app — the local
    /// engine doesn't own the sing-box process, so Stop() is a no-op and
    /// StartAsync() would fight TUN ownership. We still save the new
    /// selection to config.yaml so the next Stop+Start cycle picks it up,
    /// and we surface a clear message so the user isn't confused about
    /// why the connection didn't switch.
    /// </summary>
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

        // v2.30.2-r1 diag: log every reconnect with full context so the
        // next repro distinguishes a "should-be-manual but is-subscribe"
        // vs other ordering bugs.
        _logger?.Information(
            "[VM] ReconnectAsync target={Target} intent={Intent} ConfigMode={CM} IsVlessMode={V} IsSubscribeMode={S}",
            configName, intent,
            _settings.App.ConfigMode, IsVlessMode, IsSubscribeMode);

        try
        {
            var applyInPlace = _engine.IsRunning;
            if (!applyInPlace)
            {
                // Stop current VPN when this VM is not the live engine owner.
                await Task.Run(() => _engine.Stop());
            }

            // v2.30.2-r1 Bug 2C fix: when the user explicitly clicked a
            // manual VLESS row, force the VM flags to manual mode BEFORE
            // SaveSettings so the on-disk ConfigMode persists as
            // "generated" — even if a subscription is enabled (which the
            // r2 guard would otherwise prefer to keep as "subscribe").
            // The r2 guard's purpose is to defend against accidental
            // sub-tab "peeks"; an explicit server-row click is NOT a peek.
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

            // Save + reload settings with the new active config
            SaveSettings();
            _settings = _settingsStore.Load(AppPaths.ConfigYamlPath);

            // v2.30.2-r1 diag: log effective settings after Save+Reload
            // so the engine-side decision is auditable from the VM log.
            _logger?.Information(
                "[VM] ReconnectAsync after Save+Reload: ConfigMode={CM} VlessActive={VA} SubActive={SA} VlessServers={N}",
                _settings.App.ConfigMode,
                _settings.Vless.ActiveServer,
                _settings.App.ActiveSubscriptionServer,
                _settings.Vless.Servers?.Count ?? 0);

            // Subscribe mode: aggregate enabled subscriptions → feed into engine
            var aggregated = _settings.App.Subscriptions
                .Where(s => s.Enabled)
                .SelectMany(s => s.Servers)
                .ToList();

            // v2.30.2-r1 Bug 2C fix: branch on caller intent, not just on
            // VM flag state. ManualVless overrides any subscription
            // pollution that may have leaked into _settings.Vless.Servers
            // from a prior reconnect cycle.
            if (intent == ReconnectIntent.ManualVless)
            {
                _settings.App.ConfigMode = "generated";
                _settings.Vless.Servers = Servers.Select(s => s.ToEntry()).ToList();
                _settings.Vless.ActiveServer = configName;
                _logger?.Information(
                    "[VM] ReconnectAsync.ManualVless: forced ConfigMode=generated, Vless.Servers={N}, ActiveServer={A}",
                    _settings.Vless.Servers.Count, configName);
            }
            else if ((intent == ReconnectIntent.Subscription || (intent == ReconnectIntent.Follow && IsSubscribeMode))
                     && aggregated.Count > 0)
            {
                _settings.Vless.Servers = aggregated;
                _settings.Vless.ActiveServer = _settings.App.ActiveSubscriptionServer;
                // v2.30.2-r2 Bug 2A fix: do NOT force ConfigMode=generated
                // here. The legacy code did this so VlessServersResolver
                // wouldn't re-aggregate (since we already did). But it
                // also broke RefreshActiveIndicator's ConfigMode gate —
                // with ConfigMode=generated the indicator loop only paints
                // the manual Servers list, leaving the Subscriptions list
                // dot dark even after a successful subscribe-mode connect.
                // User report 2026-05-01:
                // «Зеленый кружочек в подписках не появляеться, хотя к
                //  кликнутому конфигу есть подключение».
                //
                // Keeping ConfigMode="subscribe" is harmless to the engine:
                // VlessServersResolver re-aggregates idempotently (same
                // content as we just wrote into Vless.Servers), and the
                // engine reads Vless.Servers + Vless.ActiveServer the same
                // way regardless of ConfigMode. RefreshActiveIndicator can
                // now correctly identify the active subscription row.
                _logger?.Information(
                    "[VM] ReconnectAsync.Subscription: aggregated {N} servers, ActiveServer={A}, ConfigMode preserved=subscribe",
                    aggregated.Count, _settings.Vless.ActiveServer);
            }

            if (applyInPlace)
            {
                _logger?.Information("[VM] ReconnectAsync applying new config via ApplyAsync(forceRestart=true)");
                var applied = await Task.Run(() => _engine.ApplyAsync(
                    _settings,
                    CancellationToken.None,
                    forceRestart: true));
                if (applied)
                {
                    RestoreConnectedStatus();
                    try { RefreshActiveIndicator(); }
                    catch (Exception ex) { _logger?.Debug(ex, "[VM] Reconnect: RefreshActiveIndicator failed"); }
                    return;
                }

                _logger?.Warning("[VM] ReconnectAsync ApplyAsync returned false; falling back to Stop+Start");
                await Task.Run(() => _engine.Stop());
            }

            // Start with new config. Retry up to 3 times because Windows Service
            // may briefly grab the TUN lock between our Stop and Start.
            const int maxRetries = 3;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    // v2.35.2 Stage 2 (PinkuDani 2026-05-21): two-phase start
                    // timer. Same Phase A (60s) + Phase B (20s) budgets as
                    // the main ToggleConnectionAsync — with up to 3 retries
                    // worst-case wall-clock is 3 × 80s = 240s, but only on
                    // TunOwnershipException (Service stealing the TUN
                    // handle).
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(
                        Internals.TwoPhaseStartCoordinator.DefaultPhaseABudget.TotalSeconds +
                        Internals.TwoPhaseStartCoordinator.DefaultPhaseBBudget.TotalSeconds));
                    var startTask = Task.Run(
                        // Reconnect (subscription/server change) must carry the
                        // session "ignore conflict" decision — else it re-throws
                        // ConflictingVpnException while a tolerated VPN is up.
                        () => _engine.StartAsync(_settings, cts.Token, _skipVpnConflictThisSession),
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
                    // StartTaskCompleted / Connected / Cancelled: surface
                    // any exception from startTask. Throws (e.g.
                    // TunOwnershipException) re-enter the outer catch which
                    // triggers the retry loop.
                    await startTask;
                    break; // success
                }
                catch (TunOwnershipException) when (attempt < maxRetries)
                {
                    _logger.Warning("[VM] Reconnect: TUN lock stolen by service, retry {A}/{M}", attempt, maxRetries);
                    await Task.Delay(ServiceReleaseRetryDelayMs); // wait for service to release
                }
            }

            // v2.30.2-r1 Bug 2A fix: refresh the active-row indicator
            // after the engine has actually settled on a new ActiveServer.
            // The legacy flow relied on RefreshActiveIndicator firing from
            // some other status callback, but the timing was racy after a
            // subscription→subscription click chain — the green dot would
            // stay on the old row (or vanish entirely). Forcing a refresh
            // here, with the just-applied _settings, makes the UI match
            // the engine's view.
            try { RefreshActiveIndicator(); }
            catch (Exception ex) { _logger?.Debug(ex, "[VM] Reconnect: RefreshActiveIndicator failed"); }
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
