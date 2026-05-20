#if PLATFORM_WINDOWS
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Win32;
using Serilog;
using VPNRouter.Core.Models;

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
    ///
    /// <para>Legacy entry point — kept so callers that don't have access to
    /// <see cref="AppSettings"/> (e.g. crash-recovery cleanup paths) can
    /// still run the registry + TUN-metric portion of hardening without the
    /// Wave 39 firewall lockdown layer. New callers should prefer the
    /// <see cref="Apply(AppSettings, ILogger?)"/> overload so the
    /// <see cref="AppConfig.DnsLeakLockdown"/> toggle is honoured.</para>
    /// </summary>
    public static void Apply(ILogger? logger = null) => Apply(null, logger);

    /// <summary>
    /// Wave 39 (2026-05-19) overload — also installs the firewall-level
    /// DNS-port lockdown when <see cref="AppConfig.DnsLeakLockdown"/> is
    /// true (default for new installs; opt-in for upgrades, see
    /// <see cref="SettingsMigrator.Migrate_4_to_5"/>).
    ///
    /// <para>The firewall portion runs as a fire-and-forget background
    /// task so a slow netsh call doesn't block VPN startup. This mirrors
    /// the pattern used elsewhere in the codebase for non-critical
    /// auxiliary work (e.g. Wave 38a OnProcessExited diagnostics). The
    /// firewall helpers themselves are idempotent and bounded by
    /// per-call + outer 5s timeouts, so a hang is contained.</para>
    /// </summary>
    /// <param name="settings">App settings carrying the
    /// <see cref="AppConfig.DnsLeakLockdown"/> flag. Null means
    /// "skip the Wave 39 firewall layer" — back-compat behaviour for
    /// the legacy <see cref="Apply(ILogger?)"/> path.</param>
    /// <param name="logger">Serilog logger for status/error output.</param>
    public static void Apply(AppSettings? settings, ILogger? logger = null)
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

        // BR-7 (brat 2026-05-20) — lockdown installation moved OUT of
        // Apply. The previous flow installed the firewall lockdown
        // immediately after sing-box started, which on slow-TUN
        // machines (brat's Win11 LTSC took 33 s for wintun to be
        // routable) caused the warm-up HTTP probe to gstatic.com to
        // fail: DNS resolution for gstatic.com was blocked because
        // UDP/53 was banned on Ethernet and the TUN adapter wasn't
        // forwarding DNS to sing-box yet. Result: 33 s window where
        // the user could not browse, panic, rollback. r11 splits the
        // two layers:
        //
        //   * Registry + TUN-metric hardening (above) is immediate.
        //     Safe — these don't break in-flight resolution.
        //
        //   * Firewall lockdown is installed by
        //     <see cref="EnableLockdownIfConfigured"/> from
        //     <see cref="VPNRouter.Core.Services.StartupPipeline"/>'s
        //     warm-up probe success branch, so the lockdown only
        //     fires once TUN is confirmed routing. If warm-up fails,
        //     lockdown never installs — user keeps internet (with a
        //     DNS-leak risk noted in the logs).
    }

    /// <summary>
    /// BR-7 (brat 2026-05-20) — install the Wave 39 firewall-level
    /// DNS-port lockdown. Called from
    /// <see cref="VPNRouter.Core.Services.StartupPipeline"/>'s warm-up
    /// success branch so the lockdown only blocks UDP/53 + TCP/53 +
    /// TCP/853 on non-loopback interfaces AFTER TUN is confirmed
    /// routing. Pre-r11 this lived inside Apply and fired immediately
    /// — which broke the warm-up probe itself on slow-TUN machines.
    ///
    /// <para>No-op when <see cref="AppConfig.DnsLeakLockdown"/> is
    /// false or settings is null. Fire-and-forget background task —
    /// the user-visible Connected state doesn't gate on lockdown
    /// install completing.</para>
    /// </summary>
    public static void EnableLockdownIfConfigured(AppSettings? settings, ILogger? logger = null)
    {
        var log = logger ?? Log.Logger;
        if (settings?.App?.DnsLeakLockdown != true)
        {
            log.Debug("[DnsHardening] DnsLeakLockdown disabled — skipping firewall rule install");
            return;
        }

        log.Information(
            "[DnsHardening] DnsLeakLockdown enabled — installing firewall rules in background " +
            "(BR-7: deferred until TUN warm-up confirmed routing)");
        // BR-8 (brat 2026-05-20) — pass the TUN CIDR so EnableDnsLockdownAsync
        // can add an explicit allow rule for sing-box's TUN DNS endpoint
        // (typically 172.19.0.2:53). Without this, the unscoped block rule
        // banned every UDP/53 outbound including TUN-bound DNS, leaving the
        // user without working DNS once the lockdown installed.
        var tunCidr = settings.Tun?.Ipv4Address;
        _ = Task.Run(async () =>
        {
            try
            {
                await FirewallManager.EnableDnsLockdownAsync(log, tunCidr);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[DnsHardening] Background DNS lockdown install failed (non-fatal)");
            }
        });
    }

    /// <summary>
    /// Restore original DNS settings.
    ///
    /// <para>Wave 39 (2026-05-19) extension: also unconditionally calls
    /// <see cref="FirewallManager.DisableDnsLockdownAsync"/> to tear down
    /// the firewall-level DNS port blocks. The disable is idempotent —
    /// netsh reports "no rules match" with a non-zero exit when the rules
    /// aren't there, which the firewall helper tolerates. We deliberately
    /// don't gate on a state flag because the lockdown is a separate
    /// safety layer; we want it cleaned up on every Stop regardless of
    /// whether Apply enabled it this session (defensive — handles the
    /// edge case where the user disabled the setting between Start and
    /// Stop, or where a crash-recovery Restore is sweeping leftover
    /// state from an earlier process).</para>
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
            }
            else
            {
                RestoreValue(Registry.LocalMachine, SmhnrPolicyKey, SmhnrPolicyValue, state.Smhnr, log);
                RestoreValue(Registry.LocalMachine, ParallelKey, ParallelValue, state.ParallelAAAA, log);

                // Reset TUN metric (only matters if interface still exists, e.g. crash recovery)
                if (state.TunMetricChanged)
                    TrySetTunMetric(0, log); // 0 = automatic

                try { File.Delete(StatePath); } catch { }
                log.Information("[DnsHardening] Restored to original values");
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[DnsHardening] Restore failed (non-fatal)");
        }

        // Wave 39 — always attempt to tear down the firewall-level DNS
        // lockdown. Idempotent; netsh returns non-zero for "no rules match"
        // which the helper logs at Debug and treats as success. Fire-and-
        // forget so a stuck netsh during shutdown doesn't block VpnEngine.Stop
        // (which has its own try/catch wrapper around this call but still
        // wouldn't want to wait on a 5s timeout per call).
        _ = Task.Run(async () =>
        {
            try
            {
                await FirewallManager.DisableDnsLockdownAsync(log);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[DnsHardening] Background DNS lockdown teardown failed (non-fatal)");
            }
        });
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

    // Phase 7 Wave 34 (2026-05-19): retired the local HardeningStateOptions
    // field. Both Save/Load now use the JsonTypeInfo<HardeningState>
    // overload directly against WindowsDnsHardeningJsonContext.Default.
    // Wire format identical (PascalCase keys + WriteIndented matched what
    // the local options pinned; both inherited from the context's
    // [JsonSourceGenerationOptions] in Wave 31b).

    private static void SaveState(HardeningState state)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var json = JsonSerializer.Serialize(state, WindowsDnsHardeningJsonContext.Default.HardeningState);
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
            return JsonSerializer.Deserialize(json, WindowsDnsHardeningJsonContext.Default.HardeningState);
        }
        catch
        {
            return null;
        }
    }

    // ─── State types ──────────────────────────────────────────────────────────

    // Phase 6 — Wave 31b (2026-05-19): visibility flipped private → internal
    // so the sibling JsonSerializerContext below (also Windows-only) can
    // generate JsonTypeInfo for them at compile time. The contract is
    // assembly-private — InternalsVisibleTo VPNRouter.Tests sees them too,
    // which is the desired behaviour (tests can construct + assert state
    // shapes directly). No external caller depends on these types.
    internal sealed class HardeningState
    {
        public SavedRegValue Smhnr { get; set; } = new();
        public SavedRegValue ParallelAAAA { get; set; } = new();
        public bool TunMetricChanged { get; set; }
    }

    internal sealed class SavedRegValue
    {
        public bool HadValue { get; set; }
        public int OldValue { get; set; }
    }
}

// Phase 6 — Wave 31b (2026-05-19): sibling JsonSerializerContext for the
// dns_hardening_state.json sidecar. Windows-only because the entire
// containing class is gated behind PLATFORM_WINDOWS — registering
// HardeningState in the cross-platform VPNRouter.Core.Json.AppJsonContext
// would require either #if-guarded attributes (clumsy) or moving the
// state types out of the Windows-only file (defeats the platform gating).
//
// Same generator options as AppJsonContext (PropertyNameCaseInsensitive,
// WhenWritingNull) so the resolver chain composes uniformly with the
// reflective fallback in HardeningStateOptions.
// Phase 7 Wave 34: WriteIndented=true preserves the human-readable
// dns_hardening_state.json shape that the retired HardeningStateOptions
// field pinned pre-Wave-34.
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true)]
[JsonSerializable(typeof(WindowsDnsHardening.HardeningState))]
[JsonSerializable(typeof(WindowsDnsHardening.SavedRegValue))]
internal sealed partial class WindowsDnsHardeningJsonContext : JsonSerializerContext
{
}
#endif
