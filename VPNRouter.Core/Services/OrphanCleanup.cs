using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Serilog;

namespace VPNRouter.Core.Services;

public static class OrphanCleanup
{
    public static void KillOrphans(ILogger? logger = null, bool respectTunLock = true)
    {
        if (OperatingSystem.IsWindows())
            TunAdapterDiagnostics.LogAdapterState(logger, "OrphanCleanup.before");

        bool skipSingBoxKill = false;
        if (respectTunLock && TunOwnershipLock.IsOwnedByAnyone())
        {
            skipSingBoxKill = true;
            (logger ?? Log.Logger).Information(
                "[OrphanCleanup] TUN owned by another VPNRouter instance — " +
                "skipping sing-box kill (the running sing-box is not an orphan)");
        }

        if (!skipSingBoxKill)
        {
            KillByName("sing-box", null, killOnly: ProcessOwnership.IsOwnedSingBox);

            KillByName("slipstream-client", null);
        }

        KillByName("VPNRouter.GUI", null);

        if (OperatingSystem.IsWindows())
            TunAdapterDiagnostics.LogAdapterState(logger, "OrphanCleanup.after");
    }

    public static void KillOrphans() => KillOrphans(null);

    private static void KillByName(string processName, int? exceptPid, Func<Process, bool>? killOnly = null)
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (exceptPid.HasValue && proc.Id == exceptPid.Value) continue;
                    if (killOnly != null && !killOnly(proc)) continue;
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(2000);
                }
                catch
                {
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch
        {
        }
    }
}
