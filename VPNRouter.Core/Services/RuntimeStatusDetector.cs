using System.Diagnostics;
using System.Net.NetworkInformation;

namespace VPNRouter.Core.Services;

/// <summary>
/// Runtime status of a VPNRouter background component, for UI display purposes.
/// Not tied to a specific VM/process instance — detects via external signals
/// (running processes, bound ports) so it works whether the component was
/// started by the desktop app, the Windows Service, or the CLI.
/// </summary>
public enum ComponentRuntimeStatus
{
    /// <summary>Not running (neither by us nor anyone else).</summary>
    Idle,

    /// <summary>Detected as running.</summary>
    Running,

    /// <summary>Recently failed (retries exhausted) — set by caller, not detected.</summary>
    Failed
}

/// <summary>
/// Detects whether VPNRouter background components (sing-box, Zapret, TgProxy)
/// are currently running via process enumeration + port probing. Stateless and
/// cheap enough to poll every 1–2 seconds.
/// </summary>
public static class RuntimeStatusDetector
{
    /// <summary>True if any sing-box.exe process is running on this machine.</summary>
    public static bool IsVpnRunning()
        => AnyProcessAlive("sing-box");

    /// <summary>True if any winws.exe process is running (Zapret DPI bypass).</summary>
    public static bool IsZapretRunning()
        => AnyProcessAlive("winws");

    /// <summary>
    /// v2.31.1-r1 (AU-9 fix): <c>Process.GetProcessesByName</c> returns
    /// <c>Process[]</c> where each entry holds a kernel handle. The detector
    /// is polled every 1–2 seconds (see class summary), so without explicit
    /// disposal we leaked one OS handle per <c>Process</c> per poll until GC
    /// finalised the orphaned objects — matching the audit's "+170 handles
    /// per VPN start/stop cycle" symptom. Centralised the disposal here so
    /// any future name-based detector picks it up automatically.
    /// </summary>
    private static bool AnyProcessAlive(string processName)
    {
        Process[]? procs = null;
        try
        {
            procs = Process.GetProcessesByName(processName);
            return procs.Length > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (procs != null)
            {
                foreach (var p in procs)
                {
                    try { p.Dispose(); } catch { /* defensive — GC will mop up */ }
                }
            }
        }
    }

    /// <summary>
    /// True if something is listening on the configured TgProxy port.
    /// Port-based detection is used because TgProxy runs as python.exe which
    /// we can't easily distinguish from other Python processes.
    /// </summary>
    /// <param name="port">Configured TgProxy port from AppSettings.</param>
    public static bool IsTgProxyRunning(int port)
    {
        if (port <= 0) return false;

        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            var listeners = properties.GetActiveTcpListeners();
            foreach (var ep in listeners)
            {
                if (ep.Port == port) return true;
            }
        }
        catch
        {
            // Access denied, feature unsupported, etc. — treat as "unknown, assume idle"
        }

        return false;
    }
}
