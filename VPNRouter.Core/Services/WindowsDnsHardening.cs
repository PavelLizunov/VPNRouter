#if PLATFORM_WINDOWS
using System.Diagnostics;
using Microsoft.Win32;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// Closes Windows DNS leak vectors that bypass our TUN routing:
///
/// 1. SMHNR (Smart Multi-Homed Name Resolution)
///    Windows 8+ DNS client sends DNS queries to ALL active network adapters
///    in PARALLEL and uses the first response. With multiple VPNs running
///    (e.g. VPNRouter TUN + AmneziaWG), DNS leaks because the query goes
///    out the secondary adapter without sing-box ever seeing it.
///    Fix: HKLM\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient\DisableSmartNameResolution = 1
///
/// 2. Parallel A+AAAA queries
///    DNS client sends A and AAAA in parallel. Same multi-homed leak vector.
///    Fix: HKLM\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DisableParallelAandAAAA = 1
///
/// 3. TUN interface metric
///    Windows picks the interface with the lowest metric for DNS routing
///    when SMHNR is off. We pin VPNRouter-TUN to metric 1 (highest priority)
///    so it always wins over physical adapters and other VPN tunnels.
///
/// Original values are saved to state.json before changes and restored on Stop().
/// All operations require admin (which we already have).
/// </summary>
public static class WindowsDnsHardening
{
    private const string SmhnrPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient";
    private const string SmhnrPolicyValue = "DisableSmartNameResolution";

    private const string ParallelKey = @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters";
    private const string ParallelValue = "DisableParallelAandAAAA";

    private const string TunInterfaceAlias = "VPNRouter-TUN";

    private static readonly string StatePath =
        Path.Combine(AppPaths.DataDir, "dns-hardening-state.json");

    /// <summary>
    /// Apply DNS hardening: disable SMHNR + parallel A/AAAA, set TUN metric.
    /// Saves original values so they can be restored later.
    /// </summary>
    public static void Apply(ILogger? logger = null)
    {
        var log = logger ?? Log.Logger;

        try
        {
            // Crash recovery: if a state file exists from a previous run that
            // didn't get a clean Stop(), restore those values FIRST so we read
            // the user's true original settings (not our modified ones).
            if (File.Exists(StatePath))
            {
                log.Information("[DnsHardening] Found stale state file — restoring before re-apply");
                Restore(log);
            }

            var state = new HardeningState
            {
                Smhnr = SaveAndSet(Registry.LocalMachine, SmhnrPolicyKey, SmhnrPolicyValue, 1, log),
                ParallelAAAA = SaveAndSet(Registry.LocalMachine, ParallelKey, ParallelValue, 1, log),
                TunMetricChanged = TrySetTunMetric(1, log)
            };

            SaveState(state);
            log.Information("[DnsHardening] Applied — SMHNR={Smhnr}, ParallelAAAA={Par}, TUN metric set={Metric}",
                state.Smhnr.HadValue ? "was " + state.Smhnr.OldValue : "was unset",
                state.ParallelAAAA.HadValue ? "was " + state.ParallelAAAA.OldValue : "was unset",
                state.TunMetricChanged);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[DnsHardening] Apply failed (non-fatal)");
        }
    }

    /// <summary>
    /// Restore original DNS settings.
    /// </summary>
    public static void Restore(ILogger? logger = null)
    {
        var log = logger ?? Log.Logger;

        try
        {
            var state = LoadState();
            if (state == null)
            {
                log.Debug("[DnsHardening] No saved state — nothing to restore");
                return;
            }

            RestoreValue(Registry.LocalMachine, SmhnrPolicyKey, SmhnrPolicyValue, state.Smhnr, log);
            RestoreValue(Registry.LocalMachine, ParallelKey, ParallelValue, state.ParallelAAAA, log);

            // Reset TUN metric (only matters if interface still exists, e.g. crash recovery)
            if (state.TunMetricChanged)
                TrySetTunMetric(0, log); // 0 = automatic

            try { File.Delete(StatePath); } catch { }
            log.Information("[DnsHardening] Restored to original values");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[DnsHardening] Restore failed (non-fatal)");
        }
    }

    // ─── Private ──────────────────────────────────────────────────────────────

    private static SavedRegValue SaveAndSet(RegistryKey root, string keyPath, string valueName, int newValue, ILogger log)
    {
        var saved = new SavedRegValue();
        try
        {
            using var key = root.CreateSubKey(keyPath, writable: true);
            if (key == null)
            {
                log.Warning("[DnsHardening] Could not open/create {Path}", keyPath);
                return saved;
            }

            var existing = key.GetValue(valueName);
            if (existing != null && existing is int existingInt)
            {
                saved.HadValue = true;
                saved.OldValue = existingInt;
            }

            key.SetValue(valueName, newValue, RegistryValueKind.DWord);
            log.Debug("[DnsHardening] Set {Path}\\{Value} = {New} (was: {Old})",
                keyPath, valueName, newValue, saved.HadValue ? saved.OldValue.ToString() : "unset");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[DnsHardening] Failed to set {Path}\\{Value}", keyPath, valueName);
        }
        return saved;
    }

    private static void RestoreValue(RegistryKey root, string keyPath, string valueName, SavedRegValue saved, ILogger log)
    {
        try
        {
            using var key = root.OpenSubKey(keyPath, writable: true);
            if (key == null) return;

            if (saved.HadValue)
            {
                key.SetValue(valueName, saved.OldValue, RegistryValueKind.DWord);
                log.Debug("[DnsHardening] Restored {Path}\\{Value} = {Val}", keyPath, valueName, saved.OldValue);
            }
            else
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
                log.Debug("[DnsHardening] Deleted {Path}\\{Value} (was unset originally)", keyPath, valueName);
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[DnsHardening] Failed to restore {Path}\\{Value}", keyPath, valueName);
        }
    }

    /// <summary>
    /// Sets VPNRouter-TUN interface metric via netsh.
    /// metric=1 means highest priority; metric=0 means automatic.
    /// </summary>
    private static bool TrySetTunMetric(int metric, ILogger log)
    {
        try
        {
            // PowerShell: Set-NetIPInterface -InterfaceAlias "VPNRouter-TUN" -InterfaceMetric N
            // Avoid PowerShell startup overhead — use netsh directly
            var args = $"interface ipv4 set interface \"{TunInterfaceAlias}\" metric={metric}";
            var psi = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(5000);

            if (proc.ExitCode == 0)
            {
                log.Debug("[DnsHardening] Set TUN metric={Metric}", metric);
                return true;
            }
            log.Debug("[DnsHardening] netsh metric set returned {Code}", proc.ExitCode);
            return false;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[DnsHardening] Failed to set TUN metric");
            return false;
        }
    }

    private static void SaveState(HardeningState state)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(state, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(StatePath, json);
        }
        catch { }
    }

    private static HardeningState? LoadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            var json = File.ReadAllText(StatePath);
            return Newtonsoft.Json.JsonConvert.DeserializeObject<HardeningState>(json);
        }
        catch
        {
            return null;
        }
    }

    // ─── State types ──────────────────────────────────────────────────────────

    private class HardeningState
    {
        public SavedRegValue Smhnr { get; set; } = new();
        public SavedRegValue ParallelAAAA { get; set; } = new();
        public bool TunMetricChanged { get; set; }
    }

    private class SavedRegValue
    {
        public bool HadValue { get; set; }
        public int OldValue { get; set; }
    }
}
#endif
