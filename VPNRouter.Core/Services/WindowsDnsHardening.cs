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
        => TrySetTunMetricViaRunner(metric, _runnerOverride ?? new ProcessRunner(), log, TunInterfaceAlias);

    /// <summary>
    /// Phase 2G test seam — netsh call routed through <see cref="IProcessRunner"/>.
    /// Internal so <c>VPNRouter.Tests</c> can inject a <c>FakeProcessRunner</c>
    /// and assert the request shape (executable, args, timeout) without
    /// spawning real netsh. The static facade <see cref="TrySetTunMetric(int, ILogger)"/>
    /// wraps this with a default <see cref="ProcessRunner"/> so production
    /// callers see no behaviour change.
    /// </summary>
    /// <param name="metric">Interface metric (1=highest priority, 0=auto).</param>
    /// <param name="runner">Process runner — real or fake.</param>
    /// <param name="log">Logger.</param>
    /// <param name="interfaceAlias">Adapter name; tests pin this to a known
    /// value to verify the shape, prod uses <see cref="TunInterfaceAlias"/>.</param>
    /// <returns>True iff netsh returned exit code 0. False on timeout, nonzero
    /// exit, or any thrown exception (logged but not surfaced — the caller's
    /// state-tracking flag absorbs the failure as "we didn't change metric").</returns>
    internal static bool TrySetTunMetricViaRunner(
        int metric,
        IProcessRunner runner,
        ILogger log,
        string interfaceAlias)
    {
        if (string.IsNullOrWhiteSpace(interfaceAlias))
        {
            log.Debug("[DnsHardening] netsh metric skipped — empty interface alias");
            return false;
        }

        try
        {
            // ArgumentList-style args (not single string) so we don't have to
            // worry about shell quoting around the alias (which may contain
            // spaces on locales we haven't seen).
            var req = new ProcessRequest(
                ExecutablePath: "netsh.exe",
                Arguments: new[]
                {
                    "interface",
                    "ipv4",
                    "set",
                    "interface",
                    interfaceAlias,
                    $"metric={metric}"
                },
                CaptureStdout: true,
                CaptureStderr: true,
                Timeout: TimeSpan.FromSeconds(5));

            var result = runner.RunAsync(req).GetAwaiter().GetResult();

            if (result.TimedOut)
            {
                log.Debug("[DnsHardening] netsh metric set timed out");
                return false;
            }
            if (result.ExitCode == 0)
            {
                log.Debug("[DnsHardening] Set TUN metric={Metric}", metric);
                return true;
            }
            log.Debug("[DnsHardening] netsh metric set returned {Code}", result.ExitCode);
            return false;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[DnsHardening] Failed to set TUN metric");
            return false;
        }
    }

    /// <summary>
    /// Test override — when non-null, <see cref="TrySetTunMetric(int, ILogger)"/>
    /// uses this runner instead of constructing a real <see cref="ProcessRunner"/>.
    /// Allows the existing static <see cref="Apply"/> / <see cref="Restore"/>
    /// public API to be exercised end-to-end with a fake netsh. Tests MUST
    /// reset this back to <c>null</c> in a try/finally so other tests aren't
    /// poisoned. Not thread-safe — assumes serial xUnit execution within the
    /// fixture (single test class), which matches our existing test pattern.
    /// </summary>
    internal static IProcessRunner? _runnerOverride;

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
