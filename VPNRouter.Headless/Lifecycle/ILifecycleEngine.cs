#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Models;

namespace VPNRouter.Headless.Lifecycle;

/// <summary>
/// Core engine abstraction decoupling RouterSession from concrete VpnEngine.
/// Enables deterministic lifecycle unit testing without live network or external processes.
/// </summary>
public interface ILifecycleEngine : IAsyncDisposable, IDisposable
{
    bool IsRunning { get; }
    string ActiveProfileName { get; }
    int? SingBoxPid { get; }
    string ActiveConfigMode { get; }
    string ActiveRoutingMode { get; }
    string ActiveServerAddress { get; }

    event Action<int>? SingBoxStarted;
    event Action<int>? Connected;
    event Action<string>? StatusChanged;
    event Action<string>? Warning;

    Task StartAsync(AppSettings settings, CancellationToken ct);
    Task<bool> ApplyAsync(AppSettings settings, CancellationToken ct);
    void Stop();

    /// <summary>
    /// Captures the engine-scoped readiness guard for the given PID.
    /// Evaluated upon receiving the Connected event to ensure typed readiness.
    /// </summary>
    Func<bool>? CaptureReadinessGuard(int pid);

    /// <summary>
    /// Optional capability readiness probe. When null, production real-checks sing-box binary on disk.
    /// Injected for fake engines to simulate capability readiness without requiring real binary on disk.
    /// </summary>
    Func<bool>? CapabilityReadinessFunc { get; }
}
