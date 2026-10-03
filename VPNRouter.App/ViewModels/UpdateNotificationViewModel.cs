#nullable enable

using System;
using System.Collections.ObjectModel;
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

public partial class UpdateNotificationViewModel : ObservableObject
{
    private const int CheckStateResetDelayMs = 3000;
    private const int StableHistoryLimit = 3;
    private const int CandidateHistoryLimit = 8;

    private readonly UpdateSettings _settings;
    private readonly ILogger _logger;
    private readonly UpdateChecker _updateChecker;
    private readonly IUpdateSource _updateSource;
    private readonly Action<int> _exitApplication;
    private System.Threading.CancellationTokenSource? _resetCheckStateCts;

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private int _downloadProgress;

    public enum UpdateCheckState { Default, Checking, UpToDate, Found, Failed }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckLinkText))]
    private UpdateCheckState _checkState = UpdateCheckState.Default;

    public string CheckLinkText => CheckState switch
    {
        UpdateCheckState.Checking => Strings.Checking,
        UpdateCheckState.UpToDate => Strings.UpToDate,
        UpdateCheckState.Found    => Strings.UpdateAvailableShort,
        UpdateCheckState.Failed   => Strings.CheckFailed,
        _                         => Strings.CheckForUpdates,
    };

    public void NotifyLangChanged()
    {
        OnPropertyChanged(nameof(CheckLinkText));
        OnPropertyChanged(nameof(VersionHistoryButtonText));
        OnPropertyChanged(nameof(VersionHistoryMessage));
        OnPropertyChanged(nameof(RollbackConfirmationText));
        OnPropertyChanged(nameof(ConfirmRollbackText));
        OnPropertyChanged(nameof(CancelRollbackText));
        foreach (var item in StableVersions)
            item.NotifyLangChanged();
    }

    [ObservableProperty] private bool _isChecking;

    public ObservableCollection<RollbackReleaseItemViewModel> StableVersions { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionHistoryButtonText))]
    private bool _isVersionHistoryVisible;

    [ObservableProperty] private bool _isLoadingVersionHistory;

    private enum VersionHistoryState { None, Loading, Ready, Empty, Failed }
    private VersionHistoryState _versionHistoryState;

    public string VersionHistoryMessage => _versionHistoryState switch
    {
        VersionHistoryState.Loading => Strings.LoadingVersions,
        VersionHistoryState.Ready => _settings.IsExperimental ? Strings.RollbackSafetyHintCandidates : Strings.RollbackSafetyHint,
        VersionHistoryState.Empty => Strings.NoOlderVersions,
        VersionHistoryState.Failed => Strings.VersionHistoryFailed,
        _ => string.Empty,
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RollbackConfirmationText))]
    private UpdateSourceInfo? _selectedRollback;

    [ObservableProperty] private bool _isRollbackConfirmationVisible;

    public string VersionHistoryButtonText => IsVersionHistoryVisible
        ? Strings.HideOlderVersions
        : Strings.OtherVersions;

    public string RollbackConfirmationText => SelectedRollback == null
        ? string.Empty
        : string.Format(Strings.RollbackConfirmation, SelectedRollback.Version);
    public string ConfirmRollbackText => Strings.ConfirmRollback;
    public string CancelRollbackText => Strings.Cancel;

    private UpdateSourceInfo? _pendingUpdate;

    // Once an error is shown, a late async status post must not overwrite it.
    private volatile bool _errorLocked;

    public UpdateNotificationViewModel(UpdateSettings settings, ILogger logger)
        : this(settings, logger, updateSource: null, exitApplication: null)
    {
    }

    public UpdateNotificationViewModel(
        UpdateSettings settings,
        ILogger logger,
        IUpdateSource? updateSource,
        Action<int>? exitApplication = null)
    {
        _settings = settings;
        _logger = logger;
        _updateChecker = new UpdateChecker(settings, AppVersion.Version);
        _updateSource = updateSource ?? PlatformServices.CreateUpdateSource(
            settings,
            AppVersion.Version,
            PolicyHttpClient.Shared,
            desktopInstaller: _updateChecker);
        _exitApplication = exitApplication ?? Environment.Exit;

        _updateChecker.DownloadProgress += progress =>
            Dispatcher.UIThread.Post(() => { if (!_errorLocked) DownloadProgress = progress; });

        _updateChecker.StatusChanged += status =>
            Dispatcher.UIThread.Post(() => { if (!_errorLocked) Message = status; });
    }

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

    [RelayCommand]
    private async Task CheckManually()
    {
        if (IsDownloading || IsChecking)
            return;

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
        var sizeMb = _pendingUpdate.AssetSize / 1024.0 / 1024.0;
        Message = string.Format(Strings.UpdateAvailableMessage, _pendingUpdate.Version, sizeMb);
        IsVisible = true;
    }

    [RelayCommand]
    private async Task ToggleVersionHistoryAsync()
    {
        if (IsDownloading || IsLoadingVersionHistory)
            return;

        if (IsVersionHistoryVisible)
        {
            IsVersionHistoryVisible = false;
            IsRollbackConfirmationVisible = false;
            SelectedRollback = null;
            return;
        }

        IsVersionHistoryVisible = true;
        IsLoadingVersionHistory = true;
        SetVersionHistoryState(VersionHistoryState.Loading);
        StableVersions.Clear();
        StableVersions.Add(new RollbackReleaseItemViewModel(
            AppVersion.Version, isInstalled: true, info: null, onSelect: null,
            isPrerelease: AppVersion.Version.Contains('-')));

        try
        {
            var includeCandidates = _settings.IsExperimental;
            var releases = await _updateSource.ListOlderAsync(
                includeCandidates ? CandidateHistoryLimit : StableHistoryLimit, includeCandidates);
            foreach (var release in releases)
            {
                StableVersions.Add(new RollbackReleaseItemViewModel(
                    release.Version,
                    isInstalled: false,
                    release,
                    SelectRollback,
                    release.IsPrerelease));
            }
            SetVersionHistoryState(releases.Count == 0
                ? VersionHistoryState.Empty
                : VersionHistoryState.Ready);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[UpdateVm] Version history load failed");
            SetVersionHistoryState(VersionHistoryState.Failed);
        }
        finally
        {
            IsLoadingVersionHistory = false;
        }
    }

    private void SelectRollback(UpdateSourceInfo info)
    {
        if (IsDownloading || IsLoadingVersionHistory)
            return;
        SelectedRollback = info;
        IsRollbackConfirmationVisible = true;
    }

    private void SetVersionHistoryState(VersionHistoryState state)
    {
        if (_versionHistoryState == state)
            return;
        _versionHistoryState = state;
        OnPropertyChanged(nameof(VersionHistoryMessage));
    }

    [RelayCommand]
    private async Task ConfirmRollbackAsync()
    {
        if (SelectedRollback == null || IsDownloading)
            return;
        _pendingUpdate = SelectedRollback;
        IsRollbackConfirmationVisible = false;
        IsVersionHistoryVisible = false;
        IsVisible = true;
        await DownloadAndApplyAsync();
    }

    [RelayCommand]
    private void CancelRollback()
    {
        SelectedRollback = null;
        IsRollbackConfirmationVisible = false;
    }

    [RelayCommand]
    private async Task DownloadAndApplyAsync()
    {
        var target = _pendingUpdate;
        if (target == null) return;

        _errorLocked = false;
        IsDownloading = true;
        DownloadProgress = 0;
        Message = Strings.UpdateDownloading;

        try
        {
            var extractedDir = await _updateSource.DownloadAsync(target, progress: null).ConfigureAwait(false);

            Message = Strings.UpdateApplying;

            try { OrphanCleanup.KillOrphans(logger: null, respectTunLock: false); } catch { }

            await _updateSource.ApplyAsync(target, extractedDir).ConfigureAwait(false);

            Message = Strings.UpdateRestarting;

            _exitApplication(0);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[UpdateVm] Update failed");
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

public sealed class RollbackReleaseItemViewModel : ObservableObject
{
    public RollbackReleaseItemViewModel(
        string version,
        bool isInstalled,
        UpdateSourceInfo? info,
        Action<UpdateSourceInfo>? onSelect,
        bool isPrerelease = false)
    {
        Version = version;
        IsInstalled = isInstalled;
        IsPrerelease = isPrerelease;
        SelectCommand = new RelayCommand(
            () =>
            {
                if (info != null)
                    onSelect?.Invoke(info);
            },
            () => !isInstalled);
    }

    public string Version { get; }
    public bool IsInstalled { get; }
    public bool IsPrerelease { get; }
    public string DisplayVersion => IsPrerelease ? $"v{Version}  ·  {Strings.PrereleaseLabel}" : $"v{Version}";
    public string StateText => IsInstalled ? Strings.InstalledVersion : Strings.RollbackAction;
    public IRelayCommand SelectCommand { get; }

    internal void NotifyLangChanged()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(DisplayVersion));
    }
}
