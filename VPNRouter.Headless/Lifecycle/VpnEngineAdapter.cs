#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Headless.Lifecycle;

/// <summary>
/// Production adapter forwarding ILifecycleEngine calls to VPNRouter.Core.Services.VpnEngine.
/// </summary>
public sealed class VpnEngineAdapter : ILifecycleEngine
{
    private readonly VpnEngine _engine;
    private bool _disposed;

    public VpnEngineAdapter(VpnEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _engine.SingBoxStarted += OnSingBoxStarted;
        _engine.Connected += OnConnected;
        _engine.StatusChanged += OnStatusChanged;
        _engine.Warning += OnWarning;
    }

    private void OnSingBoxStarted(int pid) => SingBoxStarted?.Invoke(pid);
    private void OnConnected(int pid) => Connected?.Invoke(pid);
    private void OnStatusChanged(string status) => StatusChanged?.Invoke(status);
    private void OnWarning(string warning) => Warning?.Invoke(warning);

    public bool IsRunning => _engine.IsRunning;
    public string ActiveProfileName => _engine.ActiveProfileName;
    public int? SingBoxPid => _engine.SingBoxPid;
    public string ActiveConfigMode => _engine.ActiveConfigMode;
    public string ActiveRoutingMode => _engine.ActiveRoutingMode;
    public string ActiveServerAddress => _engine.ActiveServerAddress;

    public event Action<int>? SingBoxStarted;
    public event Action<int>? Connected;
    public event Action<string>? StatusChanged;
    public event Action<string>? Warning;

    public Task StartAsync(AppSettings settings, CancellationToken ct)
        => _engine.StartAsync(settings, ct);

    public Task<bool> ApplyAsync(AppSettings settings, CancellationToken ct)
        => _engine.ApplyAsync(settings, ct);

    public void Stop()
        => _engine.Stop();

    public Func<bool>? CaptureReadinessGuard(int pid)
        => _engine.CaptureReadinessGuard(pid);

    public Func<bool>? CapabilityReadinessFunc => null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.SingBoxStarted -= OnSingBoxStarted;
        _engine.Connected -= OnConnected;
        _engine.StatusChanged -= OnStatusChanged;
        _engine.Warning -= OnWarning;
        _engine.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
