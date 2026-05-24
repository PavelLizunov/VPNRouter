// Phase 4 (Wave 18, 2026-05-18) — UpdateNotificationViewModel now drives
// IUpdateSource.CheckAsync / DownloadAsync / ApplyAsync directly instead
// of the legacy UpdateChecker.CheckForUpdateAsync flow. The underlying
// UpdateChecker stays alive as the IDesktopInstaller adapter (download
// staging + helper.cmd dispatch + StatusChanged / DownloadProgress
// events) — only the entry-point surface migrates. Brief:
// plans/phase4-iupdatesource-callers-2026-05-18.md.

#nullable enable

using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Platform;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.UpdateSources;
using VPNRouter.App.Localization;

namespace VPNRouter.App.ViewModels;

/// <summary>
/// Manages auto-update UI: background check on startup, manual check link,
/// download progress, and apply-and-restart flow. Cross-platform — drives
/// the platform-appropriate <see cref="IUpdateSource"/> built by
/// <see cref="PlatformServices.CreateUpdateSource"/>. The desktop
/// <see cref="UpdateChecker"/> still owns the staging + helper.cmd / ditto
/// / pkexec dispatch under <see cref="IDesktopInstaller"/>; this VM just
/// listens to its <see cref="UpdateChecker.StatusChanged"/> /
/// <see cref="UpdateChecker.DownloadProgress"/> event surface for UI
/// feedback while the source drives the lifecycle.
/// </summary>
public partial class UpdateNotificationViewModel : ObservableObject
{
    // v2.37.0-r12 — magic-number + leak fix. Pre-r12 the manual check
    // finally block fire-and-forgot a 3000ms reset. Named constant +
    // CTS swap pattern (mirroring MVM.SetRulesToast VM-10 fix).
    private const int CheckStateResetDelayMs = 3000;

    private readonly UpdateSettings _settings;
    private readonly ILogger _logger;
    private readonly UpdateChecker _updateChecker;
    private readonly IUpdateSource _updateSource;
    private System.Threading.CancellationTokenSource? _resetCheckStateCts;

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private int _downloadProgress;

    /// <summary>v2.30.7-r3 — UpdateCheck state enum replaces the previous
    /// `_checkLinkText` string field. Old design stored the localized
    /// string verbatim, which meant the value was frozen at app start
    /// and didn't refresh when the user toggled RU/EN. New: state is
    /// language-agnostic; CheckLinkText is computed from current
    /// Strings.X getters → re-evaluated on RefreshLocalization.</summary>
    public enum UpdateCheckState { Default, Checking, UpToDate, Found, Failed }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckLinkText))]
    private UpdateCheckState _checkState = UpdateCheckState.Default;

    /// <summary>Localized button label for the manual check action.
    /// Computed from <see cref="CheckState"/> + current Strings.Lang.</summary>
    public string CheckLinkText => CheckState switch
    {
        UpdateCheckState.Checking => Strings.Checking,
        UpdateCheckState.UpToDate => Strings.UpToDate,
        UpdateCheckState.Found    => Strings.UpdateAvailableShort,
        UpdateCheckState.Failed   => Strings.CheckFailed,
        _                         => Strings.CheckForUpdates,
    };

    /// <summary>Re-fire CheckLinkText when language flips. Called from
    /// MainWindowViewModel.RefreshLocalization.</summary>
    public void NotifyLangChanged() => OnPropertyChanged(nameof(CheckLinkText));

    [ObservableProperty] private bool _isChecking;

    private UpdateSourceInfo? _pendingUpdate;

    // v2.22.2-r2: race guard. StatusChanged posts to the UI thread async;
    // if the download throws mid-flight, the catch-block's "Update failed"
    // message can land BEFORE a pending "Extracting update..." Post runs
    // — so the UI ends up stuck showing the old status forever while the
    // real error is overwritten. Flipping this flag before setting the
    // error message makes the status handler drop late-arriving posts.
    private volatile bool _errorLocked;

    /// <summary>
    /// Production ctor — builds the platform-appropriate
    /// <see cref="IUpdateSource"/> via
    /// <see cref="PlatformServices.CreateUpdateSource"/> with the desktop
    /// <see cref="UpdateChecker"/> wired in as the
    /// <see cref="IDesktopInstaller"/> adapter.
    /// </summary>
    public UpdateNotificationViewModel(UpdateSettings settings, ILogger logger)
        : this(settings, logger, updateSource: null)
    {
    }

    /// <summary>
    /// Test / DI ctor — caller supplies a custom
    /// <see cref="IUpdateSource"/> (typically <c>FakeUpdateSource</c> in
    /// tests). When <paramref name="updateSource"/> is null, the
    /// production wiring path is used.
    /// </summary>
    public UpdateNotificationViewModel(UpdateSettings settings, ILogger logger, IUpdateSource? updateSource)
    {
        _settings = settings;
        _logger = logger;
        _updateChecker = new UpdateChecker(settings, AppVersion.Version);
        _updateSource = updateSource ?? PlatformServices.CreateUpdateSource(
            settings,
            AppVersion.Version,
            PolicyHttpClient.Shared,
            desktopInstaller: _updateChecker);

        // v2.30.7-r3 — _checkLinkText init removed; CheckLinkText is now
        // a computed property that derives from CheckState + current Strings.

        // UpdateChecker still raises these events while wired in as the
        // IDesktopInstaller adapter (GitHubReleaseSource.DownloadAsync
        // delegates to UpdateChecker.DownloadAndStageAsync under the hood,
        // which fires StatusChanged/DownloadProgress mid-flight). The VM
        // listens here so the UI banner stays in lock-step with byte-level
        // progress + status transitions without the source contract
        // needing its own event surface.
        _updateChecker.DownloadProgress += progress =>
            Dispatcher.UIThread.Post(() => { if (!_errorLocked) DownloadProgress = progress; });

        _updateChecker.StatusChanged += status =>
            Dispatcher.UIThread.Post(() => { if (!_errorLocked) Message = status; });
    }

    /// <summary>
    /// Background check called on app startup. Silent fail (no UI feedback
    /// if no update or check fails).
    /// </summary>
    public async Task CheckOnStartupAsync()
    {
        try
        {
            _updateChecker.CleanupStagingDir();
            var info = await _updateSource.CheckAsync().ConfigureAwait(false);
            if (info != null)
            {
                _pendingUpdate = info;
                Dispatcher.UIThread.Post(ShowUpdateNotification);
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[UpdateVm] Background update check failed");
        }
    }

    /// <summary>
    /// Manual "Check for updates" link click. Shows feedback in link text.
    /// </summary>
    [RelayCommand]
    private async Task CheckManually()
    {
        IsChecking = true;
        CheckState = UpdateCheckState.Checking;
        try
        {
            var info = await _updateSource.CheckAsync().ConfigureAwait(false);
            if (info != null)
            {
                _pendingUpdate = info;
                ShowUpdateNotification();
                CheckState = UpdateCheckState.Found;
            }
            else
            {
                CheckState = UpdateCheckState.UpToDate;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[UpdateVm] Manual check failed");
            CheckState = UpdateCheckState.Failed;
        }
        finally
        {
            IsChecking = false;
            // v2.37.0-r12 — cancel any prior pending reset before scheduling
            // a new one. Pre-r12 every manual check fire-and-forgot a 3s
            // delayed reset; if the user clicked Check 5× in quick
            // succession, 5 timers raced for the last write. Worst case the
            // CheckState would flicker UpToDate → Default → Failed → Default
            // in a single second. Swap+dispose CTS pattern matches the same
            // fix in MainWindowViewModel.SetRulesToast (v2.31.0-r3 VM-10).
            var oldCts = _resetCheckStateCts;
            _resetCheckStateCts = new System.Threading.CancellationTokenSource();
            var token = _resetCheckStateCts.Token;
            if (oldCts != null)
            {
                try { oldCts.Cancel(); } catch (ObjectDisposedException) { }
                oldCts.Dispose();
            }
            _ = Task.Delay(CheckStateResetDelayMs, token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!token.IsCancellationRequested)
                        CheckState = UpdateCheckState.Default;
                });
            }, TaskScheduler.Default);
        }
    }

    private void ShowUpdateNotification()
    {
        if (_pendingUpdate == null) return;
        // Phase 4 migration: UpdateSourceInfo carries only the full asset
        // size (no lite-update fork). Lite-update is a legacy desktop
        // optimization that lives behind the IDesktopInstaller adapter;
        // the user-facing banner just reports the published asset size.
        var sizeMb = _pendingUpdate.AssetSize / 1024.0 / 1024.0;
        Message = string.Format(Strings.UpdateAvailableMessage, _pendingUpdate.Version, sizeMb);
        IsVisible = true;
    }

    [RelayCommand]
    private async Task DownloadAndApplyAsync()
    {
        if (_pendingUpdate == null) return;

        _errorLocked = false; // reset for a fresh attempt
        IsDownloading = true;
        DownloadProgress = 0;
        Message = Strings.UpdateDownloading;

        try
        {
            // IUpdateSource exposes byte-percent progress via IProgress<DownloadProgress>;
            // the existing UpdateChecker.DownloadProgress event already gives us
            // throttled int-percent updates so we don't need a second sink. Keeping
            // the null progress arg lets GitHubReleaseSource.DownloadAsync skip the
            // extra delegate hop and rely on the legacy event stream.
            var extractedDir = await _updateSource.DownloadAsync(_pendingUpdate, progress: null).ConfigureAwait(false);

            Message = Strings.UpdateApplying;

            // Defensive: kill any orphan sing-box / VPNRouter.GUI processes BEFORE
            // file copy. This ensures the new instance starts clean.
            // (Killing self-process VPNRouter.App.exe is excluded by KillOrphans logic.)
            //
            // v2.31.10-r2: pass respectTunLock: false — the update flow's
            // helper.cmd separately stops the Windows Service before the
            // xcopy, and at this point we WANT the running sing-box gone
            // so file replacement can free wintun handles. If we deferred
            // to TunLock here, Service-spawned sing-box would survive the
            // pre-update sweep and helper.cmd would have to do it later
            // anyway. Mirror existing user-takeover semantics on this path.
            try { OrphanCleanup.KillOrphans(logger: null, respectTunLock: false); } catch { }

            // Apply (replaces files, may rename locked exe to .bak)
            await _updateSource.ApplyAsync(_pendingUpdate, extractedDir).ConfigureAwait(false);

            Message = Strings.UpdateRestarting;

            // ApplyAsync already launched the new exe — just exit gracefully
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[UpdateVm] Update failed");
            // Lock out late StatusChanged/DownloadProgress posts, then apply
            // the error state via the Dispatcher so it runs AFTER any already-
            // queued UI updates. Without the flag-gate + Post ordering, users
            // saw "Extracting update..." stuck forever while the real
            // "Update failed: …" message was silently overwritten.
            _errorLocked = true;
            Dispatcher.UIThread.Post(() =>
            {
                Message = string.Format(Strings.UpdateFailed, ex.Message);
                IsDownloading = false;
                DownloadProgress = 0;
            });
        }
    }

    [RelayCommand]
    private void Dismiss()
    {
        IsVisible = false;
    }
}
