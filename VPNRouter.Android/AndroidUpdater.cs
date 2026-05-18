using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using AndroidX.Core.Content;
using VPNRouter.Core;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.UpdateSources;

namespace VPNRouter.Android;

/// <summary>
/// v2.32.0 (2026-05-07) — sideload support for the Android port. Hosts
/// the platform-specific helpers that the cross-platform
/// <see cref="SideloadSource"/> + <c>AndroidInstallerAdapter</c> can't
/// own themselves (Android.App / Intent.ActionView / FileProvider /
/// PackageManager APIs are Mono-Android-only).
///
/// <para><b>What's here today:</b></para>
/// <list type="bullet">
///   <item><see cref="DownloadApkAsync"/> — streams a single APK into
///         <c>getCacheDir()/update.apk</c> with progress reporting.</item>
///   <item><see cref="BeginInstall"/> — hands the APK to the system
///         PackageInstaller via <see cref="Intent.ActionView"/>.</item>
///   <item><see cref="CanRequestInstall"/> /
///         <see cref="RequestInstallPermission"/> — the per-app
///         <c>REQUEST_INSTALL_PACKAGES</c> permission gate that API 26+
///         applies before the install intent will resolve.</item>
/// </list>
///
/// <para><b>Phase 5 (Wave 24, 2026-05-18):</b> the legacy
/// <c>CheckAsync(channel)</c> entry point was deleted — Wave 18
/// migrated <c>AndroidApp.AutoUpdate</c> onto
/// <see cref="IUpdateSource.CheckAsync"/> via
/// <see cref="SideloadSource"/>, leaving zero callers. The
/// platform-only permission + install helpers below stayed because
/// they sit underneath the cross-platform <see cref="IAndroidInstaller"/>
/// adapter.</para>
///
/// <para><b>Asset naming convention.</b> Canonical:
/// <c>VPNRouter-v{version}-android.apk</c>. Legacy fallback (any asset
/// starting with <c>com.ninitux.vpnrouter</c> + ending in <c>.apk</c>)
/// — both shapes are now matched inside
/// <see cref="SideloadSource"/>.<c>FindApkAsset</c>; this file no
/// longer needs to know.</para>
///
/// <para><b>Permission gate.</b> Sideload installs need
/// <c>REQUEST_INSTALL_PACKAGES</c> on API 26+. Pre-check via
/// <see cref="PackageManager.CanRequestPackageInstalls"/>; if false,
/// the caller deep-links the user to the per-app
/// <i>Install unknown apps</i> Settings screen via
/// <see cref="Settings.ActionManageUnknownAppSources"/> with a
/// <c>package:</c>-scoped data URI so they only see this app's
/// toggle.</para>
/// </summary>
internal static class AndroidUpdater
{
    private const string UpdateApkFileName = "update.apk";
    private const string FileProviderAuthoritySuffix = ".fileprovider";
    private const string ApkMimeType = "application/vnd.android.package-archive";

    /// <summary>HTTP client used for the APK stream. Relaxed
    /// 10-minute timeout: on slow mobile networks a 41 MB APK can take
    /// 1–3 min to land; the GitHub-probe HttpClient (now owned by
    /// <see cref="SideloadSource"/>) uses a tighter 30 s timeout for
    /// the JSON probe, but the download path needs more headroom. The
    /// download path also reports byte-percent progress through
    /// <see cref="IProgress{T}"/>, so a stalled stream is still
    /// observable via the UI freezing — the timeout is a hard upper
    /// bound, not a stall detector.</summary>
    private static readonly HttpClient _httpDownload = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
    };

    static AndroidUpdater()
    {
        _httpDownload.DefaultRequestHeaders.Add("User-Agent", "VPNRouter-Android");
    }

    /// <summary>
    /// Stream the APK from <see cref="AndroidUpdateInfo.DownloadUrl"/>
    /// into <c>getCacheDir()/update.apk</c>. Reports byte-percent
    /// progress through <paramref name="progress"/> so the UI banner
    /// can render a determinate progress bar. Returns the absolute file
    /// path on success; throws on cancel/IO/HTTP error so the caller
    /// can surface a localized failure message.
    /// </summary>
    public static async Task<string> DownloadApkAsync(
        AndroidUpdateInfo info,
        IProgress<int>? progress,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(info.DownloadUrl))
            throw new InvalidOperationException("Update info has no download URL.");

        var cacheDir = Application.Context.CacheDir
            ?? throw new InvalidOperationException("Application cache directory unavailable.");
        var apkPath = Path.Combine(cacheDir.AbsolutePath, UpdateApkFileName);

        // Defensive cleanup — prior failed attempt may have left a
        // partial file. Don't trust resumed downloads on Android; the
        // disk is small and the APK is ~50 MB, just refetch.
        try { if (File.Exists(apkPath)) File.Delete(apkPath); } catch { /* best-effort */ }

        using var resp = await _httpDownload.GetAsync(
            info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var totalBytes = resp.Content.Headers.ContentLength ?? info.SizeBytes;
        using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (var dst = new FileStream(apkPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
        {
            var buffer = new byte[81920];
            long total = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                total += n;
                if (totalBytes > 0)
                    progress?.Report((int)(total * 100 / totalBytes));
            }
        }

        // Sanity check: HTTP 200 with truncated body still returns
        // success; reject anything dramatically smaller than the
        // declared release-asset size so we don't hand a half-APK to
        // the system installer (which would error out cryptically).
        var got = new FileInfo(apkPath).Length;
        if (info.SizeBytes > 0 && got < info.SizeBytes * 0.9)
        {
            try { File.Delete(apkPath); } catch { }
            throw new InvalidOperationException(
                $"Downloaded APK is too small ({got / 1024} KB vs expected {info.SizeBytes / 1024} KB).");
        }

        return apkPath;
    }

    /// <summary>
    /// Hand the freshly-downloaded APK to the system PackageInstaller.
    /// On API 26+ the caller MUST first verify
    /// <see cref="CanRequestInstall"/>; this method assumes the
    /// permission is already granted. Returns true if the install
    /// dialog launched, false on any failure.
    /// </summary>
    public static bool BeginInstall(string apkPath)
    {
        try
        {
            var ctx = Application.Context;
            var apkFile = new Java.IO.File(apkPath);
            if (!apkFile.Exists())
                return false;

            var authority = ctx.PackageName + FileProviderAuthoritySuffix;
            var contentUri = FileProvider.GetUriForFile(ctx, authority, apkFile);

            var intent = new Intent(Intent.ActionView)
                .SetDataAndType(contentUri, ApkMimeType)
                .SetFlags(ActivityFlags.NewTask
                          | ActivityFlags.GrantReadUriPermission
                          | ActivityFlags.ClearTop);

            ctx.StartActivity(intent);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// True if the OS will let us launch the install intent without
    /// further prompting. Pre-API-26 there's no per-app gate so this
    /// always returns true. On 26+ it forwards to
    /// <see cref="PackageManager.CanRequestPackageInstalls"/>.
    /// </summary>
    public static bool CanRequestInstall()
    {
        try
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.O)
                return true;
            var pm = Application.Context.PackageManager;
            return pm?.CanRequestPackageInstalls() ?? false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Open the per-app "Install unknown apps" Settings screen so the
    /// user can grant <see cref="Manifest.Permission.RequestInstallPackages"/>.
    /// Falls back to the global "Unknown sources" list on devices that
    /// don't surface the per-app screen (rare, mostly older OEM
    /// builds). Caller should re-check <see cref="CanRequestInstall"/>
    /// after the user returns from Settings (typically on
    /// <c>OnActivityResult</c> or <c>OnResume</c>).
    /// </summary>
    public static bool RequestInstallPermission()
    {
        try
        {
            var ctx = Application.Context;
            // API 26+ supports the per-app deep link.
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var uri = global::Android.Net.Uri.Parse("package:" + ctx.PackageName);
                var intent = new Intent(Settings.ActionManageUnknownAppSources, uri)
                    .SetFlags(ActivityFlags.NewTask);
                ctx.StartActivity(intent);
                return true;
            }
            // Pre-26 — no permission gate. Caller shouldn't reach here.
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Android-flavoured cousin of <see cref="VPNRouter.Core.Models.UpdateInfo"/>.
/// Stripped down — no lite update / checksum URL / channel toggle —
/// because Android sideload is a single APK + system installer, no
/// per-asset SHA verification (the system PackageInstaller already
/// validates the APK signature against the existing app's signature).
/// </summary>
internal sealed class AndroidUpdateInfo
{
    public string CurrentVersion { get; init; } = string.Empty;
    public string LatestVersion { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string ReleaseNotes { get; init; } = string.Empty;
    public string HtmlUrl { get; init; } = string.Empty;
}
