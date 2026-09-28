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

internal static class AndroidUpdater
{
    private const string UpdateApkFileName = "update.apk";
    private const string FileProviderAuthoritySuffix = ".fileprovider";
    private const string ApkMimeType = "application/vnd.android.package-archive";

    private static readonly HttpClient _httpDownload = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
    };

    static AndroidUpdater()
    {
        _httpDownload.DefaultRequestHeaders.Add("User-Agent", "VPNRouter-Android");
    }

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

        try { if (File.Exists(apkPath)) File.Delete(apkPath); } catch {  }

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

        var got = new FileInfo(apkPath).Length;
        if (info.SizeBytes > 0 && got < info.SizeBytes * 0.9)
        {
            try { File.Delete(apkPath); } catch { }
            throw new InvalidOperationException(
                $"Downloaded APK is too small ({got / 1024} KB vs expected {info.SizeBytes / 1024} KB).");
        }

        return apkPath;
    }

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

    public static bool RequestInstallPermission()
    {
        try
        {
            var ctx = Application.Context;
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var uri = global::Android.Net.Uri.Parse("package:" + ctx.PackageName);
                var intent = new Intent(Settings.ActionManageUnknownAppSources, uri)
                    .SetFlags(ActivityFlags.NewTask);
                ctx.StartActivity(intent);
                return true;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}

internal sealed class AndroidUpdateInfo
{
    public string CurrentVersion { get; init; } = string.Empty;
    public string LatestVersion { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string ReleaseNotes { get; init; } = string.Empty;
    public string HtmlUrl { get; init; } = string.Empty;
}
