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

    // v2.31.5-r2 (user-reported VPN-loss bug):
    // tracks user intent — true between Start() and Stop(), regardless of
    // whether sing-box is currently up. Pre-fix the only crash-recovery path
    // was the Task.Delay scheduled inside AttemptRestart from OnSingBoxCrashed.
    // If that continuation didn't fire (laptop slept and the task got
    // cancelled, App quit between schedule and fire, dispatcher starved,
    // exception thrown before logger flushed) the user was stranded:
    //   - _vpnWasRunning was set to false by OnSingBoxCrashed,
    //   - the periodic health-tick check `!isHealthy && _vpnWasRunning`
    //     evaluated to false-after-crash and did nothing,
    //   - firewall block rules stayed enabled forever, but VPN never came
    //     back without manual user intervention.
    // The new flag drives a defensive recovery path inside OnHealthTick
    // that re-attempts AttemptRestart whenever sing-box is dead, the user
    // intends VPN to run, and we're not in the middle of Stop().
    // See plans/release-notes-v2.31.5-r2.md for the full timeline.
    private bool _shouldBeRunning;

    // Cancels any pending AttemptRestart Task.Delay — prevents stale restarts
    // from firing after a successful reload has already happened.
    private CancellationTokenSource? _restartCts;

    // v2.31.6-r8 — serialise AttemptRestart against itself.
    // Iter#4 audit (2026-05-04) flagged that AttemptRestart is invokable from
    // two callbacks running on different threadpool threads:
    //   • OnHealthTick (periodic timer) — every 5/30 s while VPN should be up
    //   • OnSingBoxCrashed (Process.Exited event)
    // Pre-r8 the body did `_restartAttempts++;` and a non-atomic CTS swap
    // (`var old = _restartCts; _restartCts = new ...;`). Two threads could
    // race the increment (both passing the MaxRestartAttempts gate when
    // they shouldn't) AND race the CTS swap (one Cancel/Dispose hitting an
    // already-disposed CTS, or a leaked old reference). The lock here makes
    // the whole "increment + swap CTS + schedule Task.Delay" sequence
    // atomic with respect to itself; the existing Task.Delay continuation
    // remains async (it checks `ct.IsCancellationRequested` and `_isStopping`
    // before doing real work, so a stale restart from a cancelled attempt
    // is still benign).
    private readonly object _attemptRestartLock = new();

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
        _shouldBeRunning = true;   // v2.31.5-r2: arms the OnHealthTick recovery path
        _lastScan = initialScan; // baseline — prevents reload on first debounce if nothing changed

        // Strict mode: poll every 5 seconds instead of the configured interval
        // (default 30s). Catches silent hangs faster — process exit events
        // already fire immediately, so this only matters for "alive but stuck"
        // sing-box where Clash API stops responding.
        var intervalSeconds = appSettings.App.StrictMode ? 5 : _settings.HealthCheckInterval;
        var intervalMs = intervalSeconds * 1000;

        _healthTimer = new System.Threading.Timer(
            OnHealthTick, null, intervalMs, intervalMs);

        _logger.Information("[HealthMonitor] Started — check every {Sec}s, max {Max} restarts (strict mode: {Strict})",
            intervalSeconds, _settings.MaxRestartAttempts, appSettings.App.StrictMode);
    }

    public void Stop()
    {
        _isStopping = true;
        _shouldBeRunning = false;  // v2.31.5-r2: disarm OnHealthTick recovery
        // v2.31.0-r1 (CO-2): also dispose the CTS — Cancel alone leaves the
        // wait handle alive until the GC catches the finalizer. Symmetric
        // with the AttemptRestart swap pattern.
        var cts = _restartCts;
        _restartCts = null;
        if (cts != null)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            cts.Dispose();
        }
        // v2.31.0-r1 (CO-1): atomic swap on shutdown so a late ETW callback
        // racing OnNewProcessDetected can't observe a half-disposed state.
        var ht = System.Threading.Interlocked.Exchange(ref _healthTimer, null);
        var dt = System.Threading.Interlocked.Exchange(ref _debounceTimer, null);
        ht?.Dispose();
        dt?.Dispose();
        _logger.Information("[HealthMonitor] Stopped");
    }

    /// <summary>
    /// Call when a new process is detected by ETW/polling that might need to be added.
    /// Resets the debounce timer — actual reload happens 5s after the last call.
    ///
    /// IMPORTANT: skips debounce entirely if the process name is already in
    /// _lastScan.ProcessNames. Browser tabs spawn many child processes with names
    /// already monitored (chrome.exe, msedge.exe), and rescanning + hot-reloading
    /// for each one creates a "process rescan storm" that visibly stalls the VPN
    /// for 1-3 seconds when many tabs are opened in quick succession.
    /// </summary>
    public void OnNewProcessDetected(string processName)
    {
        // Skip if the name is already monitored — config wouldn't change anyway.
        // Saves a full process rescan + sing-box hot-reload per browser tab spawn.
        if (_lastScan?.ProcessNames != null &&
            _lastScan.ProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase))
        {
            _logger.Verbose("[HealthMonitor] Process {Name} already monitored — skipping debounce", processName);
            return;
        }

        _logger.Debug("[HealthMonitor] New process detected: {Name} — debouncing", processName);

        // v2.31.0-r1 (CO-1 audit fix): atomic-swap the debounce timer.
        // Pre-fix this read+null+new sequence was non-atomic; ETW callbacks
        // fire on multiple threadpool threads (EtwProcessMonitor.cs:98-130),
        // so two concurrent calls could:
        //   - both read the same _debounceTimer reference → both call
        //     Dispose on the same instance (ObjectDisposedException) AND
        //     leak the second new'd timer (assigned-then-overwritten),
        //   - or fire two timers back-to-back, doubling the rescan storm.
        // Now: Interlocked.Exchange swaps in the new timer atomically and
        // returns the previous (or null), which we Dispose safely once.
        var newTimer = new System.Threading.Timer(
            OnDebounceElapsed, null,
            (int)DebounceWindow.TotalMilliseconds,
            Timeout.Infinite); // fire once
        var oldTimer = System.Threading.Interlocked.Exchange(ref _debounceTimer, newTimer);
        oldTimer?.Dispose();
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
            else if (!isHealthy && _shouldBeRunning && !_isStopping)
            {
                // v2.31.5-r2 (user-reported VPN-loss bug): defensive recovery.
                // sing-box is dead AND user wants VPN up AND we're not in
                // the middle of Stop(). This path catches the gap where
                // OnSingBoxCrashed scheduled an AttemptRestart Task.Delay
                // that never fired its continuation (laptop slept across the
                // 5s deadline; App was killed-then-revived by the OS;
                // dispatcher starved long enough that the CTS got cancelled
                // by Stop()-then-Start() outside our intent). Pre-fix this
                // condition silently stranded the user: _vpnWasRunning was
                // set false by OnSingBoxCrashed, the original `!isHealthy
                // && _vpnWasRunning` check no longer matched, and recovery
                // never happened until manual reconnect.
                //
                // Bound by _settings.MaxRestartAttempts — same cap as the
                // crash path. If we hit the ceiling here, AttemptRestart
                // logs "Max restart attempts reached" and gives up; the UI
                // surfaces "VPN down" and the user can press Reconnect.
                _logger.Warning("[HealthMonitor] sing-box dead while user wants VPN up — initiating recovery (intended-running path)");
                AttemptRestart();
            }
            else if (isHealthy && !_vpnWasRunning)
            {
                _vpnWasRunning = true;
                // v2.31.5-r2: a successful health observation after a crash
                // gap means recovery worked. Reset the attempt counter so
                // the next unrelated crash doesn't inherit a half-burned
                // backoff budget — the user shouldn't pay for previous
                // restart history once they're connected again.
                _restartAttempts = 0;
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
        // v2.31.6-r8: serialise the attempt-counter increment + CTS swap
        // so concurrent OnHealthTick and OnSingBoxCrashed callbacks don't
        // race the gate or leak/double-dispose the cancellation token.
        // The Task.Delay continuation is left outside the lock so we don't
        // hold the lock across an async wait.
        int attempt;
        CancellationToken ct;
        int delayMs;
        lock (_attemptRestartLock)
        {
            if (_restartAttempts >= _settings.MaxRestartAttempts)
            {
                _logger.Error("[HealthMonitor] Max restart attempts ({Max}) reached — giving up",
                    _settings.MaxRestartAttempts);
                return;
            }

            _restartAttempts++;
            attempt = _restartAttempts;

            // Cancel any previously scheduled restart — only the latest one matters.
            // v2.31.0-r1 (CO-2 audit fix): the previous code reassigned _restartCts
            // without disposing the old one — one CancellationTokenSource leaked
            // per restart attempt. Long-running sessions with many crash-restart
            // cycles accumulated CTS instances. Now: Cancel + Dispose the previous
            // before swapping in the new one.
            // v2.31.6-r8: the swap is now under the lock so a concurrent caller
            // can't observe a half-published CTS (read after our write to
            // _restartCts but before we Cancel/Dispose oldCts).
            var oldCts = _restartCts;
            _restartCts = new CancellationTokenSource();
            ct = _restartCts.Token;
            if (oldCts != null)
            {
                try { oldCts.Cancel(); } catch (ObjectDisposedException) { /* already disposed elsewhere */ }
                oldCts.Dispose();
            }

            // Exponential backoff: 5s, 10s, 20s, 40s, 80s
            delayMs = (int)Math.Pow(2, attempt - 1) * 5000;
        }

        RestartAttempted?.Invoke(this, attempt);

        _logger.Warning("[HealthMonitor] Restarting sing-box (attempt {N}/{Max}) in {Delay}ms",
            attempt, _settings.MaxRestartAttempts, delayMs);

        Task.Delay(delayMs, ct).ContinueWith(_ =>
        {
            if (ct.IsCancellationRequested || _isStopping) return;

            // If sing-box is already running (e.g. debounce reload succeeded),
            // skip — no need to restart what's already up.
            // v2.31.6-r8: counter-reset under the lock so a concurrent
            // AttemptRestart caller observes the cleared budget atomically
            // (otherwise it could read mid-write of `_restartAttempts++`
            // and produce an off-by-one cap evaluation).
            if (_singBox.IsRunning())
            {
                _logger.Debug("[HealthMonitor] sing-box already running — skipping scheduled restart");
                lock (_attemptRestartLock) { _restartAttempts = 0; }
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
                // v2.31.6-r8: counter-reset under the lock (see comment above).
                lock (_attemptRestartLock) { _restartAttempts = 0; }
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

        // v2.28.2: defense-in-depth — also resolve here. HealthMonitor's
        // _appSettings reference is normally kept consistent by VpnEngine's
        // Resolve() at StartAsync, but if something clears _appSettings.Vless.
        // Servers between then and a hot-reload trigger from health rescan,
        // we'd produce a broken config (no proxy outbound). Resolve again to
        // be safe — idempotent if already populated.
        VlessServersResolver.Resolve(_appSettings);

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
