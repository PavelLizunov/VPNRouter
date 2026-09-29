#nullable enable
using VPNRouter.Core.Services;

namespace VPNRouter.Tests.Fakes;

public sealed class FakeSingBoxApi : ISingBoxApi
{
    public bool TunnelHealthy { get; set; } = true;

    public bool? ReloadResultOverride { get; set; }

    public string Version { get; set; } = "1.13.10";

    public List<ProxyInfo> Proxies { get; } = new();

    public int ActiveConnectionCount { get; set; }

    public long TotalUploadBytes { get; set; }

    public long TotalDownloadBytes { get; set; }

    public Dictionary<string, string> SelectedByGroup { get; } = new(StringComparer.Ordinal);

    public List<(DateTimeOffset At, string Method, string Detail)> Calls { get; } = new();

    public Exception? FaultToThrow { get; set; }

    public int? ProxyDelayMs { get; set; } = 42;

    public void SimulateCrash() => TunnelHealthy = false;

    public void SimulateRecovery() => TunnelHealthy = true;

    public void SimulateProxyDelay(string name, int delayMs)
    {
        for (int i = 0; i < Proxies.Count; i++)
        {
            if (Proxies[i].Name == name)
            {
                Proxies[i] = Proxies[i] with
                {
                    DelayMs = delayMs,
                    DelayMeasuredAt = DateTimeOffset.UtcNow,
                };
                return;
            }
        }
    }

    public Task<bool> ReloadConfigAsync(string configPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Record("Reload", configPath);
        if (FaultToThrow is not null) throw FaultToThrow;
        return Task.FromResult(ReloadResultOverride ?? TunnelHealthy);
    }

    public Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Record("GetVersion", string.Empty);
        if (FaultToThrow is not null) throw FaultToThrow;
        return Task.FromResult<string?>(TunnelHealthy ? Version : null);
    }

    public Task<ConnectionsSnapshot> GetConnectionsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Record("GetConnections", string.Empty);
        if (FaultToThrow is not null) throw FaultToThrow;

        var snapshot = TunnelHealthy
            ? new ConnectionsSnapshot(ActiveConnectionCount, TotalUploadBytes, TotalDownloadBytes, DateTimeOffset.UtcNow)
            : new ConnectionsSnapshot(0, 0L, 0L, DateTimeOffset.UtcNow);
        return Task.FromResult(snapshot);
    }

    public Task<bool> SelectProxyAsync(string group, string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Record("SelectProxy", $"{group}={name}");
        if (FaultToThrow is not null) throw FaultToThrow;

        if (!TunnelHealthy) return Task.FromResult(false);

        SelectedByGroup[group] = name;
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<ProxyInfo>> ListProxiesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Record("ListProxies", string.Empty);
        if (FaultToThrow is not null) throw FaultToThrow;

        IReadOnlyList<ProxyInfo> copy = Proxies.ToArray();
        return Task.FromResult(copy);
    }

    public Task<int?> GetProxyDelayAsync(string proxyTag, string testUrl, int timeoutMs, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Record("GetProxyDelay", proxyTag);
        if (FaultToThrow is not null) throw FaultToThrow;

        var delay = TunnelHealthy ? ProxyDelayMs : null;
        return Task.FromResult(delay);
    }

    private void Record(string method, string detail)
    {
        Calls.Add((DateTimeOffset.UtcNow, method, detail));
    }
}
