namespace VPNRouter.Core.Interfaces;

public interface IProcessMonitor : IDisposable
{
    event EventHandler<ProcessEventArgs> ProcessStarted;
    event EventHandler<ProcessEventArgs> ProcessStopped;

    void Start();
    void Stop();
}

public class ProcessEventArgs : EventArgs
{
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public int ParentProcessId { get; init; }
}
