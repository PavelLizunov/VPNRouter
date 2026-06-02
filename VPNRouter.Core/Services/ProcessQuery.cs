#nullable enable
using System;
using System.Diagnostics;

namespace VPNRouter.Core.Services;

/// <summary>
/// Handle-safe wrappers around <see cref="Process.GetProcessesByName(string)"/>.
///
/// <para>v2.40.0-r3 (audit Этап 1 — bug-responsiveness-memory map P0):
/// <c>Process.GetProcessesByName(name)</c> returns a <see cref="Process"/>[]
/// where every entry holds a live kernel handle. A bare
/// <c>GetProcessesByName(name).Length &gt; 0</c> leaks one OS handle per process
/// until GC finalises the orphaned objects — on hot polling paths (runtime
/// status every 1–2 s) that was the AU-9 "+170 handles per VPN cycle" leak,
/// fixed centrally in <see cref="RuntimeStatusDetector"/> (v2.31.1) but still
/// open in several Zapret / VM / Public-Configs side paths.</para>
///
/// <para>These helpers centralise the dispose-in-<c>finally</c> so any caller
/// that only needs a boolean or a count never leaks. Prefer them over a raw
/// <c>GetProcessesByName</c>; the pre-commit grep-guard
/// (<c>.githooks/pre-commit</c>) flags new bare <c>.Length</c> uses so the leak
/// can't quietly come back (DoD: no product <c>GetProcessesByName(...).Length</c>
/// without disposing the objects).</para>
/// </summary>
public static class ProcessQuery
{
    /// <summary>
    /// True if at least one process with the given base name (no <c>.exe</c>) is
    /// running. Disposes every returned <see cref="Process"/> handle. Enumeration
    /// errors (permission / unsupported platform) are swallowed as "not running",
    /// matching the prior call-site behaviour.
    /// </summary>
    public static bool AnyAlive(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
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
            DisposeAll(procs);
        }
    }

    /// <summary>
    /// True if ANY of the given base names has a running process. Short-circuits
    /// on the first match; each probe is individually handle-safe.
    /// </summary>
    public static bool AnyAlive(params string[]? processNames)
    {
        if (processNames == null) return false;
        foreach (var name in processNames)
            if (AnyAlive(name)) return true;
        return false;
    }

    /// <summary>
    /// Number of running processes with the given base name (handle-safe).
    /// Returns 0 on enumeration error.
    /// </summary>
    public static int CountAlive(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return 0;
        Process[]? procs = null;
        try
        {
            procs = Process.GetProcessesByName(processName);
            return procs.Length;
        }
        catch
        {
            return 0;
        }
        finally
        {
            DisposeAll(procs);
        }
    }

    private static void DisposeAll(Process[]? procs)
    {
        if (procs == null) return;
        foreach (var p in procs)
        {
            try { p.Dispose(); }
            catch { /* defensive — GC will finalise if Dispose throws */ }
        }
    }
}
