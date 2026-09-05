using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using VPNRouter.App.Localization;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels;

/// <summary>
/// v2.31.10 — App-side autostart bootstrap. Mirrors the Service-side
/// <c>AutostartTgProxyAsync</c> / <c>AutostartZapretAsync</c> in
/// <c>VPNRouterService.cs</c> so the <c>autostart_tgproxy</c> /
/// <c>autostart_zapret</c> flags in <c>config.yaml</c> take effect when
/// only the desktop App is auto-launched at login (HKCU\Run via
/// <see cref="VPNRouter.Core.Platform.AutostartHelper"/>) — without the
/// Windows Service installed.
///
/// <para><b>Bug being fixed (App-side gap):</b> pre-r1 the App read the
/// flags into bound properties (<c>LoadSettingsIntoUI</c> at
/// <c>MainWindowViewModel.cs:2431-2433</c>) and persisted them
/// (<c>SaveSettings</c> at <c>MainWindowViewModel.cs:3163-3165</c>) but
/// never invoked <see cref="TgProxyManager.Start"/> /
/// <see cref="ZapretManager.Start"/> based on them. So a user who enabled
/// "Autostart Telegram proxy" in Settings, then closed the App, then
/// logged out/in (App auto-relaunches via HKCU\Run) saw the toggle ticked
/// but no proxy running. Same for Zapret. The flags only worked when the
/// Windows Service was installed.</para>
///
/// <para><b>Service-vs-App ownership:</b> if the Windows Service is
/// running we defer to it — the Service's
/// <c>VPNRouterService.AutostartTgProxyAsync</c> / <c>AutostartZapretAsync</c>
/// already handle the spawn at boot and the App is just a UI shell. The
/// <c>!ServiceVm.IsRunning</c> guard prevents the App from spawning a
/// duplicate that would race with the Service's instance over the bound
/// port (TgProxy) or for ownership of <c>winws.exe</c> (Zapret).</para>
///
/// <para><b>AutostartVpn intentionally NOT bootstrapped here.</b>
/// AutostartVpn has the same App-side gap on paper, but in normal UI
/// flow Simple-mode's "Start with Windows" toggle ties VPN-autostart to
/// Service install (see
/// <see cref="MainWindowViewModel.SmpAutostartChecked"/>), so the gap
/// only manifests for power users who manually edit <c>config.yaml</c>.
/// Adding an App-side VPN bootstrap would require <c>VpnEngine</c> +
/// subscription-resolver wiring at startup that races with Service if
/// installed, file-locks on <c>current.json</c>, and TUN ownership
/// arbitration — strictly higher risk than this Tg+Zapret fix and
/// deserves its own iteration. Tracked in the plan doc as a follow-up.</para>
///
/// <para><b>Idempotent:</b> re-checks <see cref="TgProxyManager.IsAnyRunning"/>
/// / <see cref="ZapretManager.IsWinwsRunning"/> before spawning so a
/// stale-from-previous-session daemon (already detected by
/// <c>LoadSettingsIntoUI</c>) and a second App instance both short-circuit
/// without double-spawn.</para>
/// </summary>
public partial class MainWindowViewModel
{
    // v2.37.0-r8 — magic-number extraction (Autostart bootstrap timings).
    // Settle window for TgProxy spawn — matches sibling `TgProxySettleDelayMs`
    // in main `MainWindowViewModel.cs`. Kept distinct here because the bootstrap
    // path runs at app start (different load profile vs warm interactive Start),
    // so we may want to tune independently in the future.
    private const int BootstrapSettleDelayMs = 2000;

    /// <summary>
    /// Entry point called from the constructor. Fire-and-forget; failures
    /// are logged and never propagate to the UI thread.
    /// </summary>
    private async Task BootstrapAutostartAsync()
    {
#if PLATFORM_WINDOWS
        try
        {
            // Short delay so ServiceVm.IsRunning settles. ServiceVm.Refresh()
            // ran synchronously in its ctor, but a concurrent Service start
            // by Windows at login is still possible — give the SCM a beat
            // to publish the running state before we decide whether to
            // defer. 500 ms is short enough to be invisible to the user
            // and long enough to avoid a flapping race in CI / fresh logon.
            await Task.Delay(500).ConfigureAwait(false);

            // Re-poll the SCM directly in background without blocking the UI thread with synchronous sc.exe queries.
            await Task.Run(() => ServiceVm.Refresh()).ConfigureAwait(false);

            if (ServiceVm.IsRunning)
            {
                _logger.Information(
                    "[App-Autostart] Windows Service is running — deferring " +
                    "autostart bootstraps to it (TgProxy/Zapret)");
                return;
            }

            // Run sequentially; both spawn separate daemons and the
            // sequencing keeps log output legible.
            await TryAutostartTgProxyAsync().ConfigureAwait(false);
            await TryAutostartZapretAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[App-Autostart] Bootstrap failed (non-fatal)");
        }
#else
        await Task.CompletedTask;
#endif
    }

#if PLATFORM_WINDOWS
    /// <summary>
    /// Bootstraps the Telegram proxy (tg-ws-proxy) when
    /// <see cref="AutostartTgProxy"/> is true and the Windows Service
    /// isn't running. Mirrors <c>VPNRouterService.AutostartTgProxyAsync</c>
    /// (VPNRouterService.cs:331-380): same install / secret / port checks,
    /// then calls <see cref="TgProxyManager.Start"/> with the persisted
    /// secret + port.
    /// </summary>
    private async Task TryAutostartTgProxyAsync()
    {
        if (_disposed) return;
        if (!await _tgProxyTransitionGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            if (_disposed) return;
            if (!AutostartTgProxy)
            {
                _logger.Debug("[App-Autostart] TgProxy: AutostartTgProxy=false, skipping");
                return;
            }

            if (!TgProxyUpdater.IsInstalled())
            {
                _logger.Information(
                    "[App-Autostart] TgProxy: not installed, skipping autostart " +
                    "(user must run the Telegram tab once to download tg-ws-proxy)");
                return;
            }

            // Port is occupancy, never identity. If the port is already in use,
            // fail closed and skip spawn. Never claim ownership or set TgProxyEnabled = true.
            if (TgProxyManager.IsAnyRunning(TgProxyPort))
            {
                _logger.Information(
                    "[App-Autostart] TgProxy: port {Port} already in use by another process, " +
                    "skipping spawn (fail-closed, listener unknown not owned)", TgProxyPort);
                return;
            }

            // Mirror manual-start behavior at MainWindowViewModel.cs:4354-4358:
            // generate a secret if missing rather than refusing to start.
            if (string.IsNullOrWhiteSpace(TgProxySecret))
            {
                var generatedSecret = Convert.ToHexStringLower(
                    System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_disposed) return;
                    TgProxySecret = generatedSecret;
                    SaveSettings();
                });
                if (_disposed) return;
                _logger.Information(
                    "[App-Autostart] TgProxy: generated new secret " +
                    "(was empty in config.yaml)");
            }

            _logger.Information(
                "[App-Autostart] TgProxy: starting on port {Port}", TgProxyPort);

            TgProxyManager manager;
            lock (_tgProxyStateGate)
            {
                if (_disposed) return;
                if (_tgProxy == null)
                {
                    _tgProxy = new TgProxyManager(_logger);
                    _tgProxy.StatsUpdated += OnTgProxyStats;
                }
                manager = _tgProxy;
            }
            manager.Start(TgProxyPort, TgProxySecret);

            if (_disposed || !ReferenceEquals(_tgProxy, manager))
            {
                manager.Stop();
                return;
            }

            // v2.37.0-r8 — extracted to named constant. Same 2s settle
            // window as the manual Toggle path (sibling
            // `MainWindowViewModel.cs` `TgProxySettleDelayMs` const).
            // Proxy needs ~1.5s to bind the port and serve requests.
            await Task.Delay(BootstrapSettleDelayMs, _tgProxyLifetimeCts.Token)
                .ConfigureAwait(false);

            if (_disposed || !ReferenceEquals(_tgProxy, manager))
            {
                manager.Stop();
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed || !ReferenceEquals(_tgProxy, manager)) return;
                if (manager.IsRunning)
                {
                    TgProxyEnabled = true;
                    TgProxyLink = TgProxyManager.BuildProxyLink(
                        "127.0.0.1", TgProxyPort, TgProxySecret);
                    TgProxyStatus = $"{Strings.StatusRunning} (PID {manager.Pid})";
                    _logger.Information(
                        "[App-Autostart] TgProxy: started successfully (PID {Pid})",
                        manager.Pid);
                }
                else
                {
                    TgProxyEnabled = false;
                    TgProxyStatus = IsRussian
                        ? "Автозапуск Telegram proxy: процесс завершился сразу"
                        : "Autostart Telegram proxy: process exited immediately";
                    _logger.Warning(
                        "[App-Autostart] TgProxy: process exited immediately after start " +
                        "(check tg-ws-proxy install or port {Port} availability)",
                        TgProxyPort);
                }
            });
        }
        catch (OperationCanceledException) when (_disposed)
        {
            _logger.Debug("[App-Autostart] TgProxy bootstrap cancelled during shutdown");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[App-Autostart] TgProxy bootstrap failed");
        }
        finally
        {
            _tgProxyTransitionGate.Release();
        }
    }

    /// <summary>
    /// Bootstraps Zapret (winws.exe) when <see cref="AutostartZapret"/>
    /// is true and the Windows Service isn't running. Mirrors
    /// <c>VPNRouterService.AutostartZapretAsync</c> (VPNRouterService.cs:270-328):
    /// resolves the strategy, prefers the Flowseal .bat wrapper when
    /// available (so service.bat prologue runs).
    /// </summary>
    private async Task TryAutostartZapretAsync()
    {
        try
        {
            if (!AutostartZapret)
            {
                _logger.Debug("[App-Autostart] Zapret: AutostartZapret=false, skipping");
                return;
            }

            if (!ZapretUpdater.IsInstalled())
            {
                _logger.Information(
                    "[App-Autostart] Zapret: not installed, skipping autostart " +
                    "(user must run the DPI Bypass tab once to download zapret)");
                return;
            }

            if (ZapretManager.IsWinwsRunning())
            {
                _logger.Information(
                    "[App-Autostart] Zapret: winws.exe already running, " +
                    "skipping spawn (idempotent)");
                await Dispatcher.UIThread.InvokeAsync(() => ZapretEnabled = true);
                return;
            }

            var strategyName = (ZapretStrategyIndex >= 0 && ZapretStrategyIndex < ZapretStrategies.Count)
                ? ZapretStrategies[ZapretStrategyIndex]
                : "multisplit";

            _logger.Information(
                "[App-Autostart] Zapret: starting [{Strategy}]", strategyName);

            _zapret ??= new ZapretManager(_logger);

            if (strategyName == "custom")
            {
                _zapret.Start(ZapretCustomArgs);
            }
            else if (strategyName == "multisplit" || strategyName == "fake+multisplit")
            {
                _zapret.Start(ZapretManager.BuildLegacyArgs(strategyName));
            }
            else
            {
                var parsed = _parsedStrategies.FirstOrDefault(s => s.Name == strategyName);
                if (parsed == null)
                {
                    _logger.Warning(
                        "[App-Autostart] Zapret: strategy not found in catalogue: {Name}",
                        strategyName);
                    return;
                }
                if (!string.IsNullOrEmpty(parsed.BatPath) && File.Exists(parsed.BatPath))
                    _zapret.StartFromBat(parsed.BatPath, parsed.Arguments);
                else
                    _zapret.Start(parsed.Arguments);
            }

            // Same 1.5 s settle window as the manual Toggle path
            // (MainWindowViewModel.cs:4074) — winws.exe via .bat has a
            // launcher prologue that runs briefly before the daemon is
            // visible by name.
            await Task.Delay(1500).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var winwsPid = ZapretManager.WinwsPid;
                if (_zapret.IsRunning || winwsPid != null)
                {
                    ZapretEnabled = true;
                    var pid = winwsPid ?? _zapret.Pid;
                    ZapretStatus = IsRussian
                        ? $"Работает [{strategyName}] (PID {pid})"
                        : $"Running [{strategyName}] (PID {pid})";
                    _logger.Information(
                        "[App-Autostart] Zapret: started successfully (PID {Pid})", pid);
                }
                else
                {
                    ZapretEnabled = false;
                    _logger.Warning(
                        "[App-Autostart] Zapret: winws.exe exited immediately " +
                        "(check strategy {Name} or DPI bypass binary)", strategyName);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[App-Autostart] Zapret bootstrap failed");
        }
    }
#endif
}
