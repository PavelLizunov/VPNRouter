using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace VPNRouter.Core.Services;

public static class TunAdapterDiagnostics
{
    private const int PnpRemovalPollIntervalMs = 300;
    private const int PnpRemovalMaxPolls = 40;
    private const int PnpRemovalAbsentSamples = 3;
    private const int PnpRemovalQuietPeriodMs = 2_000;
    private const int PnpRemovalBudgetMs = 12_000;

    internal static IProcessRunner Runner { get; set; } = new ProcessRunner();

    internal static Func<TimeSpan, CancellationToken, Task> RemovalDelayAsync { get; set; } =
        static (delay, ct) => Task.Delay(delay, ct);

    // Windows 10 before build 19041 (LTSC 2019) lacks pnputil /remove-device: use SetupAPI there.
    internal static Func<bool> RequiresNativePnpApi { get; set; } =
        static () => RequiresNativePnpForWindowsBuild(Environment.OSVersion.Version.Build);
    internal static bool RequiresNativePnpForWindowsBuild(int build) => build < 19041;
    internal static Func<string, NativePnpRemovalResult> RemoveNativePnpDevice { get; set; } =
        WindowsPnpDeviceManager.RemoveDevice;
    internal static Func<string, NativePnpPresenceResult> QueryNativePnpPresence { get; set; } =
        WindowsPnpDeviceManager.QueryPresence;
    internal static Func<string, NativePnpLookupResult> ResolveNativePnpDeviceIds { get; set; } =
        WindowsPnpDeviceManager.FindNetworkAdapterInstanceIds;

    [SupportedOSPlatform("windows")]
    public static void LogAdapterState(ILogger? logger, string context)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            var psiResult = Runner.RunAsync(new ProcessRequest(
                ExecutablePath: "netsh",
                Arguments: new[] { "interface", "show", "interface" },
                Timeout: TimeSpan.FromMilliseconds(3000))).GetAwaiter().GetResult();

            if (psiResult.TimedOut) return;
            var output = psiResult.Stdout;

            var hits = output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(l =>
                    l.IndexOf("VPNRouter-TUN", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    l.IndexOf("sing-box-tun", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            if (hits.Count == 0)
            {
                logger?.Information("[TunDiag] {Ctx}: no VPNRouter-TUN or sing-box-tun adapters found", context);
                return;
            }

            logger?.Information(
                "[TunDiag] {Ctx}: found {Count} TUN adapter row(s) in netsh:",
                context, hits.Count);
            foreach (var line in hits)
            {
                logger?.Information("[TunDiag]   {Line}", line.Trim());
            }
        }
        catch (Exception ex)
        {
            logger?.Debug(ex, "[TunDiag] {Ctx}: inventory query failed (non-fatal)", context);
        }
    }

    [SupportedOSPlatform("windows")]
    public static void DisableOrphanedAdapter(ILogger? logger, string interfaceName, string context)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (string.IsNullOrWhiteSpace(interfaceName)) return;

        try
        {
            var psiResult = Runner.RunAsync(new ProcessRequest(
                ExecutablePath: "netsh",
                Arguments: new[]
                {
                    "interface", "set", "interface",
                    $"name={interfaceName}",
                    "admin=disabled",
                },
                Timeout: TimeSpan.FromMilliseconds(3000))).GetAwaiter().GetResult();

            if (psiResult.TimedOut)
            {
                logger?.Warning(
                    "[TunDiag] {Ctx}: netsh disable for '{Iface}' timed out after 3s",
                    context, interfaceName);
                return;
            }

            var stdout = psiResult.Stdout;
            var stderr = psiResult.Stderr;
            var exitCode = psiResult.ExitCode;

            if (exitCode == 0)
            {
                logger?.Information(
                    "[TunDiag] {Ctx}: disabled orphaned adapter '{Iface}' (network stack should release routes)",
                    context, interfaceName);
            }
            else if (exitCode == 1
                     || stdout.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0
                     || stderr.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                logger?.Debug(
                    "[TunDiag] {Ctx}: adapter '{Iface}' already gone — nothing to clean up",
                    context, interfaceName);
            }
            else
            {
                logger?.Warning(
                    "[TunDiag] {Ctx}: netsh disable for '{Iface}' returned exit {Code}: stdout='{Out}' stderr='{Err}'",
                    context, interfaceName, exitCode, stdout.Trim(), stderr.Trim());
            }
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[TunDiag] {Ctx}: disable orphaned adapter '{Iface}' failed (non-fatal)", context, interfaceName);
        }
    }

    [SupportedOSPlatform("windows")]
    public static async Task<int> PreStartCleanupAsync(
        ILogger? logger,
        string context = "VpnEngine.PreStart")
    {
        if (!OperatingSystem.IsWindows()) return 0;

        int removed = 0;
        var enumerationFoundDefault = false;

        try
        {
            var (_, netshOut, _) = await RunAndCaptureAsync(
                "netsh", new[] { "interface", "show", "interface" },
                timeoutMs: 5000, logger: logger);

            var staleAdapters = ExtractStaleAdapterNames(netshOut);
            if (staleAdapters.Count == 0)
            {
                logger?.Information(
                    "[TunDiag] {Ctx}: pre-start cleanup: no stale TUN adapters found via netsh enumeration",
                    context);
            }
            else
            {
                logger?.Information(
                    "[TunDiag] {Ctx}: pre-start cleanup: found {Count} stale TUN adapter(s) via enumeration: {Names}",
                    context, staleAdapters.Count, string.Join(", ", staleAdapters));

                foreach (var adapter in staleAdapters)
                {
                    if (string.Equals(adapter, DefaultTunInterfaceName, StringComparison.OrdinalIgnoreCase))
                        enumerationFoundDefault = true;

                    if (await TryRemoveAdapterAsync(
                            logger, adapter, context, requireInstanceId: true))
                        removed++;
                    else
                        throw new TunAdapterNotReadyException(
                            $"Could not remove stale VPNRouter TUN adapter '{adapter}'.");
                }
            }

            if (!enumerationFoundDefault)
            {
                logger?.Debug(
                    "[TunDiag] {Ctx}: pre-start cleanup: direct-by-name fallback for '{Iface}' (enumeration didn't list it)",
                    context, DefaultTunInterfaceName);
                if (await TryRemoveAdapterAsync(logger, DefaultTunInterfaceName, context))
                    removed++;
                else
                    throw new TunAdapterNotReadyException(
                        $"Could not verify removal of '{DefaultTunInterfaceName}'.");
            }

            logger?.Information(
                "[TunDiag] {Ctx}: pre-start cleanup: removed {Removed} TUN adapter(s) total (enumeration + direct fallback)",
                context, removed);

            return removed;
        }
        catch (TunAdapterNotReadyException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[TunDiag] {Ctx}: pre-start cleanup diagnostics failed (continuing)", context);
            return removed;
        }
    }

    private const string DefaultTunInterfaceName = "VPNRouter-TUN";

    internal static List<string> ExtractStaleAdapterNames(string netshOutput)
    {
        if (string.IsNullOrEmpty(netshOutput)) return new List<string>();

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var ownedNamePattern = new Regex(
            @"^(VPNRouter-TUN|sing-box-tun(?:-[A-Za-z0-9_-]+)?)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        foreach (var rawLine in netshOutput.Split(new[] { '\r', '\n' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var columns = Regex.Split(line, @"\s{2,}");
            if (columns.Length != 4) continue;

            var match = ownedNamePattern.Match(columns[3].Trim());
            if (match.Success)
                names.Add(match.Groups[1].Value);
        }

        return names.ToList();
    }

    private static int s_removeNetAdapterMissing;

    private static Lazy<bool> s_netAdapterModuleAvailable =
        new Lazy<bool>(ProbeNetAdapterModuleAvailable, LazyThreadSafetyMode.ExecutionAndPublication);

    private static bool ProbeNetAdapterModuleAvailable()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            var result = Runner.RunAsync(new ProcessRequest(
                ExecutablePath: "powershell.exe",
                Arguments: new[]
                {
                    "-NoProfile", "-NonInteractive", "-Command",
                    "Import-Module NetAdapter -ErrorAction SilentlyContinue; " +
                    "if (Get-Command Get-NetAdapter -ErrorAction SilentlyContinue) { 1 } else { 0 }",
                },
                Timeout: TimeSpan.FromMilliseconds(5000))).GetAwaiter().GetResult();

            if (result.TimedOut) return false;
            if (result.ExitCode != 0) return false;

            var trimmed = (result.Stdout ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(trimmed)) return false;

            return int.TryParse(trimmed, out var count) && count >= 1;
        }
        catch
        {
            return false;
        }
    }

    private static int s_actionableModuleMissingLogged;

    internal static void ResetRemoveNetAdapterLatchForTests()
    {
        Volatile.Write(ref s_removeNetAdapterMissing, 0);
        Volatile.Write(ref s_actionableModuleMissingLogged, 0);
        s_netAdapterModuleAvailable = new Lazy<bool>(
            ProbeNetAdapterModuleAvailable,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal static void SetNetAdapterModuleAvailableForTests(bool available)
    {
        s_netAdapterModuleAvailable = new Lazy<bool>(
            () => available, LazyThreadSafetyMode.ExecutionAndPublication);
        _ = s_netAdapterModuleAvailable.Value;
    }

    internal static void SetNetAdapterModuleProbeForTests(Func<bool> probe)
    {
        s_netAdapterModuleAvailable = new Lazy<bool>(
            probe, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    [SupportedOSPlatform("windows")]
    internal static async Task<bool> TryDisableAdapterViaNetshAsync(
        ILogger? logger, string adapterName, string context)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (string.IsNullOrWhiteSpace(adapterName)) return false;

        try
        {
            var (exitCode, stdout, stderr) = await RunAndCaptureAsync(
                "netsh",
                new[]
                {
                    "interface", "set", "interface",
                    $"name={adapterName}",
                    "admin=disabled",
                },
                timeoutMs: 3000, logger: logger);

            if (exitCode == 0)
            {
                logger?.Information(
                    "[TunDiag] {Ctx}: netsh-disabled orphaned adapter '{Name}' (kernel handle released; exact removal follows at launch gate)",
                    context, adapterName);
                return true;
            }

            if (exitCode == 1
                || (stdout ?? "").IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0
                || (stderr ?? "").IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                logger?.Debug(
                    "[TunDiag] {Ctx}: netsh-disable for '{Name}' reported not-found (already gone — counts as success)",
                    context, adapterName);
                return true;
            }

            logger?.Warning(
                "[TunDiag] {Ctx}: netsh-disable for '{Name}' returned exit {Exit}: stdout='{Out}' stderr='{Err}'",
                context, adapterName, exitCode, (stdout ?? "").Trim(), (stderr ?? "").Trim());
            return false;
        }
        catch (Exception ex)
        {
            logger?.Warning(ex,
                "[TunDiag] {Ctx}: netsh-disable for '{Name}' threw (non-fatal)",
                context, adapterName);
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    internal static async Task<bool> TryRemoveAdapterAsync(
        ILogger? logger, string adapterName, string context,
        bool requireInstanceId = false)
    {
        var useNativePnpApi = RequiresNativePnpApi();
        var useNetAdapterModule = !useNativePnpApi && s_netAdapterModuleAvailable.Value;
        try
        {
            List<string> instanceIds;
            if (!useNetAdapterModule)
            {
                if (Interlocked.CompareExchange(ref s_actionableModuleMissingLogged, 1, 0) == 0)
                {
                    logger?.Information(
                        "[TunDiag] {Ctx}: Get-NetAdapter unavailable; resolving the exact " +
                        "TUN PnP InstanceId through Windows Network Connections.",
                        context);
                }

                var lookup = ResolveNativePnpDeviceIds(adapterName);
                if (!lookup.Success)
                {
                    logger?.Warning(
                        "[TunDiag] {Ctx}: native PnP InstanceId lookup for '{Name}' failed: {Error}",
                        context, adapterName, lookup.Error ?? "unknown error");
                    return false;
                }

                instanceIds = lookup.InstanceIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            else
            {
                if (Volatile.Read(ref s_removeNetAdapterMissing) == 1)
                    return false;

                var resolveScript =
                    $"Get-NetAdapter -Name '{adapterName}' -ErrorAction SilentlyContinue | " +
                    "Select-Object -ExpandProperty PnPDeviceID";
                var (rExit, rOut, rErr) = await RunAndCaptureAsync(
                    "powershell.exe",
                    new[] { "-NoProfile", "-NonInteractive", "-Command", resolveScript },
                    timeoutMs: 10000, logger: logger);

                var rErrText = rErr ?? string.Empty;
                if (rErrText.IndexOf("CommandNotFoundException", StringComparison.OrdinalIgnoreCase) >= 0
                    || rErrText.IndexOf("is not recognized", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Interlocked.Exchange(ref s_removeNetAdapterMissing, 1);
                    logger?.Warning(
                        "[TunDiag] {Ctx}: Get-NetAdapter cannot resolve the PnP InstanceId " +
                        "for '{Name}'.",
                        context, adapterName);
                    return false;
                }

                if (rExit != 0)
                {
                    logger?.Debug(
                        "[TunDiag] {Ctx}: Get-NetAdapter could not resolve '{Name}' (exit {Exit}); " +
                        "checking Windows Network Connections: '{Err}'",
                        context, adapterName, rExit, rErrText.Trim());

                    var lookup = ResolveNativePnpDeviceIds(adapterName);
                    if (!lookup.Success)
                    {
                        logger?.Warning(
                            "[TunDiag] {Ctx}: fallback PnP InstanceId lookup for '{Name}' failed: {Error}",
                            context, adapterName, lookup.Error ?? "unknown error");
                        return false;
                    }

                    instanceIds = lookup.InstanceIds
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Select(id => id.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                else
                {
                    instanceIds = (rOut ?? string.Empty)
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .Where(s => s.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
            }

            if (instanceIds.Count == 0)
            {
                logger?.Debug(
                    "[TunDiag] {Ctx}: adapter '{Name}' not present (no InstanceId) — nothing to remove",
                    context, adapterName);
                return !requireInstanceId;
            }

            DisableOrphanedAdapter(logger, adapterName, context);

            foreach (var id in instanceIds)
            {
                var removed = useNativePnpApi
                    ? await RunNativePnpRemoveAsync(logger, id, adapterName, context)
                    : await RunPnpUtilRemoveAsync(logger, id, adapterName, context);
                if (!removed)
                {
                    throw new TunAdapterNotReadyException(
                        $"Windows could not remove '{adapterName}' ({id}).",
                        id);
                }
            }
            return true;
        }
        catch (TunAdapterNotReadyException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.Warning(ex,
                "[TunDiag] {Ctx}: pnputil removal for '{Name}' threw (non-fatal)",
                context, adapterName);
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    internal static Task WaitForExactPnpRemovalSettledAsync(
        ILogger? logger, string instanceId, string adapterName, string context) =>
        WaitForNativePnpRemovalSettledAsync(logger, instanceId, adapterName, context);

    [SupportedOSPlatform("windows")]
    private static async Task<bool> RunNativePnpRemoveAsync(
        ILogger? logger, string instanceId, string adapterName, string context)
    {
        var result = RemoveNativePnpDevice(instanceId);
        if (!result.Success || result.RestartRequired)
        {
            logger?.Warning(
                "[TunDiag] {Ctx}: native PnP removal for '{Name}' ({Id}) failed: " +
                "error={Error}, restartRequired={RestartRequired}",
                context, adapterName, instanceId, result.ErrorCode, result.RestartRequired);
            return false;
        }

        logger?.Information(
            "[TunDiag] {Ctx}: removed stale adapter '{Name}' through Windows SetupAPI ({Id})",
            context, adapterName, instanceId);
        await WaitForNativePnpRemovalSettledAsync(logger, instanceId, adapterName, context)
            .ConfigureAwait(false);
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static async Task WaitForNativePnpRemovalSettledAsync(
        ILogger? logger, string instanceId, string adapterName, string context)
    {
        var absentSamples = 0;
        var elapsed = Stopwatch.StartNew();
        for (var poll = 0; poll < PnpRemovalMaxPolls; poll++)
        {
            if (elapsed.ElapsedMilliseconds >= PnpRemovalBudgetMs)
                break;

            var presence = QueryNativePnpPresence(instanceId);
            if (presence.Presence == NativePnpPresence.Error)
            {
                throw new TunAdapterNotReadyException(
                    $"Windows could not query '{adapterName}' ({instanceId}); ConfigMgr result 0x{presence.ConfigManagerResult:X8}.",
                    instanceId);
            }

            if (presence.Presence == NativePnpPresence.Absent)
            {
                absentSamples++;
                if (absentSamples >= PnpRemovalAbsentSamples)
                {
                    await RemovalDelayAsync(
                            TimeSpan.FromMilliseconds(PnpRemovalQuietPeriodMs),
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    var finalPresence = QueryNativePnpPresence(instanceId);
                    if (finalPresence.Presence == NativePnpPresence.Absent)
                    {
                        logger?.Information(
                            "[TunDiag] {Ctx}: native PnP removal settled for '{Name}' ({Id})",
                            context, adapterName, instanceId);
                        return;
                    }

                    if (finalPresence.Presence == NativePnpPresence.Error)
                    {
                        throw new TunAdapterNotReadyException(
                            $"Windows could not verify removal of '{adapterName}' ({instanceId}); ConfigMgr result 0x{finalPresence.ConfigManagerResult:X8}.",
                            instanceId);
                    }

                    absentSamples = 0;
                }
            }
            else
            {
                absentSamples = 0;
            }

            await RemovalDelayAsync(
                    TimeSpan.FromMilliseconds(PnpRemovalPollIntervalMs),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        throw new TunAdapterNotReadyException(
            $"Windows did not finish removing '{adapterName}' ({instanceId}) before the bounded native PnP settle gate expired.",
            instanceId);
    }

    [SupportedOSPlatform("windows")]
    private static async Task<bool> RunPnpUtilRemoveAsync(
        ILogger? logger, string instanceId, string adapterName, string context)
    {
        var (exit, _, err) = await RunAndCaptureAsync(
            "pnputil.exe",
            new[] { "/remove-device", instanceId },
            timeoutMs: 10000, logger: logger);

        if (exit == 0)
        {
            logger?.Information(
                "[TunDiag] {Ctx}: removed stale adapter '{Name}' device record via pnputil ({Id})",
                context, adapterName, instanceId);
            await WaitForNativePnpRemovalSettledAsync(logger, instanceId, adapterName, context)
                .ConfigureAwait(false);
            return true;
        }

        logger?.Information(
            "[TunDiag] {Ctx}: pnputil /remove-device for '{Name}' ({Id}) failed " +
            "with exit {Exit}; retrying through Windows SetupAPI: '{Err}'",
            context, adapterName, instanceId, exit, (err ?? string.Empty).Trim());
        return await RunNativePnpRemoveAsync(logger, instanceId, adapterName, context)
            .ConfigureAwait(false);
    }

    private static async Task<(int exitCode, string stdout, string stderr)> RunAndCaptureAsync(
        string fileName, IReadOnlyList<string> arguments, int timeoutMs, ILogger? logger)
    {
        try
        {
            var result = await Runner.RunAsync(new ProcessRequest(
                ExecutablePath: fileName,
                Arguments: arguments,
                Timeout: TimeSpan.FromMilliseconds(timeoutMs))).ConfigureAwait(false);

            if (result.TimedOut)
            {
                return (-1, result.Stdout, result.Stderr);
            }

            return (result.ExitCode, result.Stdout, result.Stderr);
        }
        catch (Exception ex)
        {
            logger?.Debug(ex, "[TunDiag] command '{Cmd} {Args}' threw", fileName, string.Join(' ', arguments));
            return (-1, string.Empty, string.Empty);
        }
    }
}

internal sealed class TunAdapterNotReadyException : Exception
{
    public TunAdapterNotReadyException(string message, string? instanceId = null) : base(message)
    {
        InstanceId = instanceId;
    }

    public string? InstanceId { get; }
}
