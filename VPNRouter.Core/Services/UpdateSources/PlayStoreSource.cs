// Phase 3 — 3F (v3.0 refactor): Play Store IUpdateSource scaffold.
//
// Stub implementation. The Play Store owns its own update lifecycle —
// once we ship through it, the user gets updates via Google Play's
// system Play Protect mechanism on a schedule we don't control. Our
// in-app "check for updates" path should short-circuit (CheckAsync
// returns null), and any caller that tries to drive Download/Apply
// directly is a bug to catch loudly (NotSupportedException, not silent
// no-op).
//
// Phase 4 will wire this against the Play Console / Play In-App Update
// API once the publishing flow lands. The interface contract is stable
// so swapping the stub for the real impl is a single
// PlatformServices.CreateUpdateSource branch change.
//
// Brief: plans/phase3-3F-android-updatesource-2026-05-18.md.

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace VPNRouter.Core.Services.UpdateSources;

/// <summary>
/// Play Store distribution source. Stub: <see cref="CheckAsync"/>
/// returns null (the Play Store self-manages updates);
/// <see cref="DownloadAsync"/> / <see cref="ApplyAsync"/> throw
/// <see cref="NotSupportedException"/>. Phase 4 will replace this with
/// a Play In-App Update API integration.
///
/// <para>
/// <b>Why not just disable the in-app updates UI on the Play Store
/// variant?</b> The Play In-App Update API lets us NUDGE the user to
/// apply a pending Play-managed update from inside the app (a soft
/// banner like the desktop's). When wired in Phase 4,
/// <see cref="CheckAsync"/> will probe the
/// <c>AppUpdateManager.appUpdateInfo</c> call and return a non-null
/// snapshot when an update is available — same contract shape as the
/// other sources. Until then the stub returns null so the in-app UI
/// just stays quiet.
/// </para>
/// </summary>
public sealed class PlayStoreSource : IUpdateSource
{
    /// <inheritdoc />
    public string SourceId => "play-store";

    /// <inheritdoc />
    /// <remarks>
    /// Returns <c>null</c> unconditionally until Phase 4 wires the
    /// Play In-App Update API. Caller treats this as "up-to-date /
    /// no banner to show", which is correct for the
    /// Play-managed-channel case — the Play Store will apply the
    /// update on its own schedule.
    /// </remarks>
    public Task<UpdateSourceInfo?> CheckAsync(CancellationToken ct = default)
    {
        // TODO(Phase 4): wire Play In-App Update API.
        // Reference docs: https://developer.android.com/guide/playcore/in-app-updates
        // Java API class: com.google.android.play.core.appupdate.AppUpdateManager
        // Sketch:
        //   var manager = AppUpdateManagerFactory.create(context);
        //   var info    = await manager.appUpdateInfo;
        //   if (info.updateAvailability() == UpdateAvailability.UPDATE_AVAILABLE)
        //       return new UpdateSourceInfo(...);
        return Task.FromResult<UpdateSourceInfo?>(null);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Always throws — Play Store distribution does not download
    /// arbitrary APKs from our side. The Play Store handles the byte
    /// transfer entirely (and trusts the signing keys it has on file
    /// for our package name). Surfacing as a hard error rather than a
    /// silent no-op so caller bugs show up in QA.
    /// </remarks>
    /// <exception cref="NotSupportedException">Always.</exception>
    public Task<string> DownloadAsync(
        UpdateSourceInfo info,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "PlayStoreSource does not download APKs directly — the Play Store handles asset transfer. " +
            "Phase 4 will route this through the Play In-App Update API (startUpdateFlow). " +
            "Callers should branch on IUpdateSource.SourceId == \"play-store\" to skip the download step.");
    }

    /// <inheritdoc />
    /// <remarks>
    /// Always throws — see <see cref="DownloadAsync"/> rationale. The
    /// Play Store dispatches its own install confirmation UI; our app
    /// never invokes <c>Intent.ActionView</c> on the Play variant.
    /// </remarks>
    /// <exception cref="NotSupportedException">Always.</exception>
    public Task<bool> ApplyAsync(
        UpdateSourceInfo info,
        string stagedPath,
        CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "PlayStoreSource does not apply downloaded APKs — the Play Store handles installation. " +
            "Phase 4 will route this through AppUpdateManager.completeUpdate for flexible flow, " +
            "or no-op for immediate flow (Play takes over the UI).");
    }
}
