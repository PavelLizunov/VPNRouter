// Phase 4 (Wave 18, 2026-05-18) — Android auto-update plumbing now drives
// IUpdateSource (SideloadSource concrete) instead of calling static
// AndroidUpdater methods. AndroidUpdater stays alive for the
// platform-specific permission gates (CanRequestInstall /
// RequestInstallPermission) + low-level APK download helpers that the
// AndroidInstallerAdapter wraps onto IAndroidInstaller. Brief:
// plans/phase4-iupdatesource-callers-2026-05-18.md.

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

/// <summary>
/// v2.32.0 (2026-05-07) — Android auto-update UI plumbing. Mirrors
/// desktop's <c>UpdateNotificationViewModel</c> + the slim XAML banner
/// it drives. Sits in a partial class so the (already huge) main
/// <c>AndroidApp.axaml.cs</c> doesn't grow further.
///
/// <para>Phase 4 (Wave 18, 2026-05-18): migrated from static
/// <see cref="AndroidUpdater"/> calls to <see cref="IUpdateSource"/>
/// (concrete <see cref="SideloadSource"/>). The Android permission
/// gates (<c>CanRequestInstall</c> / <c>RequestInstallPermission</c>)
/// stay as static helpers on <see cref="AndroidUpdater"/> — those are
/// platform-only and don't belong on the cross-platform contract.</para>
///
/// <para>Flow:</para>
/// <list type="number">
///   <item>User taps kebab > Diagnostics > "Check for updates" or
///   Settings > Updates > "Check for updates" → both call
///   <see cref="RunUpdateCheckAsync"/>.</item>
///   <item><see cref="IUpdateSource.CheckAsync"/> hits GitHub, returns
///   <see cref="UpdateSourceInfo"/>?.</item>
///   <item>Result null → "you're up to date" toast via the kebab
///   feedback banner. Result non-null → <see cref="PromptUpdateAvailable"/>
///   surfaces the persistent update banner above the config row.</item>
///   <item>User taps "Download" → <see cref="DownloadAndInstallAsync"/>
///   streams the APK with progress via
///   <see cref="IUpdateSource.DownloadAsync"/> (SHA256-verified inside
///   the source before return), then calls
///   <see cref="IUpdateSource.ApplyAsync"/>. If the system blocks for
///   missing <c>REQUEST_INSTALL_PACKAGES</c>, banner flips to "Allow"
///   → opens Settings deep-link via <see cref="AndroidUpdater.RequestInstallPermission"/>.</item>
///   <item>User grants → returns to app → on next manual tap of
///   "Install" the system PackageInstaller dialog opens, user
///   confirms, OS swaps the APK, app restarts on the new version.</item>
/// </list>
/// </summary>
public partial class AndroidApp
{
    /// <summary>
    /// Lazily-built sideload <see cref="IUpdateSource"/> reflecting the
    /// current persisted update channel. Rebuilt when the channel flips
    /// (stable ↔ experimental) so the prerelease gate stays in sync.
    /// </summary>
    private IUpdateSource? _updateSource;

    /// <summary>Channel snapshot used to build <see cref="_updateSource"/>.
    /// Tracked so we know when to discard + rebuild.</summary>
    private string? _updateSourceChannel;

    /// <summary>
    /// Build (or rebuild) <see cref="_updateSource"/> from the current
    /// persisted channel. Returns the live instance.
    /// </summary>
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

    /// <summary>
    /// Build the always-present-but-hidden update-banner Border + its
    /// children. Inserted into the inner stack at app-init time and
    /// flipped <see cref="Visual.IsVisibleProperty"/> on/off as state
    /// changes. Style mirrors the desktop's UpdateNotification card —
    /// AccentBgSubtle background, AccentBorder, RadiusMd, title +
    /// subtitle + 2-button row.
    /// </summary>
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

    /// <summary>
    /// Hit GitHub via <see cref="IUpdateSource.CheckAsync"/> using the
    /// channel pulled from <see cref="AndroidStorage.GetUpdateChannel"/>.
    /// Surfaces "checking…" / "up to date" / error in the kebab feedback
    /// banner; if a newer release exists, calls
    /// <see cref="PromptUpdateAvailable"/> to render the persistent
    /// update banner.
    /// </summary>
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

    /// <summary>
    /// Surface the persistent banner with version + size + Download
    /// button. Caches <paramref name="info"/> so the subsequent action
    /// click can pass it to <see cref="IUpdateSource.DownloadAsync"/>.
    /// </summary>
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

    /// <summary>
    /// Action button click router. Dispatches based on the button's
    /// current label rather than a separate state field — saves a field
    /// + the label uniquely identifies which step we're on (Download
    /// vs Install vs Allow vs Retry).
    /// </summary>
    private void OnUpdateBannerActionClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_updateBannerAction is null) return;
        var label = _updateBannerAction.Content as string;

        if (string.Equals(label, Localization.UpdateButtonGrantPermission, StringComparison.Ordinal))
        {
            // User tapped "Allow". Two cases:
            //   (a) First tap — permission still missing → deep-link to
            //       Settings and update subtitle to remind them to come
            //       back and re-tap. Button label stays "Allow" so a
            //       second tap re-runs this branch (catching them after
            //       returning from Settings).
            //   (b) Second tap (back in the app, permission now granted
            //       in Settings) — re-check, and if true, flip back to
            //       Install and fire HandleInstallClick. Saves the user
            //       a third tap.
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

        // Default — Download / Retry both kick off the download flow.
        _ = DownloadAndInstallAsync();
    }

    /// <summary>
    /// Stream the APK from <see cref="_pendingUpdate"/> into the cache
    /// dir, updating the banner title with byte-percent progress as we
    /// go. On completion flips the banner to "Install" state. On
    /// failure flips to "Retry" with the error message.
    /// </summary>
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
                // Auto-trigger install — the user already chose to
                // update. If permission is missing the helper flips
                // the banner to "Allow" mode.
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

    /// <summary>
    /// Hand the downloaded APK to the system PackageInstaller (or
    /// re-prompt the user to grant <c>REQUEST_INSTALL_PACKAGES</c>
    /// first). Idempotent — re-tapping after a permission grant just
    /// re-launches the install intent.
    /// </summary>
    private void HandleInstallClick()
    {
        if (string.IsNullOrEmpty(_downloadedApkPath))
            return;

        // Permission gate — on API 26+ we need REQUEST_INSTALL_PACKAGES.
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

    /// <summary>
    /// Fire-and-forget wrapper around <see cref="IUpdateSource.ApplyAsync"/>
    /// — we don't await the result inline because the banner stays
    /// visible while the system PackageInstaller dialog drives the
    /// rest of the flow asynchronously. If the dispatch fails (rare —
    /// missing permission usually surfaces via CanRequestInstall above)
    /// we flip the banner to Retry state.
    /// </summary>
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
            // BeginInstall succeeded → system installer dialog is up. Leave
            // the banner alone; if user cancels the install they can re-tap.
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
