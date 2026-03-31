using Serilog;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Monitors sing-box health and manages the full lifecycle:
/// - Periodic health check every N seconds
/// - Auto-restart on crash with exponential backoff
/// - Debounced process rescan (5s window) to minimize sing-box reloads
/// - Firewall rule management for block_on_vpn_fail
/// </summary>
public class HealthMonitor : IDisposable
{
    private readonly SingBoxManager _singBox;
    private readonly IProcessScanner _scanner;
    private readonly IFirewallManager _firewall;
    private readonly MonitoringSettings _settings;
    private readonly ILogger _logger;

    private System.Threading.Timer? _healthTimer;
    private System.Threading.Timer? _debounceTimer;

    private Profile _activeProfile = null!;
    private AppSettings _appSettings = null!;
    private ScanResult? _lastScan;
    private int _restartAttempts;
    private bool _vpnWasRunning;
    private bool _disposed;
    private bool _isStopping;   // set during HealthMonitor.Stop() to block crash handling

    // Cancels any pending AttemptRestart Task.Delay — prevents stale restarts
    // from firing after a successful reload has already happened.
    private CancellationTokenSource? _restartCts;

    // Debounce window — wait 5s after last new process before reloading
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(5);

    // Minimum time between full restarts to prevent restart storms.
    // If hot-reload fails and a full restart is needed, enforce at least this gap.
    private static readonly TimeSpan RestartCooldown = TimeSpan.FromSeconds(60);
    private DateTime _lastFullRestart = DateTime.MinValue;

    public event EventHandler? VpnStarted;
    public event EventHandler? VpnStopped;
    public event EventHandler<int>? RestartAttempted; // arg = attempt number

    public HealthMonitor(
        SingBoxManager singBox,
        IProcessScanner scanner,
        IFirewallManager firewall,
        MonitoringSettings settings,
        ILogger? logger = null)
    {
        _singBox = singBox;
        _scanner = scanner;
        _firewall = firewall;
        _settings = settings;
        _logger = logger ?? Log.Logger;

        _singBox.Crashed += OnSingBoxCrashed;
    }

    // ─── Start / Stop ─────────────────────────────────────────────────────────

    public void Start(Profile profile, AppSettings appSettings, ScanResult? initialScan = null)
    {
        _activeProfile = profile;
        _appSettings = appSettings;
        _restartAttempts = 0;
        _isStopping = false;
        _lastScan = initialScan; // baseline — prevents reload on first debounce if nothing changed

        var intervalMs = _settings.HealthCheckInterval * 1000;
        _healthTimer = new System.Threading.Timer(
            OnHealthTick, null, intervalMs, intervalMs);

        _logger.Information("[HealthMonitor] Started — check every {Sec}s, max {Max} restarts",
            _settings.HealthCheckInterval, _settings.MaxRestartAttempts);
    }

    public void Stop()
    {
        _isStopping = true;
        _restartCts?.Cancel();
        _healthTimer?.Dispose();
        _debounceTimer?.Dispose();
        _healthTimer = null;
        _debounceTimer = null;
        _logger.Information("[HealthMonitor] Stopped");
    }

    /// <summary>
    /// Call when a new process is detected by ETW that might need to be added.
    /// Resets the debounce timer — actual reload happens 5s after the last call.
    /// </summary>
    public void OnNewProcessDetected(string processName)
    {
        _logger.Debug("[HealthMonitor] New process detected: {Name} — debouncing", processName);

        // Reset the debounce timer
        _debounceTimer?.Dispose();
        _debounceTimer = new System.Threading.Timer(
            OnDebounceElapsed, null,
            (int)DebounceWindow.TotalMilliseconds,
            Timeout.Infinite); // fire once
    }

    // ─── Private: Health check ────────────────────────────────────────────────

    private void OnHealthTick(object? state)
    {
        try
        {
            var isHealthy = _singBox.IsHealthy();

            if (!isHealthy && _vpnWasRunning)
            {
                _logger.Warning("[HealthMonitor] Health check failed — sing-box is not healthy");
                AttemptRestart();
            }
            else if (isHealthy && !_vpnWasRunning)
            {
                _vpnWasRunning = true;
                _logger.Information("[HealthMonitor] VPN is up");
                VpnStarted?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[HealthMonitor] Exception in health tick");
        }
    }

    private void OnSingBoxCrashed(object? sender, EventArgs e)
    {
        if (_isStopping)
            return; // graceful shutdown in progress — ignore

        _vpnWasRunning = false;
        VpnStopped?.Invoke(this, EventArgs.Empty);

        // Enable firewall block rules — prevent traffic from leaking direct
        // while sing-box is down and TUN interface is gone
        try { _firewall.EnableBlockRules(); }
        catch (Exception ex) { _logger.Error(ex, "[HealthMonitor] Failed to enable firewall block rules on crash"); }

        if (_settings.RestartOnFailure)
            AttemptRestart();
    }

    private void AttemptRestart()
    {
        if (_restartAttempts >= _settings.MaxRestartAttempts)
        {
            _logger.Error("[HealthMonitor] Max restart attempts ({Max}) reached — giving up",
                _settings.MaxRestartAttempts);
            return;
        }

        _restartAttempts++;
        RestartAttempted?.Invoke(this, _restartAttempts);

        // Cancel any previously scheduled restart — only the latest one matters
        _restartCts?.Cancel();
        _restartCts = new CancellationTokenSource();
        var ct = _restartCts.Token;

        // Exponential backoff: 5s, 10s, 20s, 40s, 80s
        var delayMs = (int)Math.Pow(2, _restartAttempts - 1) * 5000;
        _logger.Warning("[HealthMonitor] Restarting sing-box (attempt {N}/{Max}) in {Delay}ms",
            _restartAttempts, _settings.MaxRestartAttempts, delayMs);

        Task.Delay(delayMs, ct).ContinueWith(_ =>
        {
            if (ct.IsCancellationRequested || _isStopping) return;

            // If sing-box is already running (e.g. debounce reload succeeded),
            // skip — no need to restart what's already up
            if (_singBox.IsRunning())
            {
                _logger.Debug("[HealthMonitor] sing-box already running — skipping scheduled restart");
                _restartAttempts = 0;
                return;
            }

            try
            {
                // Re-scan and regenerate config before restart
                var scan = _scanner.ScanForProfile(_activeProfile);
                var configJson = GenerateConfigJson(scan.ProcessNames);
                _singBox.ReloadConfigJson(configJson);
                _lastScan = scan;
                _lastFullRestart = DateTime.UtcNow;

                // Disable firewall block rules — sing-box TUN is back up
                try { _firewall.DisableBlockRules(); }
                catch (Exception fwEx) { _logger.Error(fwEx, "[HealthMonitor] Failed to disable firewall block rules after restart"); }

                _logger.Information("[HealthMonitor] sing-box restarted successfully");
                _restartAttempts = 0;
                _vpnWasRunning = true;
                VpnStarted?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[HealthMonitor] Restart attempt {N} failed", _restartAttempts);
            }
        }, CancellationToken.None); // ContinueWith always runs, checks ct inside
    }

    // ─── Private: Debounced process rescan ───────────────────────────────────

    private void OnDebounceElapsed(object? state)
    {
        if (_isStopping) return;

        try
        {
            _logger.Information("[HealthMonitor] Debounce elapsed — rescanning processes");

            var newScan = _scanner.ScanForProfile(_activeProfile);

            // Compare only the filtered (non-wildcard) names that actually reach sing-box.
            // This avoids spurious reloads when only wildcard patterns shift between scans.
            var newFiltered  = FilterForSingBox(newScan.ProcessNames);
            var prevFiltered = _lastScan == null ? null : FilterForSingBox(_lastScan.ProcessNames);

            if (prevFiltered != null &&
                new HashSet<string>(newFiltered, StringComparer.OrdinalIgnoreCase)
                    .SetEquals(prevFiltered))
            {
                _logger.Debug("[HealthMonitor] No effective process changes — skipping reload");
                _lastScan = newScan; // keep scan timestamp fresh
                return;
            }

            _logger.Information("[HealthMonitor] Process list changed ({Prev} → {New} processes) — reloading sing-box config",
                prevFiltered?.Count ?? 0, newFiltered.Count);

            // Cancel any pending AttemptRestart — we're doing a fresh reload now
            _restartCts?.Cancel();

            var configJson = GenerateConfigJson(newScan.ProcessNames);

            // Try hot-reload first (no restart fallback) to avoid TUN restart storms.
            // If hot-reload fails, only do a full restart if cooldown has elapsed.
            if (_singBox.TryReloadConfigJson(configJson))
            {
                _lastScan = newScan;
                _restartAttempts = 0;
                _logger.Information("[HealthMonitor] Hot-reload succeeded with {Count} processes", newFiltered.Count);
            }
            else
            {
                var sinceLastRestart = DateTime.UtcNow - _lastFullRestart;
                if (sinceLastRestart < RestartCooldown)
                {
                    _logger.Warning("[HealthMonitor] Hot-reload failed, but cooldown active ({Remaining}s left) — deferring full restart",
                        (int)(RestartCooldown - sinceLastRestart).TotalSeconds);
                    // Save scan so next debounce can try again
                    _lastScan = newScan;
                }
                else
                {
                    _logger.Warning("[HealthMonitor] Hot-reload failed — performing full restart");
                    _lastFullRestart = DateTime.UtcNow;
                    _singBox.Restart();
                    _lastScan = newScan;
                    _restartAttempts = 0;
                    _logger.Information("[HealthMonitor] Full restart completed with {Count} processes", newFiltered.Count);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[HealthMonitor] Error in debounced rescan");
        }
    }

    /// <summary>
    /// Generates sing-box config JSON. Uses CustomConfigInjector for custom mode,
    /// ConfigGenerator for generated mode. Handles both transparently.
    /// </summary>
    private string GenerateConfigJson(List<string> processNames)
    {
        var isCustom = (_appSettings.App.ConfigMode ?? "generated")
            .Equals("custom", StringComparison.OrdinalIgnoreCase);

        if (isCustom)
        {
            // Try named ProgramData copy first (multi-config)
            var configName = _appSettings.App.ActiveCustomConfig;
            if (!string.IsNullOrEmpty(configName))
            {
                var namedPath = CustomConfigInjector.GetProgramDataPath(configName);
                if (File.Exists(namedPath))
                {
                    var rawJson = File.ReadAllText(namedPath);
                    return CustomConfigInjector.Inject(rawJson, processNames, _appSettings);
                }
            }

            // Fallback: old single custom.json or custom_config path
            var legacyPath = Environment.ExpandEnvironmentVariables(
                @"%ProgramData%\VPNRouter\config\custom.json");
            if (File.Exists(legacyPath))
            {
                var rawJson = File.ReadAllText(legacyPath);
                return CustomConfigInjector.Inject(rawJson, processNames, _appSettings);
            }

            if (!string.IsNullOrEmpty(_appSettings.App.CustomConfig))
            {
                var customPath = Environment.ExpandEnvironmentVariables(_appSettings.App.CustomConfig);
                var fallbackJson = File.ReadAllText(customPath);
                return CustomConfigInjector.Inject(fallbackJson, processNames, _appSettings);
            }
        }

        var config = ConfigGenerator.Generate(_activeProfile, processNames, _appSettings);
        return ConfigGenerator.Serialize(config);
    }

    /// <summary>
    /// Returns only exact process names (no wildcards) — the same subset
    /// that ConfigGenerator passes to sing-box process_name rules.
    /// </summary>
    private static List<string> FilterForSingBox(IEnumerable<string> names) =>
        names.Where(p => !p.Contains('*') && !p.Contains('?'))
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .ToList();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
