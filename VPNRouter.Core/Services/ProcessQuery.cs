#nullable enable
using System;
using System.Diagnostics;

namespace VPNRouter.Core.Services;

public static class ProcessQuery
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _lastKnownAlivePids =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool AnyAlive(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;

        if (string.Equals(processName, "winws", StringComparison.OrdinalIgnoreCase) && !OperatingSystem.IsWindows())
            return false;

        if (_lastKnownAlivePids.TryGetValue(processName, out var lastPid))
        {
            try
            {
                using var p = Process.GetProcessById(lastPid);
                if (!p.HasExited && string.Equals(p.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                _lastKnownAlivePids.TryRemove(processName, out _);
            }
        }

        Process[]? procs = null;
        try
        {
            procs = Process.GetProcessesByName(processName);
            if (procs.Length > 0)
            {
                try { _lastKnownAlivePids[processName] = procs[0].Id; } catch { }
                return true;
            }
            _lastKnownAlivePids.TryRemove(processName, out _);
            return false;
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

    public static bool AnyAlive(params string[]? processNames)
    {
        if (processNames == null) return false;
        foreach (var name in processNames)
            if (AnyAlive(name)) return true;
        return false;
    }

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
            catch { }
        }
    }
}
