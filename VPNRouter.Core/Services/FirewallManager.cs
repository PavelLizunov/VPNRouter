using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;
using VPNRouter.Core.Interfaces;

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
public class FirewallManager : IFirewallManager
{
    private const string RulePrefix = "VPNRouter_Block_";
    private readonly ILogger _logger;
    private readonly List<string> _managedRules = new();
    private bool _disposed;

    // v2.31.6-r19: netsh.exe writes its output in the OEM code page (CP-866
    // on RU Windows, CP-850 on DE/FR/etc.), but .NET's default redirect
    // assumes the system ANSI page (CP-1251 on RU). The mismatch produced
    // mojibake like "РќРё РѕРґРЅРѕ РїСЂР°РІРёР»Рѕ" in vpnrouter.log every time a
    // firewall rule operation hit a localized warning. Resolve once at type
    // init so each PSI we spawn can pin the right encoding.
    private static readonly Encoding ConsoleEncoding = ResolveConsoleEncoding();

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    private static Encoding ResolveConsoleEncoding()
    {
        if (!OperatingSystem.IsWindows()) return Encoding.UTF8;
        try
        {
            // .NET Core / 8 ships only UTF-8/16/32 + ASCII out of the box.
            // CodePagesEncodingProvider unlocks legacy single-byte pages.
            // Idempotent — safe even if another component already registered.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)GetOEMCP());
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

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

            // Resolve full path — skip this process if path not found
            var exePath = ResolveProcessPath(name);
            if (exePath == null)
            {
                _logger.Warning("[Firewall] Skipping rule for {Process} — exe path not found (process not running?)", name);
                continue;
            }

            if (CreateBlockRule(ruleName, exePath, enabled: false))
            {
                _managedRules.Add(ruleName);
            }
            else
            {
                _logger.Warning("[Firewall] Failed to create rule for {Process} — netsh error", name);
            }
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

    /// <summary>
    /// Create a single block rule. Returns true if netsh succeeded.
    /// </summary>
    private bool CreateBlockRule(string ruleName, string programPath, bool enabled)
    {
        var enabledStr = enabled ? "yes" : "no";

        // Outbound block — blocks all direct internet access for this process.
        // When sing-box TUN is up, traffic goes through TUN (not affected by this rule).
        // When sing-box is down, TUN is gone, traffic would go direct — this rule blocks it.
        var success = RunNetsh($"advfirewall firewall add rule " +
                 $"name=\"{ruleName}\" " +
                 $"dir=out " +
                 $"action=block " +
                 $"program=\"{programPath}\" " +
                 $"enable={enabledStr} " +
                 $"profile=any " +
                 $"description=\"VPNRouter block_on_vpn_fail\"");

        if (success)
        {
            _logger.Debug("[Firewall] Created rule '{Rule}' for {Program} (enabled: {Enabled})",
                ruleName, programPath, enabled);
        }

        return success;
    }

    /// <summary>
    /// Resolve the full path to an executable.
    /// 1) Check running processes (most reliable — gives actual filesystem path)
    /// 2) Fall back to where.exe (finds exe on PATH, e.g. for system processes)
    /// 3) Return null if not found — caller should skip this rule
    /// </summary>
    private string? ResolveProcessPath(string processName)
    {
        var nameNoExt = Path.GetFileNameWithoutExtension(processName);

        // 1. Try running processes — gives the real path with correct casing
        try
        {
            var procs = Process.GetProcessesByName(nameNoExt);
            foreach (var proc in procs)
            {
                try
                {
                    var path = proc.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        return path;
                }
                catch { /* access denied for some processes — try next */ }
                finally { proc.Dispose(); }
            }
        }
        catch { /* GetProcessesByName itself can fail */ }

        // 2. Try where.exe — finds executables on PATH
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = processName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = ConsoleEncoding,
                StandardErrorEncoding = ConsoleEncoding
            };

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var output = proc.StandardOutput.ReadLine();
                proc.WaitForExit(3000);
                if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output) && File.Exists(output))
                    return output;
            }
        }
        catch { /* where.exe not available or failed */ }

        _logger.Debug("[Firewall] Could not resolve path for {Process}", processName);
        return null;
    }

    /// <summary>
    /// Remove any VPNRouter firewall rules left from a previous crash.
    /// netsh does NOT support wildcards in rule names, so we enumerate
    /// all rules and delete those matching our prefix by exact name.
    /// </summary>
    public void CleanupOrphanedRules()
    {
        var orphaned = FindRulesByPrefix(RulePrefix);

        if (orphaned.Count == 0)
        {
            _logger.Debug("[Firewall] No orphaned rules found");
            return;
        }

        foreach (var ruleName in orphaned)
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"");
            _logger.Debug("[Firewall] Deleted orphaned rule: {Rule}", ruleName);
        }

        _logger.Information("[Firewall] Cleaned up {Count} orphaned rules", orphaned.Count);
    }

    /// <summary>
    /// Enumerate firewall rules whose name starts with the given prefix.
    /// Uses 'netsh advfirewall firewall show rule name=all' and parses output.
    /// </summary>
    private List<string> FindRulesByPrefix(string prefix)
    {
        var result = new List<string>();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = "advfirewall firewall show rule name=all dir=out",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = ConsoleEncoding,
                StandardErrorEncoding = ConsoleEncoding
            };

            using var proc = Process.Start(psi);
            if (proc == null) return result;

            // Read all output — can be large, but we only need rule names
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10_000);

            // v2.31.0-r1 (CO-5 audit fix): the previous parser matched ANY
            // line where the value-after-`:` started with the prefix. On
            // localized Windows (RU/DE/ES) `netsh` outputs `Description:`
            // / `Описание:` / `Beschreibung:` BESIDE `Rule Name:` / `Имя
            // правила:` / `Regelname:`. If a user happened to have any
            // firewall rule whose Description began with `VPNRouter_Block_`
            // — including descriptions of UNRELATED rules — they'd get
            // silently deleted by FlushOrphanRules at startup. Real risk
            // of clobbering user firewall config on non-EN locales.
            //
            // Fix: structurally rely on the BLANK-LINE-separated rule
            // blocks. The first field of each block is always the rule
            // name (regardless of locale label). Track block boundaries
            // and only inspect the first colon-line per block.
            var inNewBlock = true;
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    inNewBlock = true;
                    continue;
                }
                if (!inNewBlock) continue;
                inNewBlock = false; // consume this block's first field

                var colonIdx = trimmed.IndexOf(':');
                if (colonIdx < 0) continue;

                var value = trimmed[(colonIdx + 1)..].Trim();
                if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(value);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Firewall] Failed to enumerate firewall rules");
        }

        return result;
    }

    /// <summary>
    /// Execute a netsh command. Returns true if exit code is 0.
    /// </summary>
    private bool RunNetsh(string arguments)
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
                RedirectStandardError = true,
                StandardOutputEncoding = ConsoleEncoding,
                StandardErrorEncoding = ConsoleEncoding
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                _logger.Warning("[Firewall] Failed to start netsh.exe");
                return false;
            }

            // Read streams before WaitForExit to avoid deadlocks on large output
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);

            if (proc.ExitCode != 0)
            {
                _logger.Warning("[Firewall] netsh returned {Code} for: {Args} | stdout: {Out} | stderr: {Err}",
                    proc.ExitCode, arguments, stdout.Trim(), stderr.Trim());
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Firewall] netsh failed: {Args}", arguments);
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DeleteAllRules();
    }
}
