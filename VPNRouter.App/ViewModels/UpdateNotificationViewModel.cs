using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.App.Localization;

namespace VPNRouter.App.ViewModels;

/// <summary>
/// Manages auto-update UI: background check on startup, manual check link,
/// download progress, and apply-and-restart flow. Cross-platform — wraps
/// VPNRouter.Core.Services.UpdateChecker which handles win/mac asset selection.
/// </summary>
public partial class UpdateNotificationViewModel : ObservableObject
{
    private readonly UpdateSettings _settings;
    private readonly ILogger _logger;
    private readonly UpdateChecker _updateChecker;

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

    private UpdateInfo? _pendingUpdate;

    // v2.22.2-r2: race guard. StatusChanged posts to the UI thread async;
    // if the download throws mid-flight, the catch-block's "Update failed"
    // message can land BEFORE a pending "Extracting update..." Post runs
    // — so the UI ends up stuck showing the old status forever while the
    // real error is overwritten. Flipping this flag before setting the
    // error message makes the status handler drop late-arriving posts.
    private volatile bool _errorLocked;

    public UpdateNotificationViewModel(UpdateSettings settings, ILogger logger)
    {
        _settings = settings;
        _logger = logger;
        _updateChecker = new UpdateChecker(settings, AppVersion.Version);
        // v2.30.7-r3 — _checkLinkText init removed; CheckLinkText is now
        // a computed property that derives from CheckState + current Strings.

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
            var info = await _updateChecker.CheckForUpdateAsync();
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
            var info = await _updateChecker.CheckForUpdateAsync();
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
            // Reset link text after 3 seconds
            _ = Task.Delay(3000).ContinueWith(_ =>
                Dispatcher.UIThread.Post(() => CheckState = UpdateCheckState.Default));
        }
    }

    private void ShowUpdateNotification()
    {
        if (_pendingUpdate == null) return;
        var sizeMb = (_pendingUpdate.HasLiteUpdate ? _pendingUpdate.LiteSizeBytes : _pendingUpdate.SizeBytes) / 1024.0 / 1024.0;
        Message = string.Format(Strings.UpdateAvailableMessage, _pendingUpdate.LatestVersion, sizeMb);
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
            var extractedDir = await _updateChecker.DownloadAndStageAsync(_pendingUpdate);

            Message = Strings.UpdateApplying;

            // Defensive: kill any orphan sing-box / VPNRouter.GUI processes BEFORE
            // file copy. This ensures the new instance starts clean.
            // (Killing self-process VPNRouter.App.exe is excluded by KillOrphans logic.)
            try { OrphanCleanup.KillOrphans(); } catch { }

            // Apply (replaces files, may rename locked exe to .bak)
            _updateChecker.ApplyUpdate(extractedDir);

            Message = Strings.UpdateRestarting;

            // ApplyUpdate already launched the new exe — just exit gracefully
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
