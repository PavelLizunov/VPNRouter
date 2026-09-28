#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace VPNRouter.Core.Services;

public interface ISingBoxApi
{
    Task<bool> ReloadConfigAsync(string configPath, CancellationToken ct = default);

    Task<string?> GetVersionAsync(CancellationToken ct = default);

    Task<ConnectionsSnapshot> GetConnectionsAsync(CancellationToken ct = default);

    Task<bool> SelectProxyAsync(string group, string name, CancellationToken ct = default);

    Task<IReadOnlyList<ProxyInfo>> ListProxiesAsync(CancellationToken ct = default);

    Task<int?> GetProxyDelayAsync(string proxyTag, string testUrl, int timeoutMs, CancellationToken ct = default);
}

public sealed record ConnectionsSnapshot(
    int ActiveCount,
    long TotalUploadBytes,
    long TotalDownloadBytes,
    DateTimeOffset CapturedAt)
{
    internal bool IsValid { get; init; } = true;
}

public sealed record ProxyInfo(
    string Name,
    string Type,
    int? DelayMs,
    DateTimeOffset? DelayMeasuredAt);
