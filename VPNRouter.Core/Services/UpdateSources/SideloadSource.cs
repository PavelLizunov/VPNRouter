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

public sealed class SideloadSource : IUpdateSource
{
    private readonly IHttpClient _http;
    private readonly UpdateSettings _settings;
    private readonly string _currentVersion;
    private readonly IAndroidInstaller _installer;

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

    public async Task<string> DownloadAsync(
        UpdateSourceInfo info,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (string.IsNullOrWhiteSpace(info.DownloadUrl))
            throw new InvalidOperationException("Update info has no download URL.");

        var apkPath = await _installer.DownloadApkAsync(info, progress, ct).ConfigureAwait(false);

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
                try { File.Delete(apkPath); } catch { }
                throw new InvalidOperationException(
                    "APK checksum mismatch — download is corrupted or tampered. " +
                    $"Expected: {info.AssetSha256}\nGot:      {actual}");
            }
        }

        return apkPath;
    }

    public Task<bool> ApplyAsync(
        UpdateSourceInfo info,
        string stagedPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (string.IsNullOrWhiteSpace(stagedPath))
            throw new ArgumentException("Staged path must be non-empty.", nameof(stagedPath));
        return _installer.BeginInstallAsync(stagedPath, ct);
    }

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

    private static GitHubAsset? FindChecksumAsset(GitHubAsset[]? assets, GitHubAsset? apkAsset)
    {
        if (assets == null || apkAsset == null) return null;
        var target = $"{apkAsset.Name}.sha256";
        return assets.FirstOrDefault(a =>
            string.Equals(a.Name, target, StringComparison.OrdinalIgnoreCase));
    }
}

public interface IAndroidInstaller
{
    Task<string> DownloadApkAsync(
        UpdateSourceInfo info,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct);

    Task<bool> BeginInstallAsync(string apkPath, CancellationToken ct);
}
