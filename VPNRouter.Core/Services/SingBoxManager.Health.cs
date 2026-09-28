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
