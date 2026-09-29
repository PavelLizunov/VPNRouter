using Avalonia.Threading;
using VPNRouter.App.Localization;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    private const int BootstrapSettleDelayMs = 2000;

    private async Task BootstrapAutostartAsync()
    {
#if PLATFORM_WINDOWS
        try
        {
            await Task.Delay(500).ConfigureAwait(false);

            await Task.Run(() => ServiceVm.Refresh()).ConfigureAwait(false);

            if (ServiceVm.IsRunning)
            {
                _logger.Information(
                    "[App-Autostart] Windows Service is running — deferring " +
                    "autostart bootstraps to it (TgProxy/Zapret)");
                return;
            }

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

            if (TgProxyManager.IsAnyRunning(TgProxyPort))
            {
                _logger.Information(
                    "[App-Autostart] TgProxy: port {Port} already in use by another process, " +
                    "skipping spawn (fail-closed, listener unknown not owned)", TgProxyPort);
                return;
            }

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
