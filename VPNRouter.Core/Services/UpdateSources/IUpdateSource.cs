#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VPNRouter.Core.Services.UpdateSources;

public interface IUpdateSource
{
    Task<UpdateSourceInfo?> CheckAsync(CancellationToken ct = default);

    Task<IReadOnlyList<UpdateSourceInfo>> ListStableAsync(
        int maxCount,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UpdateSourceInfo>>(Array.Empty<UpdateSourceInfo>());

    Task<string> DownloadAsync(
        UpdateSourceInfo info,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default);

    Task<bool> ApplyAsync(
        UpdateSourceInfo info,
        string stagedPath,
        CancellationToken ct = default);

    string SourceId { get; }
}

public sealed record UpdateSourceInfo(
    string Version,
    string ReleaseUrl,
    string AssetName,
    string DownloadUrl,
    long AssetSize,
    string? AssetSha256,
    bool IsPrerelease,
    string ReleaseNotes);

public sealed record DownloadProgress(long BytesReceived, long? TotalBytes)
{
    public int? Percent => TotalBytes is > 0 ? (int)(BytesReceived * 100 / TotalBytes.Value) : null;
}
