#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace VPNRouter.Core.Services;

/// <summary>
/// Resolves a process's on-disk image path via the Win32
/// <c>QueryFullProcessImageName</c> API using only the lightweight
/// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> access right.
///
/// <para><b>Why this exists — kill-switch fail-OPEN in session 0 (v2.44.x):</b>
/// <see cref="FirewallManager"/> previously resolved the exe path for a
/// <c>block_on_vpn_fail</c> rule with <c>Process.MainModule.FileName</c>.
/// <c>MainModule</c> walks the <i>target</i> process's user-mode module list
/// (<c>EnumProcessModulesEx</c>), which needs
/// <c>PROCESS_QUERY_INFORMATION | PROCESS_VM_READ</c> and FAILS — returns null
/// or throws <see cref="System.ComponentModel.Win32Exception"/> — when the
/// reader runs in <b>session 0</b> (the Windows Service / a <c>SYSTEM</c>
/// autostart task) and the target runs in a user session, or across a 32/64-bit
/// (WOW64) boundary. The observed effect (live test on windows-brat,
/// 2026-06-27): a <c>SYSTEM</c>-context VPNRouter resolved EVERY routed process
/// to null — even ones genuinely running — so <c>CreateBlockRules</c> created
/// ZERO per-process block rules. On a sing-box crash the kill-switch then failed
/// OPEN (routed apps leaked direct) for exactly the autostart/Service users who
/// rely on it most.</para>
///
/// <para><b>Why QueryFullProcessImageName is the fix:</b> it reads the image
/// path straight from the kernel <c>EPROCESS</c> object (no user-mode module
/// walk), needs only <c>PROCESS_QUERY_LIMITED_INFORMATION</c> (0x1000) — a right
/// <c>SYSTEM</c> always obtains for any process cross-session, and which is
/// immune to the WOW64 bitness mismatch — and is Microsoft's documented
/// replacement for <c>GetModuleFileNameEx</c> in precisely these
/// elevated/cross-session scenarios. It also returns the true filesystem casing,
/// which the case-sensitive sing-box <c>process_name</c> matching wants anyway.</para>
///
/// <para>Handle-safe like its sibling <see cref="ProcessQuery"/>: the native
/// process handle is always closed and any <c>Process[]</c> snapshot is disposed
/// in a <c>finally</c>. Every method swallows failures and returns null rather
/// than throwing — a resolution miss must never crash the VPN start / crash
/// handler (it degrades to the caller's next fallback).</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ProcessImagePath
{
    // The minimal access right that lets even a constrained caller query a
    // process's image name. Crucially obtainable by SYSTEM for a user-session
    // target and unaffected by 32/64-bit reader/target mismatch.
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // ERROR_INSUFFICIENT_BUFFER — QueryFullProcessImageName signals "your buffer
    // was too small" with this; we retry once with a max-path-sized buffer.
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    // dwFlags = 0 -> Win32 path format ("C:\..."), which is what netsh's
    // program=<path> expects. (dwFlags = 1 would return the native
    // \Device\HarddiskVolumeN\ form.)
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(
        IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    /// <summary>
    /// Full filesystem path of the image backing process <paramref name="pid"/>,
    /// or null if the process is gone or the path can't be read. Never throws.
    /// </summary>
    public static string? TryGetByPid(int pid)
    {
        if (pid <= 0) return null;
        if (!OperatingSystem.IsWindows()) return null;

        var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == IntPtr.Zero) return null;
        try
        {
            uint capacity = 1024;
            var sb = new StringBuilder((int)capacity);
            if (QueryFullProcessImageNameW(hProcess, 0, sb, ref capacity))
                return sb.ToString(0, (int)capacity);

            // Pathological long path (\\?\-prefixed, > ~1024 chars): retry once
            // with a 32K buffer (the NT path ceiling) before giving up.
            if (Marshal.GetLastWin32Error() == ERROR_INSUFFICIENT_BUFFER)
            {
                capacity = 32768;
                sb = new StringBuilder((int)capacity);
                if (QueryFullProcessImageNameW(hProcess, 0, sb, ref capacity))
                    return sb.ToString(0, (int)capacity);
            }
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    /// <summary>
    /// Resolve the on-disk path of a currently-running process by base name
    /// (with or without a <c>.exe</c> suffix). Enumerates matching PIDs and
    /// returns the first whose image path resolves to an existing file, or null.
    /// Handle-safe: the <see cref="Process"/>[] snapshot is always disposed.
    ///
    /// <para>Unlike <c>Process.MainModule.FileName</c> this works when the caller
    /// is in session 0 (Service / SYSTEM) and the target is in a user session —
    /// the case that made the kill-switch fail OPEN. See the type remarks.</para>
    /// </summary>
    public static string? ResolveRunningPath(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;
        if (!OperatingSystem.IsWindows()) return null;

        // Process.GetProcessesByName wants the friendly name = file name minus
        // ONLY a trailing ".exe". Strip exactly that — NOT
        // Path.GetFileNameWithoutExtension, which would wrongly truncate at the
        // last dot of a dotted process name (e.g. "My.App" -> "My", so the
        // process would never be found). The kill-switch passes "<App>.exe"
        // names today, but be robust to a bare name too.
        var nameNoExt = processName.Trim();
        if (nameNoExt.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            nameNoExt = nameNoExt[..^4];
        if (string.IsNullOrEmpty(nameNoExt)) return null;

        Process[]? procs = null;
        try
        {
            procs = Process.GetProcessesByName(nameNoExt);
            foreach (var proc in procs)
            {
                int pid;
                try { pid = proc.Id; }
                catch { continue; } // process exited between snapshot and read
                var path = TryGetByPid(pid);
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return path;
            }
            return null;
        }
        catch
        {
            // GetProcessesByName itself can throw on some hardened systems.
            return null;
        }
        finally
        {
            if (procs != null)
                foreach (var p in procs)
                {
                    try { p.Dispose(); }
                    catch { /* defensive — GC finalises if Dispose throws */ }
                }
        }
    }

    /// <summary>
    /// Resolve a process <b>name</b> (e.g. <c>"curl.exe"</c>) to a full on-disk path WITHOUT the
    /// process running, via a <c>where.exe</c> PATH search. Lets the true-split driver register a
    /// not-yet-launched excluded app's image path so its in-kernel process-arrival tracking splits it
    /// the moment it launches. Returns the first <c>where.exe</c> hit that exists on disk, or null.
    /// Handle-safe (the child <see cref="Process"/> is always disposed) and never throws.
    ///
    /// <para><b>Limitation:</b> apps NOT on <c>PATH</c> (e.g. Discord in <c>%LocalAppData%</c>) still
    /// won't resolve pre-launch — that residual (ETW-driven late re-engage) is a documented follow-up
    /// (arch plan §5.4). The post-capture <c>process_name → direct</c> rule covers them meanwhile.</para>
    /// </summary>
    public static string? ResolveNameToPath(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;
        if (!OperatingSystem.IsWindows()) return null;

        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo("where.exe", processName.Trim())
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,   // swallow the "not found" line off the console
                CreateNoWindow = true,
            };
            proc = Process.Start(psi);
            if (proc is null) return null;

            // where.exe emits one path per line; take the first (mirrors FirewallManager.ResolveProcessPath).
            string? firstLine = proc.StandardOutput.ReadLine();
            if (!proc.WaitForExit(3000)) { try { proc.Kill(true); } catch { } return null; }
            proc.StandardError.ReadToEnd();   // drain so a wedged child can't block on a full stderr pipe

            firstLine = firstLine?.Trim();
            if (proc.ExitCode == 0 && !string.IsNullOrEmpty(firstLine) && File.Exists(firstLine))
                return firstLine;
            return null;
        }
        catch
        {
            // where.exe missing / spawn denied / pipe error — degrade to the caller's next fallback.
            return null;
        }
        finally
        {
            try { proc?.Dispose(); }
            catch { /* defensive */ }
        }
    }
}
