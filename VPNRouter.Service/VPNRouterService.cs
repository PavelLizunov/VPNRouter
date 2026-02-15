using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using VPNRouter.Core.Services;

namespace VPNRouter.Service;

/// <summary>
/// Windows Service implementation.
/// Runs as a BackgroundService hosted by Microsoft.Extensions.Hosting.
/// Delegates all VPN lifecycle to VpnEngine.
/// </summary>
public class VPNRouterService : BackgroundService
{
    private readonly ILogger<VPNRouterService> _logger;
    private VpnEngine? _engine;

    private const string EventSourceName = "VPNRouter";
    private const string EventLogName = "Application";

    public VPNRouterService(ILogger<VPNRouterService> logger)
    {
        _logger = logger;
        EnsureEventSource();
    }

    // ─── BackgroundService ────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[VPNRouterService] ExecuteAsync started");

        try
        {
            var settings = SettingsLoader.Load();
            _logger.LogInformation("[VPNRouterService] Loaded config, active profile: {Profile}",
                settings.ActiveProfile);

            _engine = new VpnEngine(Serilog.Log.Logger);

            _engine.StatusChanged += msg =>
                _logger.LogInformation("[VPNRouterService] {Status}", msg);

            _engine.RestartAttempted += (attempt, max) =>
                WriteEventLog($"sing-box restart attempt {attempt}/{max}", EventLogEntryType.Warning);

            _engine.Warning += msg =>
                _logger.LogWarning("[VPNRouterService] {Warn}", msg);

            await _engine.StartAsync(settings, stoppingToken);

            WriteEventLog(
                $"VPN Router started — profile: {_engine.ActiveProfileName}, PID: {_engine.SingBoxPid}",
                EventLogEntryType.Information);

            // Keep service alive until stop is requested
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[VPNRouterService] Stop requested");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "[VPNRouterService] Fatal error in ExecuteAsync");
            WriteEventLog($"Fatal error: {ex.Message}", EventLogEntryType.Error);
            throw; // causes service to report failure and stop
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[VPNRouterService] Stopping");
        WriteEventLog("VPN Router service stopping", EventLogEntryType.Information);

        try { _engine?.Stop(); } catch { }
        try { _engine?.Dispose(); } catch { }

        await base.StopAsync(cancellationToken);
        _logger.LogInformation("[VPNRouterService] Stopped");
        WriteEventLog("VPN Router service stopped", EventLogEntryType.Information);
    }

    // ─── Event Log ────────────────────────────────────────────────────────────

    private static void EnsureEventSource()
    {
        try
        {
            if (!EventLog.SourceExists(EventSourceName))
                EventLog.CreateEventSource(EventSourceName, EventLogName);
        }
        catch { /* may require elevated rights first run */ }
    }

    private static void WriteEventLog(string message, EventLogEntryType type)
    {
        try
        {
            EventLog.WriteEntry(EventSourceName, message, type);
        }
        catch { /* ignore if source not yet created */ }
    }
}
