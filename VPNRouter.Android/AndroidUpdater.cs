using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using AndroidX.Core.Content;
using VPNRouter.Core;
using VPNRouter.Core.Services;

namespace VPNRouter.Android;

/// <summary>
/// v2.32.0 (2026-05-07) — auto-update for Android, parity with desktop's
/// <see cref="UpdateChecker"/>. Mirrors the desktop flow:
///
/// <list type="number">
///   <item>Hit GitHub releases API, filter by channel
///         (stable / experimental), find the newest tag strictly newer
///         than <see cref="AppVersion.Version"/>.</item>
///   <item>Pick the Android APK asset off that release.</item>
///   <item>Download into <c>getCacheDir()/update.apk</c>, expose via
///         FileProvider, hand off to the system PackageInstaller via
///         <c>Intent.ActionView</c> with the standard <c>application/vnd.android.package-archive</c>
///         MIME type.</item>
/// </list>
///
/// <para><b>Why a separate class instead of reusing <see cref="UpdateChecker"/>?</b>
/// <see cref="UpdateChecker"/> is desktop-only by design — its
/// platform branches (<c>OperatingSystem.IsLinux/IsMacOS/IsWindows</c>)
/// don't have an Android arm, and on Android <c>OperatingSystem.IsLinux()</c>
/// returns true (Android <i>is</i> Linux), so it would happily try to
/// download a tar.gz, untar it, and run a Linux pkexec helper that
/// doesn't exist. Rather than bolt platform branches onto Core's
/// updater (and pull in <c>android.app.PackageInstaller</c> dependencies
/// that wouldn't compile on Windows), this class re-implements the
/// thin GitHub-API + asset-pick + version-compare flow using the
/// internal <see cref="UpdateChecker.TryParseSemVer"/> helper for
/// version compare so the rolling-rN semantics stay identical.</para>
///
/// <para><b>Asset naming convention.</b> The Android APK asset on a
/// GitHub release is expected to be
/// <c>VPNRouter-v{version}-android.apk</c> — mirrors the existing
/// <c>-win.zip / -linux.tar.gz / -mac.zip</c> naming. As a fallback
/// (for releases that haven't migrated yet), we also accept any asset
/// whose name starts with <c>com.ninitux.vpnrouter</c> and ends in
/// <c>.apk</c> — that's the default name the .NET Android SDK emits.
/// The plan doc at <c>plans/v2.32.0-android-autoupdate.md</c> tracks
/// publishing the canonical asset.</para>
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

    /// <summary>HTTP client used for the lightweight GitHub releases
    /// JSON probe. Tight 30 s timeout — matches Core's
    /// <see cref="UpdateChecker"/>.</summary>
    private static readonly HttpClient _httpCheck = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>Separate client for the APK stream with a relaxed
    /// timeout (10 min). On slow mobile networks a 41 MB APK can take
    /// 1–3 min to land; the 30 s probe-tier timeout would have killed
    /// the read mid-flight (live test on KYOCERA / 4 G surfaced
    /// "Download failed: net_http_request_timedout, 30"). The download
    /// path also reports byte-percent progress through
    /// <see cref="IProgress{T}"/>, so a stalled stream is still
    /// observable via the UI freezing — the timeout is a hard upper
    /// bound, not a stall detector.</summary>
    private static readonly HttpClient _httpDownload = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
    };

    static AndroidUpdater()
    {
        _httpCheck.DefaultRequestHeaders.Add("User-Agent", "VPNRouter-Android");
        _httpDownload.DefaultRequestHeaders.Add("User-Agent", "VPNRouter-Android");
    }

    private const string GitHubRepo = "PavelLizunov/VPNRouter";

    /// <summary>
    /// Hit the GitHub releases API, return the highest release strictly
    /// newer than the running version, taking <paramref name="channel"/>
    /// into account ("stable" skips prereleases, "experimental" includes
    /// them). Returns null if no newer release is found, the API is
    /// unreachable, or the release has no Android APK asset.
    /// </summary>
    public static async Task<AndroidUpdateInfo?> CheckAsync(
        string channel,
        CancellationToken ct = default)
    {
        var includePrerelease = string.Equals(channel, "experimental", StringComparison.OrdinalIgnoreCase);

        // Reuse the desktop SemVer parser (rolling-rN aware) so the
        // version-ladder semantics stay consistent. Both sides understand
        // 2.32.0-r1 < 2.32.0-r2 < 2.32.0 stable.
        if (!UpdateChecker.TryParseSemVer(AppVersion.Version, out var current))
            return null;

        var url = $"https://api.github.com/repos/{GitHubRepo}/releases?per_page=30";
        string json;
        try
        {
            json = await _httpCheck.GetStringAsync(url, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }

        var releases = JsonConvert.DeserializeAnonymousType(json, new[]
        {
            new
            {
                tag_name = "",
                body = "",
                html_url = "",
                draft = false,
                prerelease = false,
                assets = new[] { new { browser_download_url = "", size = 0L, name = "" } }
            }
        });
        if (releases == null || releases.Length == 0)
            return null;

        var newer = releases
            .Where(r => !r.draft && (includePrerelease || !r.prerelease))
            .Select(r => new
            {
                Release = r,
                Tag = r.tag_name.TrimStart('v'),
                Parsed = UpdateChecker.TryParseSemVer(r.tag_name.TrimStart('v'), out var v) ? v : (UpdateChecker.SemVer?)null
            })
            .Where(r => r.Parsed != null && r.Parsed.Value.CompareTo(current) > 0)
            .OrderByDescending(r => r.Parsed!.Value)
            .ToList();

        if (newer.Count == 0)
            return null;

        var latest = newer[0];
        var apk = FindApkAsset(latest.Release.assets);
        if (apk == null)
            return null;

        // Concatenate release notes from every release that's strictly
        // newer than the running version — same as desktop, so the user
        // sees the cumulative change list when they skip several rN's.
        var notes = newer
            .Where(r => !string.IsNullOrWhiteSpace(r.Release.body))
            .Select(r => r.Release.body!.Trim())
            .ToList();

        return new AndroidUpdateInfo
        {
            CurrentVersion = AppVersion.Version,
            LatestVersion = latest.Tag,
            DownloadUrl = apk.browser_download_url ?? string.Empty,
            SizeBytes = apk.size,
            ReleaseNotes = string.Join("\n\n", notes),
            HtmlUrl = latest.Release.html_url ?? string.Empty,
        };
    }

    /// <summary>
    /// Locate the Android APK on a release's asset list. Preference
    /// order: <c>VPNRouter-v{ver}-android.apk</c> (canonical, mirrors
    /// other platforms) → fallback any <c>com.ninitux.vpnrouter*.apk</c>
    /// (default .NET Android output). Returns null if neither exists.
    /// </summary>
    private static dynamic? FindApkAsset(dynamic[]? assets)
    {
        if (assets == null) return null;

        var enumerable = (IEnumerable<dynamic>)assets;

        var canonical = enumerable.FirstOrDefault(a =>
        {
            string name = a.name;
            return name.StartsWith("VPNRouter-v", StringComparison.OrdinalIgnoreCase) &&
                   name.EndsWith("-android.apk", StringComparison.OrdinalIgnoreCase);
        });
        if (canonical != null) return canonical;

        return enumerable.FirstOrDefault(a =>
        {
            string name = a.name;
            return name.StartsWith("com.ninitux.vpnrouter", StringComparison.OrdinalIgnoreCase) &&
                   name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);
        });
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
