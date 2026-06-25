using System.Diagnostics;
using System.Net.Http;
using System.Text;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public partial class SingBoxManager
{
    public bool IsRunning()
    {
        // v2.21.5: on Unix (macOS + Linux) the Clash API is the authoritative
        // signal. Previously we short-circuited on State != Running, which
        // forced false when:
        //   • The app was restarted and a sing-box from a previous session
        //     is still alive (no process tracked by this VM instance).
        //   • sing-box was started by the Windows Service / external
        //     autostart path and our local _process reference was never
        //     populated.
        //   • Linux pkexec wrapper exited after spawning the root child —
        //     _process.HasExited=true even though sing-box is alive.
        // In all three cases Clash API still answers if the tunnel is up,
        // which is what the UI actually cares about. Drop the State gate
        // on Unix and trust the HTTP probe.
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            return IsClashApiAlive();

        if (State != SingBoxState.Running) return false;
        return _handle?.HasExited == false;
    }

    public bool IsHealthy()
    {
        if (OperatingSystem.IsMacOS())
            return State == SingBoxState.Running && IsClashApiAlive();

        if (_handle == null || _handle.HasExited)
            return false;

        // Phase 3+ (2026-05-21): metric introspection via the IProcessHandle
        // snapshot — the seam refreshes the underlying Process internally.
        var snapshot = _handle.TryGetSnapshot();
        if (snapshot == null)
            return false;

        var memoryMb = snapshot.WorkingSetBytes / 1024 / 1024;
        if (memoryMb > 500)
            _logger.Warning("[SingBoxManager] sing-box memory usage: {Mem}MB (threshold: 500MB)", memoryMb);

        return true;
    }

    public ProcessMetrics GetMetrics()
    {
        if (_handle == null || _handle.HasExited)
            return new ProcessMetrics();

        var snapshot = _handle.TryGetSnapshot();
        if (snapshot == null)
            return new ProcessMetrics();

        return new ProcessMetrics
        {
            MemoryMb = snapshot.WorkingSetBytes / 1024 / 1024,
            CpuTime = snapshot.TotalProcessorTime,
            StartTime = snapshot.StartTime
        };
    }
}
