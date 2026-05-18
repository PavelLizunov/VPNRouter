// Phase 4 (Wave 18, 2026-05-18) — IAndroidInstaller adapter for the
// existing static AndroidUpdater helpers. Bridges the Core-side
// SideloadSource contract (DownloadApk → BeginInstall) onto the
// Android.App / Intent.ActionView surface that lives in AndroidUpdater.
//
// SideloadSource lives in Core (compiled net8.0); the adapter has to
// live here so the net8.0-android Android.* references stay contained
// to the Android assembly. Brief:
// plans/phase4-iupdatesource-callers-2026-05-18.md.

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Services.UpdateSources;

namespace VPNRouter.Android;

/// <summary>
/// Production <see cref="IAndroidInstaller"/> impl wrapping the
/// platform-specific static helpers in <see cref="AndroidUpdater"/>.
/// The adapter exists because <see cref="SideloadSource"/> needs a
/// portable interface to drive the APK stream + Intent.ActionView
/// dispatch — it cannot call <c>Android.App.*</c> directly from Core
/// (net8.0), so the Android-side host supplies this adapter when
/// constructing the source.
/// </summary>
internal sealed class AndroidInstallerAdapter : IAndroidInstaller
{
    /// <inheritdoc />
    public async Task<string> DownloadApkAsync(
        UpdateSourceInfo info,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(info);

        // Bridge the byte-level DownloadProgress record onto the
        // int-percent progress sink AndroidUpdater.DownloadApkAsync
        // already uses. Forward declared totals from info.AssetSize so
        // the caller can render a determinate bar even when
        // Content-Length is missing from the response.
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

        // Adapt: AndroidUpdater.DownloadApkAsync expects the legacy
        // AndroidUpdateInfo shape (CurrentVersion/LatestVersion fields
        // that the new contract doesn't use). Synthesize one off the
        // UpdateSourceInfo record so the helper sees the values it
        // needs without changing its public signature.
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

    /// <inheritdoc />
    public Task<bool> BeginInstallAsync(string apkPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apkPath))
            throw new ArgumentException("APK path must be non-empty.", nameof(apkPath));
        return Task.FromResult(AndroidUpdater.BeginInstall(apkPath));
    }

    /// <summary>
    /// Lightweight inline <see cref="IProgress{T}"/> impl that runs the
    /// supplied callback synchronously rather than queuing onto a
    /// SynchronizationContext (the default <see cref="Progress{T}"/>
    /// behaviour). We don't want the per-byte chunk callback to hop
    /// threads here — the upstream IProgress&lt;DownloadProgress&gt;
    /// already handles UI marshalling on the caller side.
    /// </summary>
    private sealed class InlineIntProgress : IProgress<int>
    {
        private readonly Action<int> _handler;
        public InlineIntProgress(Action<int> handler) => _handler = handler;
        public void Report(int value) => _handler(value);
    }
}
