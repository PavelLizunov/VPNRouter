using System;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Models;

namespace VPNRouter.Headless.Tests;

/// <summary>
/// Controllable fake router session implementing IRouterSession.
/// Used for hermetic offline testing of RouterBackend and feature endpoints.
/// </summary>
public sealed class FakeRouterSession : IRouterSession
{
    public string State { get; set; } = "disconnected";
    public string? ErrorCode { get; set; }
    public bool CanConnect { get; set; } = true;
    public bool SupportsKillSwitch { get; set; } = false;
    public bool SupportsDnsLockdown { get; set; } = false;
    public bool FailApply { get; set; } = false;

    public event Action? Changed;

    public int ConnectCallCount { get; private set; }
    public int ApplyCallCount { get; private set; }
    public int DisconnectCallCount { get; private set; }
    public AppSettings? LastSettings { get; private set; }

    public Task ConnectAsync(AppSettings settings, CancellationToken ct)
    {
        ConnectCallCount++;
        LastSettings = settings;
        State = "connected";
        ErrorCode = null;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task ApplyAsync(AppSettings settings, CancellationToken ct)
    {
        ApplyCallCount++;
        if (FailApply)
        {
            throw new RouterException("internal_error", "Simulated engine apply failure");
        }

        LastSettings = settings;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken ct)
    {
        DisconnectCallCount++;
        State = "disconnected";
        ErrorCode = null;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public void TriggerChanged(string state, string? errorCode = null)
    {
        State = state;
        ErrorCode = errorCode;
        Changed?.Invoke();
    }

    public ValueTask DisposeAsync()
    {
        State = "unavailable";
        return ValueTask.CompletedTask;
    }
}
