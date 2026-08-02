// Phase 3 — 3F (v3.0 refactor): Android sideload IUpdateSource impl.
//
// Pulls the .apk asset from a GitHub Release and hands it off to the
// platform installer via a caller-supplied IAndroidInstaller adapter
// (today: AndroidInstallerAdapter on top of AndroidUpdater). The
// IUpdateSource contract owns:
//   1. GitHub release JSON probe (shared shape with GitHubReleaseSource).
//   2. APK asset discovery (canonical "VPNRouter-v*-android.apk" or
//      fallback "com.ninitux.vpnrouter*.apk" from the .NET Android SDK
//      default emit).
//   3. SHA256 fetch (companion .sha256 file, when published).
//
// CRITICAL — SHA256 ordering: DownloadAsync MUST hash the bytes on
// disk BEFORE returning the path. The system PackageInstaller called
// by ApplyAsync trusts the file on disk wholesale; once Intent.ActionView
// fires we have NO way to abort the install. The check is the last gate.
// See SecurityReview note in tests/IUpdateSourceContractTests.cs.
//
// Brief: plans/phase3-3F-android-updatesource-2026-05-18.md.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services.UpdateSources;

/// <summary>
/// Android sideload <see cref="IUpdateSource"/>. Downloads the .apk
/// asset from a GitHub Release and dispatches it to the system
/// PackageInstaller via <see cref="IAndroidInstaller"/>. Production
/// adapter lives in <c>VPNRouter.Android.AndroidInstallerAdapter</c>.
///
/// <para>
/// The class is portable (pure HTTP + JSON + SHA256), so tests can
/// construct it on any platform with a fake <see cref="IAndroidInstaller"/>.
/// Android-only methods (Intent.ActionView dispatch, Application.Context
/// CacheDir lookup) live behind the adapter interface so this Core
/// type can compile as <c>net8.0</c>.
/// </para>
///
/// <para><b>Distribution channel.</b> Sideload pulls from GitHub
/// Releases — the same source as the desktop. Variants:
/// <list type="bullet">
///   <item><see cref="SideloadSource"/> — this class. Default for
///   non-Play-Store distribution.</item>
/// </list>
/// Build-time flavour selection lands in Phase 4 via an MSBuild
/// constant; today's APK ships with the sideload variant.</para>
/// </summary>
public sealed class SideloadSource : IUpdateSource
{
    private readonly IHttpClient _http;
    private readonly UpdateSettings _settings;
    private readonly string _currentVersion;
    private readonly IAndroidInstaller _installer;

    /// <inheritdoc />
    public string SourceId => "sideload";

    public SideloadSource(
        UpdateSettings settings,
        string currentVersion,
        IHttpClient http,
        IAndroidInstaller installer)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _currentVersion = currentVersion ?? throw new ArgumentNullException(nameof(currentVersion));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
    }

    /// <inheritdoc />
    public async Task<UpdateSourceInfo?> CheckAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.GitHubRepo))
            return null;

        if (!UpdateChecker.TryParseSemVer(_currentVersion, out var current))
            return null;

        var url = $"https://api.github.com/repos/{_settings.GitHubRepo}/releases?per_page=30";
        var listResponse = await _http.SendAsync(
            new HttpRequest(System.Net.Http.HttpMethod.Get, new Uri(url)),
            ct).ConfigureAwait(false);
        if (!listResponse.IsSuccess())
            return null;

        // Phase 4 (2026-05-18) — share the same GitHubRelease/GitHubAsset
        // DTOs as GitHubReleaseSource (single source of truth for the
        // wire shape). Round-trip tests pin the wire keys in
        // Phase3StjJsonRoundTripTests.GitHubRelease_*.
        GitHubRelease[]? releases;
        try
        {
            releases = JsonSerializer.Deserialize(
                listResponse.AsString(), VPNRouter.Core.Json.AppJsonContext.Default.GitHubReleaseArray);
        }
        catch (JsonException)
        {
            return null;
        }
        if (releases == null || releases.Length == 0)
            return null;

        var newer = releases
            .Where(r => !r.Draft && (_settings.IsExperimental || !r.Prerelease))
            .Select(r => new
            {
                Release = r,
                Tag = (r.TagName ?? string.Empty).TrimStart('v'),
                Parsed = UpdateChecker.TryParseSemVer((r.TagName ?? string.Empty).TrimStart('v'), out var v) ? v : (UpdateChecker.SemVer?)null
            })
            .Where(r => r.Parsed != null && r.Parsed.Value.CompareTo(current) > 0)
            .OrderByDescending(r => r.Parsed!.Value)
            .ToList();

        if (newer.Count == 0)
            return null;

        var latest = newer[0];
        var apk = FindApkAsset(latest.Release.Assets);
        if (apk == null)
            return null;

        // Companion .sha256 fetch — best-effort.
        string? sha = null;
        var shaAsset = FindChecksumAsset(latest.Release.Assets, apk);
        if (shaAsset != null)
        {
            var shaResp = await _http.SendAsync(
                new HttpRequest(System.Net.Http.HttpMethod.Get, new Uri(shaAsset.BrowserDownloadUrl)),
                ct).ConfigureAwait(false);
            if (shaResp.IsSuccess())
            {
                var raw = shaResp.AsString().Trim().ToLowerInvariant();
                if (raw.Contains(' '))
                    raw = raw.Split(' ', 2)[0].Trim();
                if (raw.Length == 64)
                    sha = raw;
            }
        }

        var notes = newer
            .Where(r => !string.IsNullOrWhiteSpace(r.Release.Body))
            .Select(r => r.Release.Body!.Trim());

        return new UpdateSourceInfo(
            Version: latest.Tag,
            ReleaseUrl: latest.Release.HtmlUrl ?? string.Empty,
            AssetName: apk.Name,
            DownloadUrl: apk.BrowserDownloadUrl,
            AssetSize: apk.Size,
            AssetSha256: sha,
            IsPrerelease: latest.Release.Prerelease,
            ReleaseNotes: string.Join("\n\n", notes));
    }

    /// <inheritdoc />
    public async Task<string> DownloadAsync(
        UpdateSourceInfo info,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (string.IsNullOrWhiteSpace(info.DownloadUrl))
            throw new InvalidOperationException("Update info has no download URL.");

        // Delegate the actual stream-to-cache write to the platform
        // installer adapter — it knows about Application.Context.CacheDir
        // on Android. The contract guarantees the returned path is a
        // real file we can hash.
        var apkPath = await _installer.DownloadApkAsync(info, progress, ct).ConfigureAwait(false);

        // SECURITY GATE — verify SHA256 BEFORE handing the path back to
        // the caller. The caller's next step is ApplyAsync which fires
        // Intent.ActionView; once that dispatches, the OS
        // PackageInstaller trusts the file on disk and we have no abort
        // path. If the .sha256 companion wasn't published (legacy
        // releases) we degrade to size-only validation — already
        // enforced inside DownloadApkAsync.
        if (!string.IsNullOrEmpty(info.AssetSha256))
        {
            string actual;
            await using (var fs = File.OpenRead(apkPath))
            {
                var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
                actual = Convert.ToHexStringLower(hash);
            }
            if (!string.Equals(actual, info.AssetSha256, StringComparison.Ordinal))
            {
                // Wipe the corrupt file so a retry pulls fresh bytes.
                try { File.Delete(apkPath); } catch { /* best-effort */ }
                throw new InvalidOperationException(
                    "APK checksum mismatch — download is corrupted or tampered. " +
                    $"Expected: {info.AssetSha256}\nGot:      {actual}");
            }
        }

        return apkPath;
    }

    /// <inheritdoc />
    public Task<bool> ApplyAsync(
        UpdateSourceInfo info,
        string stagedPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (string.IsNullOrWhiteSpace(stagedPath))
            throw new ArgumentException("Staged path must be non-empty.", nameof(stagedPath));
        // The caller (UI VM on Android) is expected to have already
        // verified IAndroidInstaller.CanRequestInstall() and offered
        // the Settings deep-link if not. We don't probe again here —
        // double-prompting is worse UX than letting the system installer
        // surface the missing-permission error.
        return _installer.BeginInstallAsync(stagedPath, ct);
    }

    // ─── Asset matching ──────────────────────────────────────────────────

    /// <summary>
    /// Locate the Android APK asset. Preference order:
    /// canonical <c>VPNRouter-v{ver}-android.apk</c> → fallback any
    /// <c>com.ninitux.vpnrouter*.apk</c> (default .NET Android emit).
    /// Mirrors the legacy AndroidUpdater.FindApkAsset.
    /// </summary>
    private static GitHubAsset? FindApkAsset(GitHubAsset[]? assets)
    {
        if (assets == null) return null;

        var canonical = assets.FirstOrDefault(a =>
        {
            var name = a.Name ?? string.Empty;
            return name.StartsWith("VPNRouter-v", StringComparison.OrdinalIgnoreCase) &&
                   name.EndsWith("-android.apk", StringComparison.OrdinalIgnoreCase);
        });
        if (canonical != null) return canonical;

        return assets.FirstOrDefault(a =>
        {
            var name = a.Name ?? string.Empty;
            return name.StartsWith("com.ninitux.vpnrouter", StringComparison.OrdinalIgnoreCase) &&
                   name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>Find the .sha256 companion asset for a given APK
    /// asset.</summary>
    private static GitHubAsset? FindChecksumAsset(GitHubAsset[]? assets, GitHubAsset? apkAsset)
    {
        if (assets == null || apkAsset == null) return null;
        var target = $"{apkAsset.Name}.sha256";
        return assets.FirstOrDefault(a =>
            string.Equals(a.Name, target, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Adapter for the Android-specific bits of sideload install. Lives in
/// this Core-side interface so <see cref="SideloadSource"/> stays
/// compileable in <c>net8.0</c> tests (no <c>Android.App</c> import).
/// Production impl: <c>VPNRouter.Android.AndroidInstallerAdapter</c>
/// over the existing <c>AndroidUpdater</c> static methods.
/// </summary>
public interface IAndroidInstaller
{
    /// <summary>
    /// Stream the APK from <see cref="UpdateSourceInfo.DownloadUrl"/>
    /// into the per-app cache directory; report byte-percent progress.
    /// Returns the absolute file path. The Core-side
    /// <see cref="SideloadSource"/> will then verify SHA256 against the
    /// returned bytes BEFORE letting the caller call
    /// <see cref="BeginInstallAsync"/> — the implementor MUST NOT skip
    /// past the source's hashing.
    /// </summary>
    Task<string> DownloadApkAsync(
        UpdateSourceInfo info,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct);

    /// <summary>
    /// Fire <c>Intent.ActionView</c> with the
    /// <c>application/vnd.android.package-archive</c> MIME type
    /// pointing at <paramref name="apkPath"/>. Returns false if the
    /// permission is missing or the intent could not be launched.
    /// </summary>
    Task<bool> BeginInstallAsync(string apkPath, CancellationToken ct);
}
