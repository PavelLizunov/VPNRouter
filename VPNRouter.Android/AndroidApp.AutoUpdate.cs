using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Threading;
using System.Threading.Tasks;
using Orientation = Avalonia.Layout.Orientation;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Platform;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.UpdateSources;

namespace VPNRouter.Android;

public partial class AndroidApp
{
    private IUpdateSource? _updateSource;

    private string? _updateSourceChannel;

    private IUpdateSource GetOrBuildUpdateSource()
    {
        var channel = AndroidStorage.GetUpdateChannel();
        if (_updateSource is not null &&
            string.Equals(_updateSourceChannel, channel, StringComparison.Ordinal))
        {
            return _updateSource;
        }

        var settings = new UpdateSettings
        {
            GitHubRepo = "PavelLizunov/VPNRouter",
            Channel = channel,
        };
        _updateSource = PlatformServices.CreateUpdateSource(
            settings,
            AppVersion.Version,
            PolicyHttpClient.Shared,
            androidInstaller: new AndroidInstallerAdapter());
        _updateSourceChannel = channel;
        return _updateSource;
    }

    private void BuildUpdateBanner(double radiusMd)
    {
        _updateBannerTitle = new TextBlock
        {
            Text = string.Empty,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        _updateBannerTitle.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _updateBannerSubtitle = new TextBlock
        {
            Text = string.Empty,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        _updateBannerSubtitle.BindToken(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        _updateBannerAction = new Avalonia.Controls.Button
        {
            Content = Localization.UpdateButtonDownload,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(14, 6),
            CornerRadius = new CornerRadius(radiusMd),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _updateBannerAction.BindToken(Avalonia.Controls.Button.BackgroundProperty, "AccentSolidBrush");
        _updateBannerAction.BindToken(Avalonia.Controls.Button.ForegroundProperty, "AccentOnSolidBrush");
        _updateBannerAction.Click += OnUpdateBannerActionClicked;

        _updateBannerDismiss = new Avalonia.Controls.Button
        {
            Content = Localization.UpdateButtonDismiss,
            FontSize = 12,
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(radiusMd),
            Background = Avalonia.Media.Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _updateBannerDismiss.BindToken(Avalonia.Controls.Button.ForegroundProperty, "TextSecondaryBrush");
        _updateBannerDismiss.Click += OnUpdateBannerDismissClicked;

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { _updateBannerDismiss, _updateBannerAction },
        };

        var bannerStack = new StackPanel
        {
            Spacing = 0,
            Children = { _updateBannerTitle, _updateBannerSubtitle, buttonRow },
        };

        _updateBanner = new Border
        {
            Padding = new Thickness(14, 12),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radiusMd),
            Child = bannerStack,
            IsVisible = false,
        };
        _updateBanner.BindToken(Border.BackgroundProperty, "AccentBgSubtleBrush");
        _updateBanner.BindToken(Border.BorderBrushProperty, "BorderAccentBrush");
    }

    private async Task RunUpdateCheckAsync(bool manual)
    {
        if (_updateInFlight)
            return;
        _updateInFlight = true;

        if (manual)
            ShowMenuFeedback(Localization.UpdateCheckChecking);

        try
        {
            var source = GetOrBuildUpdateSource();
            var info = await source.CheckAsync().ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (info is null)
                {
                    if (manual)
                        ShowMenuFeedback(Localization.UpdateCheckUpToDate);
                    return;
                }
                PromptUpdateAvailable(info);
            }).GetTask().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                ShowMenuFeedback(string.Format(Localization.UpdateCheckFailed, ex.Message)))
                .GetTask().ConfigureAwait(false);
        }
        finally
        {
            _updateInFlight = false;
        }
    }

    private void PromptUpdateAvailable(UpdateSourceInfo info)
    {
        _pendingUpdate = info;
        _downloadedApkPath = null;
        if (_updateBannerTitle is not null)
        {
            var sizeMb = info.AssetSize / 1024.0 / 1024.0;
            _updateBannerTitle.Text = string.Format(Localization.UpdateBannerTitle,
                info.Version, sizeMb);
        }
        if (_updateBannerSubtitle is not null)
        {
            _updateBannerSubtitle.Text = Localization.UpdateBannerSubtitle;
            _updateBannerSubtitle.IsVisible = true;
        }
        if (_updateBannerAction is not null)
        {
            _updateBannerAction.Content = Localization.UpdateButtonDownload;
            _updateBannerAction.IsEnabled = true;
        }
        if (_updateBanner is not null)
            _updateBanner.IsVisible = true;
    }

    private void OnUpdateBannerActionClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_updateBannerAction is null) return;
        var label = _updateBannerAction.Content as string;

        if (string.Equals(label, Localization.UpdateButtonGrantPermission, StringComparison.Ordinal))
        {
            if (AndroidUpdater.CanRequestInstall())
            {
                if (_updateBannerAction is not null)
                    _updateBannerAction.Content = Localization.UpdateButtonInstall;
                if (_updateBannerSubtitle is not null)
                    _updateBannerSubtitle.IsVisible = false;
                HandleInstallClick();
                return;
            }
            AndroidUpdater.RequestInstallPermission();
            if (_updateBannerSubtitle is not null)
            {
                _updateBannerSubtitle.Text = Localization.UpdateInstallPermissionGranted;
                _updateBannerSubtitle.IsVisible = true;
            }
            return;
        }

        if (string.Equals(label, Localization.UpdateButtonInstall, StringComparison.Ordinal))
        {
            HandleInstallClick();
            return;
        }

        _ = DownloadAndInstallAsync();
    }

    private async Task DownloadAndInstallAsync()
    {
        var info = _pendingUpdate;
        if (info is null) return;
        if (_updateInFlight) return;
        _updateInFlight = true;

        try
        {
            if (_updateBannerAction is not null) _updateBannerAction.IsEnabled = false;
            if (_updateBannerSubtitle is not null) _updateBannerSubtitle.IsVisible = false;

            var progress = new Progress<DownloadProgress>(p =>
            {
                if (_updateBannerTitle is not null)
                {
                    var pct = p.Percent ?? 0;
                    _updateBannerTitle.Text = string.Format(Localization.UpdateDownloading, pct);
                }
            });

            var source = GetOrBuildUpdateSource();
            var apkPath = await source.DownloadAsync(info, progress).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _downloadedApkPath = apkPath;
                if (_updateBannerTitle is not null)
                    _updateBannerTitle.Text = Localization.UpdateDownloadDone;
                if (_updateBannerAction is not null)
                {
                    _updateBannerAction.Content = Localization.UpdateButtonInstall;
                    _updateBannerAction.IsEnabled = true;
                }
                HandleInstallClick();
            }).GetTask().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_updateBannerTitle is not null)
                    _updateBannerTitle.Text = string.Format(Localization.UpdateDownloadFailed, ex.Message);
                if (_updateBannerAction is not null)
                {
                    _updateBannerAction.Content = Localization.UpdateButtonRetry;
                    _updateBannerAction.IsEnabled = true;
                }
            }).GetTask().ConfigureAwait(false);
        }
        finally
        {
            _updateInFlight = false;
        }
    }

    private void HandleInstallClick()
    {
        if (string.IsNullOrEmpty(_downloadedApkPath))
            return;

        if (!AndroidUpdater.CanRequestInstall())
        {
            if (_updateBannerTitle is not null)
                _updateBannerTitle.Text = Localization.UpdateInstallPermissionNeeded;
            if (_updateBannerSubtitle is not null)
            {
                _updateBannerSubtitle.Text = string.Empty;
                _updateBannerSubtitle.IsVisible = false;
            }
            if (_updateBannerAction is not null)
                _updateBannerAction.Content = Localization.UpdateButtonGrantPermission;
            return;
        }

        var info = _pendingUpdate;
        if (info is null)
            return;

        _ = LaunchInstallAsync(info, _downloadedApkPath);
    }

    private async Task LaunchInstallAsync(UpdateSourceInfo info, string apkPath)
    {
        try
        {
            var source = GetOrBuildUpdateSource();
            var dispatched = await source.ApplyAsync(info, apkPath).ConfigureAwait(false);
            if (!dispatched)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_updateBannerTitle is not null)
                        _updateBannerTitle.Text = Localization.UpdateInstallLaunchFailed;
                    if (_updateBannerAction is not null)
                        _updateBannerAction.Content = Localization.UpdateButtonRetry;
                }).GetTask().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_updateBannerTitle is not null)
                    _updateBannerTitle.Text = string.Format(Localization.UpdateDownloadFailed, ex.Message);
                if (_updateBannerAction is not null)
                    _updateBannerAction.Content = Localization.UpdateButtonRetry;
            }).GetTask().ConfigureAwait(false);
        }
    }

    private void OnUpdateBannerDismissClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_updateBanner is not null)
            _updateBanner.IsVisible = false;
        _pendingUpdate = null;
        _downloadedApkPath = null;
    }
}
