#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace VPNRouter.Core.Services;

[SupportedOSPlatform("windows")]
internal static class ProcessImagePath
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(
        IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

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

    public static string? ResolveRunningPath(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;
        if (!OperatingSystem.IsWindows()) return null;

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
                catch { continue; }
                var path = TryGetByPid(pid);
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return path;
            }
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (procs != null)
                foreach (var p in procs)
                {
                    try { p.Dispose(); }
                    catch { }
                }
        }
    }

    // The same search where.exe does (the current directory, then every PATH entry, each with the PATHEXT extensions) without starting a process:
    // the firewall asks for ~90 names on every split-tunnel connect and a where.exe each cost 50-300 ms.
    public static string? ResolveNameOnPath(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;
        if (!OperatingSystem.IsWindows()) return null;
        var name = processName.Trim();
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;

        var extensions = Path.HasExtension(name)
            ? new[] { string.Empty }
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var dirs = new List<string> { Environment.CurrentDirectory };
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        foreach (var dir in dirs)
        {
            foreach (var ext in extensions)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim('"'), name + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                }
            }
        }

        return null;
    }

    public static string? ResolveNameToPath(string? processName, IProcessRunner? runner = null)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;
        if (!OperatingSystem.IsWindows()) return null;
        var name = processName.Trim();

        if (runner != null)
        {
            try
            {
                var r = runner.RunAsync(new ProcessRequest(
                    ExecutablePath: "where.exe",
                    Arguments: new[] { name },
                    Timeout: TimeSpan.FromMilliseconds(WhereExeTimeoutMs)))
                    .GetAwaiter().GetResult();
                if (r.TimedOut || r.ExitCode != 0) return null;
                return FirstExistingPath(r.Stdout);
            }
            catch { return null; }
        }

        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo("where.exe", name)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            proc = Process.Start(psi);
            if (proc is null) return null;

            var stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(WhereExeTimeoutMs)) { try { proc.Kill(true); } catch { } return null; }
            proc.StandardError.ReadToEnd();

            return proc.ExitCode == 0 ? FirstExistingPath(stdout) : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { proc?.Dispose(); }
            catch { }
        }
    }

    private const int WhereExeTimeoutMs = 3000;

    private static string? FirstExistingPath(string? whereStdout)
    {
        if (string.IsNullOrEmpty(whereStdout)) return null;
        foreach (var line in whereStdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.Trim();
            if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
        }
        return null;
    }
}
