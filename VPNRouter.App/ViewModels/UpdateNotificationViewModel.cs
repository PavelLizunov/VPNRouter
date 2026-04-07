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
    [ObservableProperty] private string _checkLinkText;
    [ObservableProperty] private bool _isChecking;

    private UpdateInfo? _pendingUpdate;

    public UpdateNotificationViewModel(UpdateSettings settings, ILogger logger)
    {
        _settings = settings;
        _logger = logger;
        _updateChecker = new UpdateChecker(settings, AppVersion.Version);
        _checkLinkText = Strings.CheckForUpdates;

        _updateChecker.DownloadProgress += progress =>
            Dispatcher.UIThread.Post(() => DownloadProgress = progress);

        _updateChecker.StatusChanged += status =>
            Dispatcher.UIThread.Post(() => Message = status);
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
        CheckLinkText = Strings.Checking;
        try
        {
            var info = await _updateChecker.CheckForUpdateAsync();
            if (info != null)
            {
                _pendingUpdate = info;
                ShowUpdateNotification();
                CheckLinkText = Strings.UpdateAvailableShort;
            }
            else
            {
                CheckLinkText = Strings.UpToDate;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[UpdateVm] Manual check failed");
            CheckLinkText = Strings.CheckFailed;
        }
        finally
        {
            IsChecking = false;
            // Reset link text after 3 seconds
            _ = Task.Delay(3000).ContinueWith(_ =>
                Dispatcher.UIThread.Post(() => CheckLinkText = Strings.CheckForUpdates));
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
            Message = string.Format(Strings.UpdateFailed, ex.Message);
            IsDownloading = false;
            DownloadProgress = 0;
        }
    }

    [RelayCommand]
    private void Dismiss()
    {
        IsVisible = false;
    }
}
