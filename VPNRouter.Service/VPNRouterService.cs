using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using VPNRouter.Core.Services;

namespace VPNRouter.Service;

/// <summary>
/// Windows Service implementation.
/// Runs as a BackgroundService hosted by Microsoft.Extensions.Hosting.
/// Delegates all VPN lifecycle to VpnEngine.
///
/// Race condition fix: ExecuteAsync holds a lock (_startupLock) during startup.
/// StopAsync waits for startup to complete (or be cancelled) before calling Stop(),
/// ensuring no zombie sing-box processes are left behind.
/// </summary>
public class VPNRouterService : BackgroundService
{
    private readonly ILogger<VPNRouterService> _logger;
    private VpnEngine? _engine;

    /// <summary>
    /// Signals that ExecuteAsync has finished its startup phase (either successfully or via cancellation).
    /// StopAsync waits on this before calling _engine.Stop() to avoid the race where
    /// Stop() runs before sing-box is started, leaving a zombie process.
    /// </summary>
    private readonly TaskCompletionSource _startupComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

            _engine = new VpnEngine(
                new ProcessScanner(Serilog.Log.Logger),
                () => new FirewallManager(Serilog.Log.Logger),
                () => new EtwProcessMonitor(Serilog.Log.Logger),
                Serilog.Log.Logger);

            _engine.StatusChanged += msg =>
                _logger.LogInformation("[VPNRouterService] {Status}", msg);

            _engine.RestartAttempted += (attempt, max) =>
                WriteEventLog($"sing-box restart attempt {attempt}/{max}", EventLogEntryType.Warning);

            _engine.Warning += msg =>
                _logger.LogWarning("[VPNRouterService] {Warn}", msg);

            await _engine.StartAsync(settings, stoppingToken);

            // Signal that startup completed successfully
            _startupComplete.TrySetResult();

            WriteEventLog(
                $"VPN Router started — profile: {_engine.ActiveProfileName}, PID: {_engine.SingBoxPid}",
                EventLogEntryType.Information);

            // Keep service alive until stop is requested
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[VPNRouterService] Stop requested (startup was cancelled or service stopping)");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "[VPNRouterService] Fatal error in ExecuteAsync");
            WriteEventLog($"Fatal error: {ex.Message}", EventLogEntryType.Error);
            throw; // causes service to report failure and stop
        }
        finally
        {
            // Always signal completion — even on error/cancellation.
            // This unblocks StopAsync if it's waiting.
            _startupComplete.TrySetResult();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[VPNRouterService] Stopping — waiting for startup to finish");
        WriteEventLog("VPN Router service stopping", EventLogEntryType.Information);

        // Wait for ExecuteAsync to finish its startup phase.
        // This prevents the race where Stop() is called before sing-box is started,
        // leaving a zombie sing-box process. Timeout after 15s to avoid deadlock.
        try
        {
            await Task.WhenAny(_startupComplete.Task, Task.Delay(15000, cancellationToken));
        }
        catch (OperationCanceledException) { /* timeout or host shutdown, proceed with cleanup */ }

        _logger.LogInformation("[VPNRouterService] Startup phase done, cleaning up engine");

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
