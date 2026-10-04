#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Json;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services.UpdateSources;

public sealed class GitHubReleaseSource : IUpdateSource
{
    private readonly IHttpClient _http;
    private readonly UpdateSettings _settings;
    private readonly string _currentVersion;
    private readonly IDesktopInstaller _installer;

    private static readonly string PlatformSuffix =
        OperatingSystem.IsMacOS() ? "-mac" :
        OperatingSystem.IsLinux() ? "-linux" :
        "-win";

    private static readonly string AssetExtension =
        OperatingSystem.IsLinux() ? ".tar.gz" : ".zip";

    public string SourceId => "github";

    public GitHubReleaseSource(
        UpdateSettings settings,
        string currentVersion,
        IHttpClient http,
        IDesktopInstaller installer)
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

        var releases = await FetchReleasesAsync(ct).ConfigureAwait(false);

        if (releases == null || releases.Length == 0)
            return null;

        var newer = ReleaseCandidates.NewerThan(releases, _settings.IsExperimental, current);

        if (newer.Count == 0)
            return null;

        foreach (var candidate in newer)
        {
            var asset = FindFullAsset(candidate.Release.Assets, candidate.Tag);
            if (asset == null ||
                !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var assetUri) ||
                (assetUri.Scheme != Uri.UriSchemeHttp && assetUri.Scheme != Uri.UriSchemeHttps))
                continue;

            var sha = await FetchChecksumAsync(candidate.Release.Assets, asset, ct)
                .ConfigureAwait(false);
            if (!IsValidSha256(sha))
                continue;

            var notes = newer
                .Where(r => !string.IsNullOrWhiteSpace(r.Release.Body))
                .Select(r => r.Release.Body!.Trim());

            return new UpdateSourceInfo(
                Version: candidate.Tag,
                ReleaseUrl: candidate.Release.HtmlUrl ?? string.Empty,
                AssetName: asset.Name,
                DownloadUrl: asset.BrowserDownloadUrl,
                AssetSize: asset.Size,
                AssetSha256: sha,
                IsPrerelease: candidate.Release.Prerelease,
                ReleaseNotes: string.Join("\n\n", notes));
        }

        return null;
    }

    public Task<IReadOnlyList<UpdateSourceInfo>> ListStableAsync(
        int maxCount,
        CancellationToken ct = default) =>
        ListOlderAsync(maxCount, includePrereleases: false, ct);

    public async Task<IReadOnlyList<UpdateSourceInfo>> ListOlderAsync(
        int maxCount,
        bool includePrereleases,
        CancellationToken ct = default)
    {
        if (maxCount <= 0 || string.IsNullOrWhiteSpace(_settings.GitHubRepo) ||
            !UpdateChecker.TryParseSemVer(_currentVersion, out var current))
            return Array.Empty<UpdateSourceInfo>();

        var releases = await FetchReleasesAsync(ct).ConfigureAwait(false);
        if (releases == null)
            return Array.Empty<UpdateSourceInfo>();

        // Stable list: only real stable releases. Candidate list: a -rN tag counts as a candidate whatever flag the release carries.
        var candidates = ReleaseCandidates.Parse(releases.Where(r => !r.Draft && (includePrereleases || !r.Prerelease)))
            .Where(r => r.Parsed != null
                && (includePrereleases || r.Parsed.Value.Rc == null)
                && r.Parsed.Value.CompareTo(current) < 0)
            .OrderByDescending(r => r.Parsed!.Value);

        // On the candidate list the newest releases are mostly candidates; keep room for the three newest stable ones, which is where a tester
        // wants to go back to. Without candidates the whole list is stable and the reserve is not used.
        var stableReserve = includePrereleases && maxCount >= MinCountForStableReserve ? StableReserve : 0;
        var openSlots = maxCount - stableReserve;
        var acceptedStable = 0;
        var result = new List<(UpdateSourceInfo Info, UpdateChecker.SemVer Version)>(Math.Min(maxCount, 8));
        foreach (var candidate in candidates)
        {
            var isStable = candidate.Parsed!.Value.Rc == null && !candidate.Release.Prerelease;
            if (result.Count >= openSlots && !(isStable && acceptedStable < stableReserve))
                continue;

            var asset = FindFullAsset(candidate.Release.Assets, candidate.Tag);
            if (asset == null ||
                !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var assetUri) ||
                (assetUri.Scheme != Uri.UriSchemeHttp && assetUri.Scheme != Uri.UriSchemeHttps))
                continue;

            var sha = await FetchChecksumAsync(candidate.Release.Assets, asset, ct)
                .ConfigureAwait(false);
            if (!IsValidSha256(sha))
                continue;

            result.Add((new UpdateSourceInfo(
                Version: candidate.Tag,
                ReleaseUrl: candidate.Release.HtmlUrl ?? string.Empty,
                AssetName: asset.Name,
                DownloadUrl: asset.BrowserDownloadUrl,
                AssetSize: asset.Size,
                AssetSha256: sha,
                IsPrerelease: candidate.Release.Prerelease || candidate.Parsed!.Value.Rc != null,
                ReleaseNotes: candidate.Release.Body?.Trim() ?? string.Empty), candidate.Parsed!.Value));
            if (isStable) acceptedStable++;

            if (result.Count >= maxCount || (result.Count >= openSlots && acceptedStable >= stableReserve))
                break;
        }

        return result.OrderByDescending(r => r.Version).Select(r => r.Info).ToList();
    }

    private const int StableReserve = 3;
    private const int MinCountForStableReserve = 4;

    public Task<string> DownloadAsync(
        UpdateSourceInfo info,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        return _installer.DownloadAndStageAsync(info, progress, ct);
    }

    public Task<bool> ApplyAsync(
        UpdateSourceInfo info,
        string stagedPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (string.IsNullOrWhiteSpace(stagedPath))
            throw new ArgumentException("Staged path must be non-empty.", nameof(stagedPath));
        return _installer.ApplyStagedAsync(info, stagedPath, ct);
    }

    private async Task<GitHubRelease[]?> FetchReleasesAsync(CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{_settings.GitHubRepo}/releases?per_page=30";
        var response = await _http.SendAsync(
            new HttpRequest(System.Net.Http.HttpMethod.Get, new Uri(url)), ct)
            .ConfigureAwait(false);
        if (!response.IsSuccess())
            return null;

        try
        {
            return JsonSerializer.Deserialize(
                response.AsString(), AppJsonContext.Default.GitHubReleaseArray);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string?> FetchChecksumAsync(
        GitHubAsset[]? assets,
        GitHubAsset asset,
        CancellationToken ct)
    {
        var shaAsset = FindChecksumAsset(assets, asset);
        if (shaAsset == null ||
            !Uri.TryCreate(shaAsset.BrowserDownloadUrl, UriKind.Absolute, out var shaUri) ||
            (shaUri.Scheme != Uri.UriSchemeHttp && shaUri.Scheme != Uri.UriSchemeHttps))
            return null;

        var response = await _http.SendAsync(
            new HttpRequest(System.Net.Http.HttpMethod.Get, shaUri),
            ct).ConfigureAwait(false);
        if (!response.IsSuccess())
            return null;

        var raw = response.AsString().Trim().ToLowerInvariant();
        if (raw.Contains(' '))
            raw = raw.Split(' ', 2)[0].Trim();
        return IsValidSha256(raw) ? raw : null;
    }

    private static bool IsValidSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static GitHubAsset? FindFullAsset(GitHubAsset[]? assets, string expectedVersion)
    {
        if (assets == null) return null;

        var expectedName = $"VPNRouter-v{expectedVersion}{PlatformSuffix}{AssetExtension}";
        var newFormat = assets.FirstOrDefault(a =>
            string.Equals(a.Name, expectedName, StringComparison.OrdinalIgnoreCase));
        if (newFormat != null) return newFormat;

        if (OperatingSystem.IsWindows())
        {
            var legacyName = $"VPNRouter-install-v{expectedVersion}.zip";
            return assets.FirstOrDefault(a =>
                string.Equals(a.Name, legacyName, StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }

    private static GitHubAsset? FindChecksumAsset(GitHubAsset[]? assets, GitHubAsset? zipAsset)
    {
        if (assets == null || zipAsset == null) return null;
        var target = $"{zipAsset.Name}.sha256";
        return assets.FirstOrDefault(a =>
            string.Equals(a.Name, target, StringComparison.OrdinalIgnoreCase));
    }

    internal static readonly JsonSerializerOptions GitHubReleaseJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        TypeInfoResolver = JsonTypeInfoResolver.Combine(
            AppJsonContext.Default,
            new DefaultJsonTypeInfoResolver()),
    };
}

internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("assets")]
    public GitHubAsset[]? Assets { get; set; }
}

internal sealed class GitHubAsset
{
    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public interface IDesktopInstaller
{
    Task<string> DownloadAndStageAsync(
        UpdateSourceInfo info,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct);

    Task<bool> ApplyStagedAsync(
        UpdateSourceInfo info,
        string stagedPath,
        CancellationToken ct);
}
