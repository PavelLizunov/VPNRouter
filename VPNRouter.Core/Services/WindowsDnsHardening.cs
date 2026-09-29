#if PLATFORM_WINDOWS
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Win32;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static class WindowsDnsHardening
{
    private const string SmhnrPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient";
    private const string SmhnrPolicyValue = "DisableSmartNameResolution";

    private const string ParallelKey = @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters";
    private const string ParallelValue = "DisableParallelAandAAAA";

    private const string TunInterfaceAlias = "VPNRouter-TUN";

    private static readonly string StatePath =
        Path.Combine(AppPaths.DataDir, "dns-hardening-state.json");

    // The lockdown mirrors live tunnel state: armed while the tunnel serves, lifted (fail open) when it stops.
    private static volatile bool _lockdownEffective;

    // Enable, disable and teardown run strictly in call order: unordered background tasks could delete the rules
    // before a slower enable installs them, leaving DNS blocked while the flag says the lockdown is lifted.
    private static readonly object LockdownQueueGate = new();
    private static Task _lockdownQueue = Task.CompletedTask;

    private static Task QueueLockdown(Func<Task> action)
    {
        lock (LockdownQueueGate)
        {
            var next = _lockdownQueue
                .ContinueWith(_ => action(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
                .Unwrap();
            _lockdownQueue = next;
            return next;
        }
    }

    public static void Apply(ILogger? logger = null) => Apply(null, logger);

    public static void Apply(AppSettings? settings, ILogger? logger = null)
    {
        var log = logger ?? Log.Logger;

        try
        {
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

    public static void EnableLockdownIfConfigured(AppSettings? settings, ILogger? logger = null)
    {
        ReconcileLockdownForHealth(tunnelServing: true, settings, logger);
    }

    public static void ReconcileLockdownForHealth(bool tunnelServing, AppSettings? settings, ILogger? logger = null)
    {
        var log = logger ?? Log.Logger;
        bool settingEnabled = settings?.App?.DnsLeakLockdown == true;
        var action = DnsLockdownPolicy.Decide(settingEnabled, tunnelServing, _lockdownEffective);

        switch (action)
        {
            case DnsLockdownAction.Enable:
                _lockdownEffective = true;
                log.Information(
                    "[DnsHardening] DnsLeakLockdown armed — TUN confirmed serving " +
                    "(UDP/53 + TCP/53 + TCP/853 blocked off-tunnel; BR-7/BR-8 background install)");
                var tunCidr = settings?.Tun?.Ipv4Address;
                _ = QueueLockdown(async () =>
                {
                    try { await FirewallManager.EnableDnsLockdownAsync(log, tunCidr); }
                    catch (Exception ex) { log.Warning(ex, "[DnsHardening] Background DNS lockdown install failed (non-fatal)"); }
                });
                break;

            case DnsLockdownAction.Disable:
                _lockdownEffective = false;
                log.Information(
                    "[DnsHardening] DnsLeakLockdown lifted (fail-open) — tunnel not serving; " +
                    "DNS restored so the user keeps internet while the VPN is down / reconnecting");
                _ = QueueLockdown(async () =>
                {
                    try { await FirewallManager.DisableDnsLockdownAsync(log); }
                    catch (Exception ex) { log.Warning(ex, "[DnsHardening] Background DNS lockdown lift failed (non-fatal)"); }
                });
                break;

            case DnsLockdownAction.None:
            default:
                break;
        }
    }

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

                if (state.TunMetricChanged)
                    TrySetTunMetric(0, log);

                try { File.Delete(StatePath); } catch { }
                log.Information("[DnsHardening] Restored to original values");
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[DnsHardening] Restore failed (non-fatal)");
        }

        _lockdownEffective = false;
        var teardown = QueueLockdown(async () =>
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

        // Wait briefly so a normal quit does not leave the firewall rules behind; the ProcessExit sweep is the fallback.
        try { teardown.Wait(TimeSpan.FromSeconds(5)); } catch { }
    }

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

    private static bool TrySetTunMetric(int metric, ILogger log)
        => TrySetTunMetricViaRunner(metric, _runnerOverride ?? new ProcessRunner(), log, TunInterfaceAlias);

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

    internal static IProcessRunner? _runnerOverride;

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
