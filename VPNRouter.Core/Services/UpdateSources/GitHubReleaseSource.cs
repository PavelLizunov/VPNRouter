// Phase 3 — 3F (v3.0 refactor): desktop concrete IUpdateSource impl.
//
// Wraps the GitHub Releases API discovery + asset-pick flow that lived
// inline in UpdateChecker.CheckForUpdateAsync pre-3F. The download +
// apply paths still go through UpdateChecker (which owns the
// platform-specific helper.cmd / ditto / pkexec dance — too much
// platform surface to fold cleanly into an interface for one phase),
// so DownloadAsync / ApplyAsync here delegate to a caller-supplied
// IUpdateChecker shim. The interface boundary stays clean: callers
// don't need to know about the GitHub JSON shape.
//
// Brief: plans/phase3-3F-android-updatesource-2026-05-18.md.

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

/// <summary>
/// Desktop default <see cref="IUpdateSource"/>. Hits the GitHub Releases
/// API for a tagged release strictly newer than the running version,
/// picks the per-platform asset (<c>VPNRouter-v*-win.zip</c> /
/// <c>-mac.zip</c> / <c>-linux.tar.gz</c>), and fetches the matching
/// <c>.sha256</c> companion file so the caller can pin the asset.
///
/// <para>
/// Download + apply are wired through an <see cref="IDesktopInstaller"/>
/// adapter rather than re-implementing the helper.cmd / detached-bash
/// dance here. The legacy <see cref="UpdateChecker"/> is the canonical
/// adapter for now (Phase 3F+ moves more logic into this class once
/// the contract has settled).
/// </para>
/// </summary>
public sealed class GitHubReleaseSource : IUpdateSource
{
    private readonly IHttpClient _http;
    private readonly UpdateSettings _settings;
    private readonly string _currentVersion;
    private readonly IDesktopInstaller _installer;

    /// <summary>Platform-suffix mapping: matches the build-script naming
    /// convention (see <c>build.ps1</c> / <c>build-mac.sh</c> /
    /// <c>build-linux</c>).</summary>
    private static readonly string PlatformSuffix =
        OperatingSystem.IsMacOS() ? "-mac" :
        OperatingSystem.IsLinux() ? "-linux" :
        "-win";

    /// <summary>Linux ships as .tar.gz; everything else ships as .zip.</summary>
    private static readonly string AssetExtension =
        OperatingSystem.IsLinux() ? ".tar.gz" : ".zip";

    /// <inheritdoc />
    public string SourceId => "github";

    /// <summary>
    /// Production ctor — caller supplies the shared
    /// <see cref="PolicyHttpClient"/> + an installer adapter (today's
    /// <see cref="UpdateChecker"/> implements it).
    /// </summary>
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

    /// <inheritdoc />
    public async Task<UpdateSourceInfo?> CheckAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.GitHubRepo))
            return null;

        // v2.21.10: rolling-rN aware parser. Tags with non-rN suffixes
        // (e.g. "v1.0.0-mac") return false and are skipped.
        if (!UpdateChecker.TryParseSemVer(_currentVersion, out var current))
            return null;

        var url = $"https://api.github.com/repos/{_settings.GitHubRepo}/releases?per_page=30";
        var listResponse = await _http.SendAsync(
            new HttpRequest(System.Net.Http.HttpMethod.Get, new Uri(url)),
            ct).ConfigureAwait(false);
        if (!listResponse.IsSuccess())
            return null;

        // Phase 4 (2026-05-18) — explicit DTOs replace Newtonsoft's
        // DeserializeAnonymousType. snake_case wire keys pinned via
        // [JsonPropertyName] match the GitHub Releases API contract
        // (tag_name / browser_download_url / etc.) exactly — round-trip
        // tests in Phase3StjJsonRoundTripTests.GitHubRelease_* pin this.
        // PropertyNameCaseInsensitive=true on GitHubReleaseJsonOptions
        // tolerates any GitHub variant casing without breaking parse.
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
        var asset = FindFullAsset(latest.Release.Assets);
        if (asset == null)
            return null;

        // Companion .sha256 fetch — best-effort. Asset name is
        // "{asset}.sha256" by build.ps1 convention.
        string? sha = null;
        var shaAsset = FindChecksumAsset(latest.Release.Assets, asset);
        if (shaAsset != null)
        {
            var shaResp = await _http.SendAsync(
                new HttpRequest(System.Net.Http.HttpMethod.Get, new Uri(shaAsset.BrowserDownloadUrl)),
                ct).ConfigureAwait(false);
            if (shaResp.IsSuccess())
            {
                var raw = shaResp.AsString().Trim().ToLowerInvariant();
                // ".sha256" file format: either "HASH" or "HASH  filename"
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
            AssetName: asset.Name,
            DownloadUrl: asset.BrowserDownloadUrl,
            AssetSize: asset.Size,
            AssetSha256: sha,
            IsPrerelease: latest.Release.Prerelease,
            ReleaseNotes: string.Join("\n\n", notes));
    }

    /// <inheritdoc />
    public Task<string> DownloadAsync(
        UpdateSourceInfo info,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        return _installer.DownloadAndStageAsync(info, progress, ct);
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
        return _installer.ApplyStagedAsync(info, stagedPath, ct);
    }

    // ─── Asset matching ──────────────────────────────────────────────────

    /// <summary>
    /// Find the full install asset for the current desktop platform.
    /// Mirrors <see cref="UpdateChecker"/>'s legacy private
    /// <c>FindFullAsset</c> — single source of truth lives here now.
    /// </summary>
    private static GitHubAsset? FindFullAsset(GitHubAsset[]? assets)
    {
        if (assets == null) return null;

        // New naming: VPNRouter-v*-{platform}{ext} (not containing "update")
        var newFormat = assets.FirstOrDefault(a =>
        {
            var name = a.Name ?? string.Empty;
            return name.StartsWith("VPNRouter-v", StringComparison.OrdinalIgnoreCase) &&
                   name.EndsWith($"{PlatformSuffix}{AssetExtension}", StringComparison.OrdinalIgnoreCase) &&
                   !name.Contains("update", StringComparison.OrdinalIgnoreCase);
        });
        if (newFormat != null) return newFormat;

        // Legacy (Windows only): VPNRouter-install-v*.zip
        if (OperatingSystem.IsWindows())
        {
            return assets.FirstOrDefault(a =>
            {
                var name = a.Name ?? string.Empty;
                return name.StartsWith("VPNRouter-install-v", StringComparison.OrdinalIgnoreCase) &&
                       name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            });
        }
        return null;
    }

    /// <summary>Find the .sha256 companion asset for a given install
    /// asset (naming: <c>{name}.sha256</c>).</summary>
    private static GitHubAsset? FindChecksumAsset(GitHubAsset[]? assets, GitHubAsset? zipAsset)
    {
        if (assets == null || zipAsset == null) return null;
        var target = $"{zipAsset.Name}.sha256";
        return assets.FirstOrDefault(a =>
            string.Equals(a.Name, target, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Phase 4 (2026-05-18) — STJ options shared by every GitHub Releases
    /// API parse in this assembly. Case-insensitive so any GitHub API
    /// quirk (extremely unlikely but harmless to allow) doesn't break
    /// parse; <c>JsonNumberHandling.AllowReadingFromString</c> covers
    /// any future field GitHub starts emitting as a string instead of a
    /// number.
    /// </summary>
    internal static readonly JsonSerializerOptions GitHubReleaseJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        // Phase 5 — Wave 25 AOT-2 (2026-05-18): GitHubRelease + GitHubAsset +
        // GitHubRelease[] are registered in AppJsonContext so the GitHub
        // Releases API parse routes through generated JsonTypeInfo on AOT
        // builds. Compose with DefaultJsonTypeInfoResolver so callers
        // passing this options instance for any other DTO (none today)
        // still work via reflection. Phase 6 retires the reflective
        // fallback once PublishAot lands.
        TypeInfoResolver = JsonTypeInfoResolver.Combine(
            AppJsonContext.Default,
            new DefaultJsonTypeInfoResolver()),
    };
}

/// <summary>
/// Phase 4 (2026-05-18) — explicit DTO mirroring the subset of the GitHub
/// Releases API response we consume. Pre-Phase-4 the code used
/// <see cref="System.Object"/>-typed anonymous templates via Newtonsoft's
/// <c>DeserializeAnonymousType</c>; this typed shape is the STJ equivalent
/// and is pinned by Phase3StjJsonRoundTripTests.
/// </summary>
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

/// <summary>
/// Adapter the desktop <see cref="GitHubReleaseSource"/> uses to defer
/// platform-specific download + helper-dispatch logic. Today's sole
/// implementation is <see cref="UpdateChecker"/>; Phase 4 could split
/// it further (e.g. <c>WindowsInstaller</c> / <c>MacInstaller</c> /
/// <c>LinuxInstaller</c>) without changing the source contract.
/// </summary>
public interface IDesktopInstaller
{
    /// <summary>Stream the asset bytes to disk + verify checksum +
    /// extract. Returns the extracted directory path.</summary>
    Task<string> DownloadAndStageAsync(
        UpdateSourceInfo info,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct);

    /// <summary>Hand the extracted directory to the platform helper
    /// (helper.cmd / ditto / pkexec).</summary>
    Task<bool> ApplyStagedAsync(
        UpdateSourceInfo info,
        string stagedPath,
        CancellationToken ct);
}
