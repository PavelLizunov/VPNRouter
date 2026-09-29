#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests.Fakes;

internal sealed class FakeSplitTunnelDriver : ISplitTunnelDriver
{
    public int EngageCount { get; private set; }
    public int DisengageCount { get; private set; }
    public int DisposeCount { get; private set; }
    public SplitTunnelEngageRequest? LastRequest { get; private set; }

    public bool EngageResult { get; set; } = true;
    public bool IsAvailable { get; set; } = true;
    public string? LastFailureReason { get; set; }

    public bool IsEngaged { get; private set; }
    public bool IsPumpHealthy => true;
    public event Action<bool>? EngagedChanged;

    public Task<bool> EngageAsync(SplitTunnelEngageRequest request, CancellationToken ct)
    {
        EngageCount++;
        LastRequest = request;
        IsEngaged = EngageResult;
        EngagedChanged?.Invoke(IsEngaged);
        return Task.FromResult(EngageResult);
    }

    public Task DisengageAsync(CancellationToken ct)
    {
        DisengageCount++;
        IsEngaged = false;
        EngagedChanged?.Invoke(false);
        return Task.CompletedTask;
    }

    public int SweepCount { get; private set; }
    public Task SweepStaleStateAsync(CancellationToken ct)
    {
        SweepCount++;
        return Task.CompletedTask;
    }

    public void Dispose() => DisposeCount++;
}
