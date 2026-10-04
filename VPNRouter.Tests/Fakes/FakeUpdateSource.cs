#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Services.UpdateSources;

namespace VPNRouter.Tests.Fakes;

public sealed class FakeUpdateSource : IUpdateSource
{
    public string SourceId { get; init; } = "fake";

    public UpdateSourceInfo? CheckResult { get; init; }

    public Exception? CheckException { get; init; }

    public IReadOnlyList<UpdateSourceInfo> StableReleases { get; init; } =
        Array.Empty<UpdateSourceInfo>();

    public Exception? ListStableException { get; init; }

    public IReadOnlyList<UpdateSourceInfo> CandidateReleases { get; init; } =
        Array.Empty<UpdateSourceInfo>();

    public bool? LastIncludePrereleases { get; private set; }

    public int? LastListMaxCount { get; private set; }

    public string DownloadReturnPath { get; init; } = Path.Combine(Path.GetTempPath(), "fake-update-staging");

    public Exception? DownloadException { get; init; }

    public Func<UpdateSourceInfo, Task<string>>? DownloadHandler { get; init; }

    public DownloadProgress[] DownloadProgressEmits { get; init; } = Array.Empty<DownloadProgress>();

    public bool ApplyReturnValue { get; init; } = true;

    public Exception? ApplyException { get; init; }

    public int CheckCallCount { get; private set; }

    public int ListStableCallCount { get; private set; }

    public int DownloadCallCount { get; private set; }

    public int ApplyCallCount { get; private set; }

    public UpdateSourceInfo? LastDownloadInfo { get; private set; }

    public UpdateSourceInfo? LastApplyInfo { get; private set; }

    public string? LastApplyStagedPath { get; private set; }

    public Task<UpdateSourceInfo?> CheckAsync(CancellationToken ct = default)
    {
        CheckCallCount++;
        if (CheckException is not null)
            return Task.FromException<UpdateSourceInfo?>(CheckException);
        return Task.FromResult(CheckResult);
    }

    public Task<IReadOnlyList<UpdateSourceInfo>> ListStableAsync(
        int maxCount,
        CancellationToken ct = default)
    {
        ListStableCallCount++;
        if (ListStableException is not null)
            return Task.FromException<IReadOnlyList<UpdateSourceInfo>>(ListStableException);
        return Task.FromResult(StableReleases);
    }

    public Task<IReadOnlyList<UpdateSourceInfo>> ListOlderAsync(
        int maxCount,
        bool includePrereleases,
        CancellationToken ct = default)
    {
        LastIncludePrereleases = includePrereleases;
        LastListMaxCount = maxCount;
        return includePrereleases ? Task.FromResult(CandidateReleases) : ListStableAsync(maxCount, ct);
    }

    public Task<string> DownloadAsync(
        UpdateSourceInfo info,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        DownloadCallCount++;
        LastDownloadInfo = info;
        if (DownloadHandler is not null)
            return DownloadHandler(info);
        if (DownloadException is not null)
            return Task.FromException<string>(DownloadException);

        if (progress != null)
            foreach (var sample in DownloadProgressEmits)
                progress.Report(sample);

        return Task.FromResult(DownloadReturnPath);
    }

    public Task<bool> ApplyAsync(
        UpdateSourceInfo info,
        string stagedPath,
        CancellationToken ct = default)
    {
        ApplyCallCount++;
        LastApplyInfo = info;
        LastApplyStagedPath = stagedPath;
        if (ApplyException is not null)
            return Task.FromException<bool>(ApplyException);
        return Task.FromResult(ApplyReturnValue);
    }
}
