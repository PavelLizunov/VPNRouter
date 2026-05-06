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
    /// Kill orphan sing-box.exe + VPNRouter.GUI.exe stubs. Safe to call
    /// on startup before the VPN engine initializes.
    ///
    /// <para>v2.31.10-r1: VPNRouter.App.exe siblings are NO LONGER killed
    /// here — that responsibility moved to <c>SingleInstance</c> which
    /// correctly leaves the original alive and exits the new launch
    /// silently. See F-4 in plans/session-night-shift-2026-05-06.md.</para>
    ///
    /// <para>v2.27.2: optionally emits <see cref="TunAdapterDiagnostics"/>
    /// log lines before and after the sing-box sweep so we can correlate
    /// "orphan sing-box killed" events with the TUN adapter state left
    /// behind. Passive only — no adapter deletes. See <c>plans/vpnrouter-core-stability-audit.md</c>
    /// §B1 for the hypothesis we're gathering data for.</para>
    /// </summary>
    public static void KillOrphans(ILogger? logger = null)
    {
        // Before: capture the adapter inventory so we know what was
        // present at startup. If a user ships us their log after a bad
        // experience, this is the "was there a leak?" data point.
        if (OperatingSystem.IsWindows())
            TunAdapterDiagnostics.LogAdapterState(logger, "OrphanCleanup.before");

        // v2.31.10-r1 (F-4): we used to KillByName("VPNRouter.App",
        // selfPid) here as a defensive belt against "two instances after
        // update". That predates SingleInstance (v2.31.7-r2). With
        // SingleInstance in place, this kill is at best redundant and
        // at worst a footgun — when the SingleInstance check has any
        // race or bug (as in the v2.31.7..v2.31.9 mutex-not-owned bug),
        // OrphanCleanup gleefully kills the ORIGINAL instance and
        // leaves the brand-new one as sole survivor. The OPPOSITE of
        // what SingleInstance is supposed to guarantee. SingleInstance
        // is the correct gate; killing live VPNRouter.App siblings here
        // is never desirable. Removed.

        // 1. Kill any sing-box.exe processes. We're starting fresh —
        // the engine will spawn its own sing-box if needed.
        KillByName("sing-box", null);

        // 2. Kill any leftover VPNRouter.GUI.exe (legacy WinForms or Go stub).
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
