using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// Defensive cleanup of orphan processes left behind by failed updates,
/// crashes, or v2.3.x→v2.4.x migration where the old process couldn't
/// kill its child sing-box before exiting.
/// </summary>
public static class OrphanCleanup
{
    /// <summary>
    /// Kill any orphan sing-box.exe and other VPNRouter.App.exe instances
    /// that don't belong to the current process. Safe to call on startup
    /// before the VPN engine initializes.
    ///
    /// <para>v2.27.2: optionally emits <see cref="TunAdapterDiagnostics"/>
    /// log lines before and after the sing-box sweep so we can correlate
    /// "orphan sing-box killed" events with the TUN adapter state left
    /// behind. Passive only — no adapter deletes. See <c>plans/vpnrouter-core-stability-audit.md</c>
    /// §B1 for the hypothesis we're gathering data for.</para>
    /// </summary>
    public static void KillOrphans(ILogger? logger = null)
    {
        var selfPid = Environment.ProcessId;

        // Before: capture the adapter inventory so we know what was
        // present at startup. If a user ships us their log after a bad
        // experience, this is the "was there a leak?" data point.
        if (OperatingSystem.IsWindows())
            TunAdapterDiagnostics.LogAdapterState(logger, "OrphanCleanup.before");

        // 1. Kill any other VPNRouter.App.exe instances (different PID).
        // This prevents the "two instances after update" symptom.
        KillByName("VPNRouter.App", selfPid);

        // 2. Kill any sing-box.exe processes. We're starting fresh —
        // the engine will spawn its own sing-box if needed.
        KillByName("sing-box", null);

        // 3. Kill any leftover VPNRouter.GUI.exe (legacy WinForms or Go stub).
        // Stub should self-exit but defensive in case it's hung.
        KillByName("VPNRouter.GUI", null);

        // After: let the log tell us whether sing-box's exit actually
        // dropped its TUN adapter. If adapter rows persist with no
        // sing-box process behind them → confirmed leak pattern.
        if (OperatingSystem.IsWindows())
            TunAdapterDiagnostics.LogAdapterState(logger, "OrphanCleanup.after");
    }

    /// <summary>
    /// Parameterless overload kept for callers that predate the logger
    /// parameter (e.g. Service boot path). Just delegates.
    /// </summary>
    public static void KillOrphans() => KillOrphans(null);

    private static void KillByName(string processName, int? exceptPid)
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (exceptPid.HasValue && proc.Id == exceptPid.Value) continue;
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(2000);
                }
                catch
                {
                    // Process may have exited, or access denied — ignore.
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch
        {
            // GetProcessesByName can throw on permission issues — ignore.
        }
    }
}
