#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace VPNRouter.Core.Services;

public interface ISingBoxApi
{
    /// <summary>
    /// Legacy Clash <c>PUT /configs</c> reload. sing-box 1.14.4 answers 204 without
    /// applying the file, so implementations return <c>false</c> and callers must
    /// restart the process with the new configuration instead.
    /// </summary>
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
