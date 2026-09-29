#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Services.UpdateSources;

namespace VPNRouter.Android;

internal sealed class AndroidInstallerAdapter : IAndroidInstaller
{
    public async Task<string> DownloadApkAsync(
        UpdateSourceInfo info,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(info);

        IProgress<int>? intProgress = null;
        if (progress != null)
        {
            intProgress = new InlineIntProgress(pct =>
            {
                var bytes = info.AssetSize > 0
                    ? info.AssetSize * pct / 100
                    : 0L;
                progress.Report(new DownloadProgress(
                    BytesReceived: bytes,
                    TotalBytes: info.AssetSize > 0 ? info.AssetSize : (long?)null));
            });
        }

        var legacy = new AndroidUpdateInfo
        {
            CurrentVersion = VPNRouter.Core.AppVersion.Version,
            LatestVersion = info.Version,
            DownloadUrl = info.DownloadUrl,
            SizeBytes = info.AssetSize,
            ReleaseNotes = info.ReleaseNotes,
            HtmlUrl = info.ReleaseUrl,
        };

        return await AndroidUpdater
            .DownloadApkAsync(legacy, intProgress, ct)
            .ConfigureAwait(false);
    }

    public Task<bool> BeginInstallAsync(string apkPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apkPath))
            throw new ArgumentException("APK path must be non-empty.", nameof(apkPath));
        return Task.FromResult(AndroidUpdater.BeginInstall(apkPath));
    }

    private sealed class InlineIntProgress : IProgress<int>
    {
        private readonly Action<int> _handler;
        public InlineIntProgress(Action<int> handler) => _handler = handler;
        public void Report(int value) => _handler(value);
    }
}
