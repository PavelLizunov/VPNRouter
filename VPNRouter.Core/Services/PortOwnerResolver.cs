using System.Diagnostics;

namespace VPNRouter.Core.Services;

internal static class PortOwnerResolver
{
    internal static string? TryResolve(int port)
    {
        if (!OperatingSystem.IsWindows() || port <= 0) return null;
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);
            foreach (var line in stdout.Split('\n'))
            {
                if (!line.Contains("LISTENING")) continue;
                if (!line.Contains($":{port} ")) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5 || !int.TryParse(parts[^1], out var pid)) continue;
                try { using var p = Process.GetProcessById(pid); return $"{p.ProcessName} (PID {pid})"; }
                catch { return $"PID {pid}"; }
            }
        }
        catch { }
        return null;
    }
}
