using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

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
    /// </summary>
    public static void KillOrphans()
    {
        var selfPid = Environment.ProcessId;

        // 1. Kill any other VPNRouter.App.exe instances (different PID).
        // This prevents the "two instances after update" symptom.
        KillByName("VPNRouter.App", selfPid);

        // 2. Kill any sing-box.exe processes. We're starting fresh —
        // the engine will spawn its own sing-box if needed.
        KillByName("sing-box", null);

        // 3. Kill any leftover VPNRouter.GUI.exe (legacy WinForms or Go stub).
        // Stub should self-exit but defensive in case it's hung.
        KillByName("VPNRouter.GUI", null);
    }

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
