using System.Diagnostics;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// Manages Windows Firewall rules for block_on_vpn_fail.
///
/// Lifecycle:
/// 1. CreateBlockRules() — creates DISABLED outbound block rules at VPN start
/// 2. Rules stay DISABLED while VPN is running (sing-box TUN handles routing)
/// 3. EnableBlockRules() — called by HealthMonitor when sing-box crashes
///    (prevents traffic from leaking direct while VPN is down)
/// 4. DisableBlockRules() — called when sing-box successfully restarts
/// 5. DeleteAllRules() — called on clean shutdown
///
/// Key insight: while sing-box is running, TUN captures all targeted traffic
/// and routes it through proxy. Firewall rules are NOT needed during normal
/// operation. They are a safety net for the brief window when sing-box dies
/// and TUN is gone — without them, traffic would go direct.
/// </summary>
public class FirewallManager : IDisposable
{
    private const string RulePrefix = "VPNRouter_Block_";
    private readonly ILogger _logger;
    private readonly List<string> _managedRules = new();
    private bool _disposed;

    public FirewallManager(ILogger? logger = null)
    {
        _logger = logger ?? Log.Logger;
    }

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    /// <summary>
    /// Create DISABLED block rules for all processes with block_on_vpn_fail=true.
    /// Rules stay disabled while VPN is running normally.
    /// </summary>
    public void CreateBlockRules(IEnumerable<string> processNames)
    {
        CleanupOrphanedRules();

        // netsh does not support wildcards in program paths or rule names —
        // skip patterns; only create rules for exact .exe names
        var exact = processNames
            .Where(n => !n.Contains('*') && !n.Contains('?'))
            .ToList();

        foreach (var name in exact)
        {
            var ruleName = RulePrefix + name.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
            CreateBlockRule(ruleName, name, enabled: false);
            _managedRules.Add(ruleName);
        }

        _logger.Information("[Firewall] Created {Count} block rules (disabled — will enable on VPN crash)", _managedRules.Count);
    }

    /// <summary>
    /// Enable block rules — call ONLY when sing-box crashes.
    /// This blocks all direct outbound traffic for targeted processes,
    /// preventing data leaks while VPN is down.
    /// </summary>
    public void EnableBlockRules()
    {
        foreach (var rule in _managedRules)
        {
            RunNetsh($"advfirewall firewall set rule name=\"{rule}\" new enable=yes");
        }
        _logger.Information("[Firewall] ENABLED {Count} block rules (VPN down — leak protection active)", _managedRules.Count);
    }

    /// <summary>
    /// Disable block rules — call when sing-box successfully starts/restarts
    /// or during clean shutdown.
    /// </summary>
    public void DisableBlockRules()
    {
        foreach (var rule in _managedRules)
        {
            RunNetsh($"advfirewall firewall set rule name=\"{rule}\" new enable=no");
        }
        _logger.Information("[Firewall] Disabled {Count} block rules (VPN up — TUN handles routing)", _managedRules.Count);
    }

    /// <summary>
    /// Delete all managed rules — call on clean shutdown.
    /// </summary>
    public void DeleteAllRules()
    {
        foreach (var rule in _managedRules)
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{rule}\"");
            _logger.Debug("[Firewall] Deleted rule: {Rule}", rule);
        }
        _managedRules.Clear();
        _logger.Information("[Firewall] All VPNRouter firewall rules deleted");
    }

    // ─── Private ──────────────────────────────────────────────────────────────

    private void CreateBlockRule(string ruleName, string processName, bool enabled)
    {
        var enabledStr = enabled ? "yes" : "no";

        // Outbound block — blocks all direct internet access for this process.
        // When sing-box TUN is up, traffic goes through TUN (not affected by this rule).
        // When sing-box is down, TUN is gone, traffic would go direct — this rule blocks it.
        RunNetsh($"advfirewall firewall add rule " +
                 $"name=\"{ruleName}\" " +
                 $"dir=out " +
                 $"action=block " +
                 $"program=\"{ResolveProcessPath(processName)}\" " +
                 $"enable={enabledStr} " +
                 $"profile=any " +
                 $"description=\"VPNRouter block_on_vpn_fail\"");

        _logger.Debug("[Firewall] Created rule '{Rule}' for {Process} (enabled: {Enabled})",
            ruleName, processName, enabled);
    }

    private static string ResolveProcessPath(string processName)
    {
        // Try to find the actual .exe path from running processes
        var name = Path.GetFileNameWithoutExtension(processName);
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName(name);
            if (procs.Length > 0)
            {
                var path = procs[0].MainModule?.FileName;
                if (!string.IsNullOrEmpty(path)) return path;
            }
        }
        catch { /* process may have exited or access denied */ }

        // If not running, use process name as-is (firewall accepts this too)
        return processName;
    }

    /// <summary>
    /// Remove any VPNRouter firewall rules left from a previous crash.
    /// </summary>
    public void CleanupOrphanedRules()
    {
        // Delete all rules matching our prefix
        RunNetsh($"advfirewall firewall delete rule name=\"{RulePrefix}*\"");
        _logger.Debug("[Firewall] Cleaned up any orphaned rules");
    }

    private void RunNetsh(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);

            var exitCode = proc?.ExitCode ?? -1;
            if (exitCode != 0)
            {
                _logger.Warning("[Firewall] netsh returned {Code} for: {Args}", exitCode, arguments);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Firewall] netsh failed: {Args}", arguments);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DeleteAllRules();
    }
}
