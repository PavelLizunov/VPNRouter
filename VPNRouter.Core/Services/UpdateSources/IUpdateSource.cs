// Phase 3 — 3F (v3.0 refactor): per-platform abstraction over the
// "where do updates come from" axis.
//
// Audit B (plans/v3.0-architecture-roadmap.md §3F): pre-3F, the desktop
// auto-update path (VPNRouter.Core.Services.UpdateChecker) and the
// Android sideload path (VPNRouter.Android.AndroidUpdater) duplicated
// GitHub-release-API logic with a hard-coded distribution channel —
// blocked future Play Store distribution because every change to that
// channel required forking the desktop updater.
//
// Solution: thin IUpdateSource interface + 2 concrete impls.
//   • GitHubReleaseSource — desktop default (win/mac/linux), wraps the
//     existing UpdateChecker.CheckForUpdateAsync flow against the
//     GitHub Releases JSON API.
//   • SideloadSource — current Android distribution: same GitHub
//     Releases API but picks the .apk asset and hands install off to
//     android.app.PackageInstaller via Intent.ActionView.
//
// UpdateChecker now becomes a thin wrapper that delegates to the
// platform-appropriate IUpdateSource via PlatformServices.
// CreateUpdateSource factory. Existing public surface
// (CheckForUpdateAsync / DownloadAndStageAsync / ApplyUpdate /
// CleanupStagingDir / events) stays identical so call sites
// (UpdateNotificationViewModel desktop, TestUpdateCommand CI, Android's
// AndroidUpdater) don't move yet — only the GitHub asset discovery +
// JSON parse is delegated. Phase 3F+ converts Android's bespoke updater
// to the same interface.
//
// Brief: plans/phase3-3F-android-updatesource-2026-05-18.md.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VPNRouter.Core.Services.UpdateSources;

/// <summary>
/// Per-platform update-distribution abstraction. Concrete implementations:
/// <list type="bullet">
///   <item><see cref="GitHubReleaseSource"/> — desktop (Win/Mac/Linux).</item>
///   <item><see cref="SideloadSource"/> — Android sideload via GitHub Releases.</item>
/// </list>
///
/// <para>
/// Each instance is bound to a single source (one repo, one variant) at
/// construction time. <see cref="CheckAsync"/> returns a stable
/// <see cref="UpdateSourceInfo"/> snapshot; <see cref="DownloadAsync"/>
/// streams the asset with <see cref="IProgress{T}"/>-driven progress so
/// the UI can render a determinate progress bar without polling;
/// <see cref="ApplyAsync"/> hands the downloaded payload to the
/// platform-specific installer (xcopy.cmd helper on Windows,
/// detached-bash on Linux/macOS, <c>Intent.ActionView</c> on Android).
/// </para>
///
/// <para>
/// Security contract: <see cref="DownloadAsync"/> MUST validate the
/// downloaded bytes against <see cref="UpdateSourceInfo.AssetSha256"/>
/// (when non-null) BEFORE returning the stream. The implementor cannot
/// defer this check to the caller — by the time
/// <see cref="ApplyAsync"/> dispatches to the system installer (Intent
/// on Android, helper.cmd on Windows, etc.) the bytes are already on
/// disk and the system installer trusts them. SHA verification is the
/// last gate against a tampered or truncated transfer.
/// </para>
/// </summary>
public interface IUpdateSource
{
    /// <summary>
    /// Probe the source for a release strictly newer than the running
    /// version. Returns null when up-to-date, the source is
    /// unreachable, or no eligible asset exists for this platform.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Update metadata, or <c>null</c> when nothing newer is
    /// available. Implementations MUST NOT throw on transient network
    /// errors — those return <c>null</c> instead so the caller can
    /// silently retry on the next poll interval. Configuration errors
    /// (e.g. missing repo) ARE allowed to throw.</returns>
    Task<UpdateSourceInfo?> CheckAsync(CancellationToken ct = default);

    /// <summary>
    /// Return a bounded list of recent stable releases older than the running
    /// version. Sources that cannot safely install older builds return an empty
    /// list. Every returned item must carry a verified SHA-256 digest.
    /// </summary>
    Task<IReadOnlyList<UpdateSourceInfo>> ListStableAsync(
        int maxCount,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UpdateSourceInfo>>(Array.Empty<UpdateSourceInfo>());

    /// <summary>
    /// Stream the asset described in <paramref name="info"/> into a
    /// platform-appropriate staging location, reporting byte-percent
    /// progress through <paramref name="progress"/>. Returns the path
    /// of the staged payload (extracted directory or APK file,
    /// platform-dependent).
    /// </summary>
    /// <param name="info">Metadata returned from <see cref="CheckAsync"/>.</param>
    /// <param name="progress">Optional progress sink. <c>null</c> = no
    /// progress reporting; the download still happens.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Absolute path to the staged payload. Caller hands this
    /// path to <see cref="ApplyAsync"/>.</returns>
    /// <exception cref="InvalidOperationException">SHA256 check failed,
    /// asset truncated, or the download URL is empty.</exception>
    Task<string> DownloadAsync(
        UpdateSourceInfo info,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Hand the staged payload at <paramref name="stagedPath"/> to the
    /// platform installer. Implementation details:
    /// <list type="bullet">
    ///   <item>Windows / Linux / macOS: spawn detached helper that
    ///   replaces the running install dir after this process exits.</item>
    ///   <item>Android sideload: launch <c>Intent.ActionView</c> with
    ///   <c>application/vnd.android.package-archive</c> MIME. The OS
    ///   PackageInstaller dialog drives the rest.</item>
    ///   <item>Android Play Store: throws (Play Store owns the upgrade).</item>
    /// </list>
    /// </summary>
    /// <param name="info">Update metadata (for receipt / logging).</param>
    /// <param name="stagedPath">Path returned by <see cref="DownloadAsync"/>.</param>
    /// <param name="ct">Cancellation token (rarely honoured — most
    /// platforms fire-and-forget the helper).</param>
    /// <returns><c>true</c> when the installer/helper launched
    /// successfully; <c>false</c> when it could not start (e.g. missing
    /// REQUEST_INSTALL_PACKAGES on Android, missing helper binary on
    /// Linux).</returns>
    Task<bool> ApplyAsync(
        UpdateSourceInfo info,
        string stagedPath,
        CancellationToken ct = default);

    /// <summary>
    /// Short, machine-stable identifier (e.g. "github", "sideload",
    /// "play-store"). Logged + persisted so post-mortem analysis knows
    /// which source path was active.
    /// </summary>
    string SourceId { get; }
}

/// <summary>
/// Snapshot of an available update returned by <see cref="IUpdateSource.CheckAsync"/>.
/// </summary>
/// <param name="Version">Release tag string with <c>v</c> prefix
/// stripped (e.g. <c>"2.32.0"</c> or <c>"2.32.0-r1"</c>). Comparable
/// via <see cref="UpdateChecker.TryParseSemVer"/>.</param>
/// <param name="ReleaseUrl">Human-readable release page URL (e.g.
/// <c>https://github.com/PavelLizunov/VPNRouter/releases/tag/v2.32.0</c>).
/// Used for the "View release notes" link.</param>
/// <param name="AssetName">Filename of the downloadable asset (e.g.
/// <c>VPNRouter-v2.32.0-win.zip</c>, <c>VPNRouter-v2.32.0-android.apk</c>).</param>
/// <param name="DownloadUrl">Direct download URL for
/// <paramref name="AssetName"/>.</param>
/// <param name="AssetSize">Asset size in bytes (advisory — used for
/// truncation check + progress bar denominator when
/// <c>Content-Length</c> isn't set on the response).</param>
/// <param name="AssetSha256">Hex-encoded SHA256 of the asset bytes (64
/// lowercase chars). <c>null</c> when no <c>.sha256</c> companion file
/// was published (legacy releases) — in that case the source falls
/// back to size-only validation.</param>
/// <param name="IsPrerelease">True when the release is flagged as
/// pre-release on the source (GitHub <c>prerelease</c> flag). Lets the
/// caller surface a "candidate" badge in the UI.</param>
/// <param name="ReleaseNotes">Cumulative markdown release notes from
/// every version newer than the running build. Empty string when none.</param>
public sealed record UpdateSourceInfo(
    string Version,
    string ReleaseUrl,
    string AssetName,
    string DownloadUrl,
    long AssetSize,
    string? AssetSha256,
    bool IsPrerelease,
    string ReleaseNotes);

/// <summary>
/// Streaming-download progress sample passed to the caller-supplied
/// <see cref="IProgress{T}"/> sink.
/// </summary>
/// <param name="BytesReceived">Bytes received so far.</param>
/// <param name="TotalBytes">Total expected bytes, or <c>null</c> when
/// the server didn't supply <c>Content-Length</c>. Caller should
/// degrade to indeterminate progress when null.</param>
public sealed record DownloadProgress(long BytesReceived, long? TotalBytes)
{
    /// <summary>0-100 integer percent, or <c>null</c> when total is unknown.</summary>
    public int? Percent => TotalBytes is > 0 ? (int)(BytesReceived * 100 / TotalBytes.Value) : null;
}
