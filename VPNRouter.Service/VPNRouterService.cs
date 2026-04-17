using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net.NetworkInformation;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Service;

/// <summary>
/// Windows Service implementation.
/// On cold start: waits for network, then auto-starts VPN/Zapret/TgProxy
/// based on config flags. Defers to desktop UI if it's running.
/// </summary>
public class VPNRouterService : BackgroundService
{
    private readonly ILogger<VPNRouterService> _logger;
    private VpnEngine? _engine;
    private ZapretManager? _zapret;
    private TgProxyManager? _tgProxy;

    private readonly TaskCompletionSource _startupComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private const string EventSourceName = "VPNRouter";
    private const string EventLogName = "Application";

    public VPNRouterService(ILogger<VPNRouterService> logger)
    {
        _logger = logger;
        EnsureEventSource();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[Service] ExecuteAsync started");

        try
        {
            var settings = SettingsLoader.Load();
            _logger.LogInformation("[Service] Config loaded, mode: {Mode}", settings.App.ConfigMode);

            // ── Step 1: Wait for network (cold boot — NIC may not be up yet) ──
            await WaitForNetworkAsync(stoppingToken, TimeSpan.FromSeconds(30));

            // ── Step 2: Auto-start VPN ──
            if (settings.App.AutostartVpn)
            {
                await AutostartVpnAsync(settings, stoppingToken);
            }
            else
            {
                _logger.LogInformation("[Service] AutostartVpn=false, skipping VPN");
                _startupComplete.TrySetResult();
            }

            // ── Step 3: Auto-start Zapret (independent, parallel) ──
            if (settings.App.AutostartZapret)
            {
                _ = Task.Run(() => AutostartZapret(settings), stoppingToken);
            }

            // ── Step 4: Auto-start TgProxy (independent, parallel) ──
            if (settings.App.AutostartTgProxy)
            {
                _ = Task.Run(() => AutostartTgProxy(settings), stoppingToken);
            }

            // Keep service alive until stop is requested
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

    /// <summary>Wait for network adapter to become available (max timeout).</summary>
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

    /// <summary>Start VPN connection with subscription refresh + UI deference.</summary>
    private async Task AutostartVpnAsync(AppSettings settings, CancellationToken ct)
    {
        _engine = new VpnEngine(
            new ProcessScanner(Serilog.Log.Logger),
            () => new FirewallManager(Serilog.Log.Logger),
            () => new EtwProcessMonitor(Serilog.Log.Logger),
            Serilog.Log.Logger);

        _engine.StatusChanged += msg =>
            _logger.LogInformation("[Service] {Status}", msg);
        _engine.RestartAttempted += (attempt, max) =>
            WriteEventLog($"sing-box restart attempt {attempt}/{max}", EventLogEntryType.Warning);
        _engine.Warning += msg =>
            _logger.LogWarning("[Service] {Warn}", msg);

        // Subscription mode: refresh servers before connecting
        if (settings.App.ConfigMode?.Equals("subscribe", StringComparison.OrdinalIgnoreCase) == true
            && !string.IsNullOrEmpty(settings.App.SubscriptionUrl))
        {
            _logger.LogInformation("[Service] Refreshing subscription before connect...");
            try
            {
                var entries = await SubscriptionFetcher.FetchAsync(
                    settings.App.SubscriptionUrl, Serilog.Log.Logger, ct);

                if (entries.Count > 0)
                {
                    settings.App.SubscriptionServers = entries;
                    _logger.LogInformation("[Service] Subscription refreshed: {Count} servers", entries.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Service] Subscription refresh failed, using cached servers");
            }
        }

        // Sync subscription/manual servers to Vless engine
        if (settings.App.ConfigMode?.Equals("subscribe", StringComparison.OrdinalIgnoreCase) == true
            && settings.App.SubscriptionServers?.Count > 0)
        {
            settings.Vless.Servers = settings.App.SubscriptionServers;
            settings.Vless.ActiveServer = settings.App.ActiveSubscriptionServer;
            settings.App.ConfigMode = "generated";
        }

        // Start VPN with retry loop (defer to UI if running)
        while (true)
        {
            var uiRunning = Process.GetProcessesByName("VPNRouter.App").Length > 0;
            if (uiRunning)
            {
                _logger.LogInformation("[Service] Desktop UI is running — deferring, retry in 30s");
                _startupComplete.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                continue;
            }

            try
            {
                await _engine.StartAsync(settings, ct);
                break;
            }
            catch (TunOwnershipException)
            {
                _logger.LogInformation("[Service] TUN adapter busy — retry in 30s");
                _startupComplete.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
        }

        _startupComplete.TrySetResult();

        WriteEventLog(
            $"VPN started — profile: {_engine.ActiveProfileName}, PID: {_engine.SingBoxPid}",
            EventLogEntryType.Information);
    }

    /// <summary>Start Zapret DPI bypass (independent of VPN).</summary>
    private void AutostartZapret(AppSettings settings)
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
            _zapret.Start(args);
            _logger.LogInformation("[Service] Zapret started [{Strategy}] (PID {Pid})",
                strategyName, _zapret.Pid);
            WriteEventLog($"Zapret started: {strategyName}", EventLogEntryType.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Service] Zapret autostart failed");
        }
    }

    /// <summary>Start TgProxy Telegram proxy (independent of VPN).</summary>
    private void AutostartTgProxy(AppSettings settings)
    {
        try
        {
            if (!TgProxyUpdater.IsInstalled())
            {
                _logger.LogWarning("[Service] TgProxy not installed, skipping autostart");
                return;
            }

            var port = settings.App.TgProxyPort > 0 ? settings.App.TgProxyPort : 1443;
            var secret = settings.App.TgProxySecret;

            if (string.IsNullOrWhiteSpace(secret))
            {
                _logger.LogWarning("[Service] TgProxy secret not configured, skipping");
                return;
            }

            _tgProxy = new TgProxyManager(Serilog.Log.Logger);
            _tgProxy.Start(port, secret);
            _logger.LogInformation("[Service] TgProxy started on port {Port} (PID {Pid})",
                port, _tgProxy.Pid);
            WriteEventLog($"TgProxy started on port {port}", EventLogEntryType.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Service] TgProxy autostart failed");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[Service] Stopping...");
        WriteEventLog("VPN Router service stopping", EventLogEntryType.Information);

        try
        {
            await Task.WhenAny(_startupComplete.Task, Task.Delay(15000, cancellationToken));
        }
        catch (OperationCanceledException) { }

        try { _engine?.Stop(); } catch { }
        try { _engine?.Dispose(); } catch { }
        try { _zapret?.Stop(); } catch { }
        try { _zapret?.Dispose(); } catch { }
        try { _tgProxy?.Stop(); } catch { }
        try { _tgProxy?.Dispose(); } catch { }

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
