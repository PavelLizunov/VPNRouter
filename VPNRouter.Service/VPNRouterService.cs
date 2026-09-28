using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net.NetworkInformation;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Service;

public class VPNRouterService : BackgroundService
{
    private readonly ILogger<VPNRouterService> _logger;
    private readonly ISettingsStore _store;
    private VpnEngine? _engine;
    private ZapretManager? _zapret;
    private TgProxyManager? _tgProxy;

    private AppSettings? _currentSettings;

    private readonly TaskCompletionSource _startupComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private const string EventSourceName = "VPNRouter";
    private const string EventLogName = "Application";

    public VPNRouterService(ILogger<VPNRouterService> logger, ISettingsStore? store = null)
    {
        _logger = logger;
        _store = store ?? RealSettingsStore.Instance;
        EnsureEventSource();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[Service] ExecuteAsync started");

        try
        {
            _currentSettings = _store.Load();
            var settings = _currentSettings;
            _logger.LogInformation("[Service] Config loaded, mode: {Mode}", settings.App.ConfigMode);

            var recovery = _store.ConsumeRecoveryNotice();
            if (!string.IsNullOrWhiteSpace(recovery))
            {
                _logger.LogWarning("[Service] {Notice}", recovery);
                WriteEventLog(recovery, EventLogEntryType.Warning);
            }

            _store.StartWatching(onReload: OnConfigChanged);

            TryMigrateDependencies();

            await WaitForNetworkAsync(stoppingToken, TimeSpan.FromSeconds(30));

            if (settings.App.AutostartVpn)
            {
                await AutostartVpnAsync(settings, stoppingToken);
            }
            else
            {
                _logger.LogInformation("[Service] AutostartVpn=false, skipping VPN");
                _startupComplete.TrySetResult();
            }

            if (settings.App.AutostartZapret)
            {
                _ = AutostartZapretAsync(settings, stoppingToken);
            }

            if (settings.App.AutostartTgProxy)
            {
                _ = AutostartTgProxyAsync(settings, stoppingToken);
            }

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[Service] Stop requested");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "[Service] Fatal error");
            WriteEventLog($"Fatal error: {ex.Message}", EventLogEntryType.Error);
            throw;
        }
        finally
        {
            _startupComplete.TrySetResult();
        }
    }

    private void TryMigrateDependencies()
    {
        try
        {
            var current = ServiceInstaller.GetDependencies();
            if (current != null &&
                current.Any(d => string.Equals(d, "Tcpip", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            _logger.LogInformation("[Service] Migrating: adding Tcpip/Dnscache/Dhcp dependencies");
            var result = ServiceInstaller.UpdateDependencies();
            _logger.LogInformation("[Service] Migration result: {Message}", result.Message);
            if (result.Success)
            {
                WriteEventLog(
                    "Added boot dependencies (Tcpip/Dnscache/Dhcp). Takes effect on next reboot.",
                    EventLogEntryType.Information);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Service] Dependency migration failed (non-fatal)");
        }
    }

    private async Task WaitForNetworkAsync(CancellationToken ct, TimeSpan timeout)
    {
        if (NetworkInterface.GetIsNetworkAvailable())
        {
            _logger.LogInformation("[Service] Network available");
            return;
        }

        _logger.LogInformation("[Service] Waiting for network (max {Sec}s)...", timeout.TotalSeconds);
        var deadline = DateTime.UtcNow + timeout;

        while (!NetworkInterface.GetIsNetworkAvailable() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000, ct);
        }

        if (NetworkInterface.GetIsNetworkAvailable())
            _logger.LogInformation("[Service] Network became available");
        else
            _logger.LogWarning("[Service] Network still unavailable after timeout, proceeding anyway");
    }

    private async Task AutostartVpnAsync(AppSettings settings, CancellationToken ct)
    {
        _engine = VPNRouter.Core.Platform.PlatformServices
            .CreateVpnEngine(Serilog.Log.Logger);

        _engine.StatusChanged += msg =>
            _logger.LogInformation("[Service] {Status}", msg);
        _engine.RestartAttempted += (attempt, max) =>
            WriteEventLog($"sing-box restart attempt {attempt}/{max}", EventLogEntryType.Warning);
        _engine.Warning += msg =>
            _logger.LogWarning("[Service] {Warn}", msg);

        await SubscriptionResolver.ResolveAsync(
            settings,
            refreshFromNetwork: true,
            Serilog.Log.Logger,
            ct);

        if (TunOwnershipLock.IsOwnedByAnyone())
        {
            _logger.LogInformation(
                "[Service] TUN already owned by another VPNRouter process — " +
                "entering watcher mode, will not contend for sing-box.");
            _startupComplete.TrySetResult();
            return;
        }

        var vpnStarted = false;
        try
        {
            vpnStarted = await ResilientStarter.StartWithBackoffAsync(
                componentName: "VPN",
                startFn: innerCt => _engine.StartAsync(settings, innerCt),
                logger: Serilog.Log.Logger,
                ct: ct);

            if (!vpnStarted)
            {
                _logger.LogError(
                    "[Service] VPN autostart failed after retries. Not retrying further — " +
                    "Zapret/TgProxy will still attempt to start.");
                WriteEventLog(
                    "VPN autostart failed after retries",
                    EventLogEntryType.Error);
            }
        }
        catch (TunOwnershipException)
        {
            _logger.LogInformation(
                "[Service] TUN adapter acquired by another process mid-start — " +
                "entering watcher mode.");
        }

        _startupComplete.TrySetResult();

        if (vpnStarted)
        {
            WriteEventLog(
                $"VPN started — profile: {_engine.ActiveProfileName}, PID: {_engine.SingBoxPid}",
                EventLogEntryType.Information);
        }
    }

    private async Task AutostartZapretAsync(AppSettings settings, CancellationToken ct)
    {
        try
        {
            if (!ZapretUpdater.IsInstalled())
            {
                _logger.LogWarning("[Service] Zapret not installed, skipping autostart");
                return;
            }

            var strategyName = settings.App.ZapretStrategy ?? "multisplit";
            string args;

            if (strategyName == "custom")
            {
                args = settings.App.ZapretCustomArgs;
            }
            else if (strategyName == "multisplit" || strategyName == "fake+multisplit")
            {
                args = ZapretManager.BuildLegacyArgs(strategyName);
            }
            else
            {
                var strategies = ZapretUpdater.ParseStrategies();
                var parsed = strategies.FirstOrDefault(s => s.Name == strategyName);
                args = parsed?.Arguments ?? ZapretManager.BuildLegacyArgs("multisplit");
            }

            _zapret = new ZapretManager(Serilog.Log.Logger);

            var started = await ResilientStarter.StartWithBackoffAsync(
                componentName: "Zapret",
                startFn: () => _zapret.Start(args),
                logger: Serilog.Log.Logger,
                ct: ct);

            if (started)
            {
                _logger.LogInformation("[Service] Zapret started [{Strategy}] (PID {Pid})",
                    strategyName, _zapret.Pid);
                WriteEventLog($"Zapret started: {strategyName}", EventLogEntryType.Information);
            }
            else
            {
                _logger.LogError("[Service] Zapret autostart failed after retries");
                WriteEventLog(
                    $"Zapret autostart failed after retries ({strategyName})",
                    EventLogEntryType.Error);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Service] Zapret autostart failed");
        }
    }

    private async Task AutostartTgProxyAsync(AppSettings settings, CancellationToken ct)
    {
        _logger.LogInformation("[Service] AutostartTgProxyAsync: entered");
        try
        {
            if (!TgProxyUpdater.IsInstalled(Serilog.Log.Logger))
            {
                _logger.LogWarning("[Service] TgProxy not installed, skipping autostart");
                WriteEventLog(
                    "TgProxy autostart skipped: tg-ws-proxy is not installed. " +
                    "Open the Telegram tab in the desktop app and click Install.",
                    EventLogEntryType.Warning);
                return;
            }

            var port = settings.App.TgProxyPort > 0 ? settings.App.TgProxyPort : 1443;
            var secret = settings.App.TgProxySecret;

            if (string.IsNullOrWhiteSpace(secret))
            {
                _logger.LogWarning("[Service] TgProxy secret not configured, skipping");
                WriteEventLog(
                    "TgProxy autostart skipped: tg_proxy_secret is empty in config.yaml. " +
                    "Toggle the autostart checkbox once in v2.31.10-r5+ to auto-generate, " +
                    "or click Start in the Telegram tab to generate via the older path.",
                    EventLogEntryType.Warning);
                return;
            }

            _logger.LogInformation(
                "[Service] AutostartTgProxyAsync: secret configured (len {SecretLen}), port {Port} chosen, handing to ResilientStarter",
                secret.Length, port);

            _tgProxy = new TgProxyManager(Serilog.Log.Logger);

            var started = await ResilientStarter.StartWithBackoffAsync(
                componentName: "TgProxy",
                startFn: () => _tgProxy.Start(port, secret),
                logger: Serilog.Log.Logger,
                ct: ct);

            if (started)
            {
                _logger.LogInformation("[Service] TgProxy started on port {Port} (PID {Pid})",
                    port, _tgProxy.Pid);
                WriteEventLog($"TgProxy started on port {port}", EventLogEntryType.Information);
            }
            else
            {
                _logger.LogError("[Service] TgProxy autostart failed after retries");
                WriteEventLog(
                    $"TgProxy autostart failed after retries (port {port})",
                    EventLogEntryType.Error);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Service] TgProxy autostart failed");
        }
    }

    private void OnConfigChanged(AppSettings newSettings)
    {
        try
        {
            _currentSettings = newSettings;
            _logger.LogInformation("[Service] config.yaml changed → settings reconciled");

            if (_engine != null && _engine.IsRunning)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var ok = await _engine.ApplyAsync(newSettings);
                        _logger.LogInformation(
                            "[Service] Hot-reload {Result} after config change",
                            ok ? "succeeded" : "failed (kept previous config)");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Service] ApplyAsync raised");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Service] OnConfigChanged error (non-fatal)");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[Service] Stopping...");
        WriteEventLog("VPN Router service stopping", EventLogEntryType.Information);

        try { _store.StopWatching(); } catch { }

        try
        {
            await Task.WhenAny(_startupComplete.Task, Task.Delay(15000, cancellationToken));
        }
        catch (OperationCanceledException) { }

        try { _engine?.Stop(); }
        catch (Exception ex) { _logger.LogWarning(ex, "[Service] _engine.Stop failed (non-fatal)"); }
        try { _engine?.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "[Service] _engine.Dispose failed (non-fatal)"); }
        try { _zapret?.Stop(); }
        catch (Exception ex) { _logger.LogWarning(ex, "[Service] _zapret.Stop failed (non-fatal)"); }
        try { _zapret?.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "[Service] _zapret.Dispose failed (non-fatal)"); }
        try { _tgProxy?.Stop(); }
        catch (Exception ex) { _logger.LogWarning(ex, "[Service] _tgProxy.Stop failed (non-fatal)"); }
        try { _tgProxy?.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "[Service] _tgProxy.Dispose failed (non-fatal)"); }

        await base.StopAsync(cancellationToken);
        _logger.LogInformation("[Service] Stopped");
        WriteEventLog("VPN Router service stopped", EventLogEntryType.Information);
    }

    private static void EnsureEventSource()
    {
        try
        {
            if (!EventLog.SourceExists(EventSourceName))
                EventLog.CreateEventSource(EventSourceName, EventLogName);
        }
        catch { }
    }

    private static void WriteEventLog(string message, EventLogEntryType type)
    {
        try { EventLog.WriteEntry(EventSourceName, message, type); }
        catch { }
    }
}
