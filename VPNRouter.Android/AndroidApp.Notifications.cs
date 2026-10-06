using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Linq;
using UiIcons = VPNRouter.Core.Services.UiIcons;

namespace VPNRouter.Android;

public partial class AndroidApp
{
    private const int MenuFeedbackDismissMs = 3000;

    private void ConsumeAndSurfaceRecoveryNotice()
    {
        var coreNotice = VPNRouter.Core.Services.SettingsLoader.ConsumeRecoveryNotice();
        var androidNotice = AndroidStorage.ConsumeRecoveryNotice();
        var safeMode = AndroidStorage.ConsumeSafeModeBanner();
        var placeholderCount = AndroidStorage.GetPlaceholderPruneCount();

        var parts = new System.Collections.Generic.List<string>(4);
        if (!string.IsNullOrWhiteSpace(coreNotice)) parts.Add(coreNotice);
        if (!string.IsNullOrWhiteSpace(androidNotice)) parts.Add(androidNotice);
        if (placeholderCount > 0)
        {
            var anyServerLeft =
                (AndroidStorage.GetServers().Count > 0) ||
                AndroidStorage.GetSubscriptions().Any(s => s?.Servers?.Count > 0);
            parts.Add(anyServerLeft
                ? string.Format(Localization.PlaceholderPruneBanner, placeholderCount)
                : Localization.PlaceholderPruneBannerAllGone);
            AndroidStorage.ClearPlaceholderPruneCount();
        }
        if (safeMode)
        {
            parts.Add(Localization.Ru
                ? "Если проблемы продолжаются: Настройки > Приложения > VPNRouter > Хранилище > Очистить данные."
                : "If problems persist: Settings > Apps > VPNRouter > Storage > Clear data.");
        }

        if (parts.Count == 0) return;
        var combined = string.Join(" — ", parts);
        ShowMenuFeedback(combined);
    }

    private Border BuildLogOverlay()
    {
        _logViewerTitle = new TextBlock
        {
            Text = "singbox.log",
            FontSize = UiScale.Fs(13),
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _logViewerTitle.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _logViewerCloseBtn = new Avalonia.Controls.Button
        {
            Content = IconContent(UiIcons.X, 22),
            Width = 44,
            Height = 44,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };
        _logViewerCloseBtn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "TextSecondaryBrush");
        _logViewerCloseBtn.Click += OnLogViewerCloseClicked;

        _logViewerRefreshBtn = new Avalonia.Controls.Button
        {
            Content = IconContent(UiIcons.RefreshCw, 20),
            Width = 44,
            Height = 44,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };
        _logViewerRefreshBtn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "TextSecondaryBrush");
        _logViewerRefreshBtn.Click += OnLogViewerRefreshClicked;

        var titleBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(8, 4, 4, 4),
        };
        Grid.SetColumn(_logViewerTitle, 0);
        Grid.SetColumn(_logViewerRefreshBtn, 1);
        Grid.SetColumn(_logViewerCloseBtn, 2);
        _logViewerRefreshBtn.HorizontalAlignment = HorizontalAlignment.Right;
        titleBar.Children.Add(_logViewerTitle);
        titleBar.Children.Add(_logViewerRefreshBtn);
        titleBar.Children.Add(_logViewerCloseBtn);

        var titleBarBorder = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 4),
            Child = titleBar,
        };
        titleBarBorder.BindToken(Border.BackgroundProperty, "SurfaceRaisedBrush");
        titleBarBorder.BindToken(Border.BorderBrushProperty, "BorderSubtleBrush");

        _logViewerContent = new TextBlock
        {
            FontFamily = new FontFamily("monospace"),
            FontSize = UiScale.Fs(9),
            TextWrapping = TextWrapping.NoWrap,
            Padding = new Thickness(8),
        };
        _logViewerContent.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _logViewerEmptyState = new TextBlock
        {
            FontSize = UiScale.Fs(12),
            Text = string.Empty,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(24),
            IsVisible = false,
        };
        _logViewerEmptyState.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        _logViewerScroller = new ScrollViewer
        {
            Content = _logViewerContent,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _logViewerScroller.BindToken(ScrollViewer.BackgroundProperty, "SurfaceAppBrush");

        var contentArea = new Grid
        {
            Children = { _logViewerScroller, _logViewerEmptyState }
        };

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(titleBarBorder, Dock.Top);
        dock.Children.Add(titleBarBorder);
        dock.Children.Add(contentArea);

        var overlay = new Border
        {
            IsVisible = false,
            Child = dock,
        };
        overlay.BindToken(Border.BackgroundProperty, "SurfaceAppBrush");
        return overlay;
    }

    private void OnMenuOpenLogClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        ShowLogViewer();
    }

    private void ShareDiagnosticsZip(string zipPath, string name)
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(
                ctx, ctx.PackageName + ".fileprovider", new Java.IO.File(zipPath));
            var send = new global::Android.Content.Intent(global::Android.Content.Intent.ActionSend);
            send.SetType("application/zip");
            send.PutExtra(global::Android.Content.Intent.ExtraStream, uri);
            send.AddFlags(global::Android.Content.ActivityFlags.GrantReadUriPermission);
            var chooser = global::Android.Content.Intent.CreateChooser(send, name);
            chooser!.AddFlags(global::Android.Content.ActivityFlags.NewTask);
            ctx.StartActivity(chooser);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter", $"AND-DIAG-SHARE: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async void OnMenuExportDiagClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        try
        {
            var connected = MainActivity.IntendedConnected;
            var configMode = AndroidStorage.GetConfigMode();

            var result = await System.Threading.Tasks.Task.Run(() =>
            {
                int serverCount;
                try { serverCount = AndroidStorage.GetServers()?.Count ?? 0; } catch { serverCount = 0; }
                return AndroidDiagnosticsExporter.Export(
                    System.DateTime.Now, connected, configMode, serverCount);
            });

            if (result.ZipPath is not null)
            {
                var name = System.IO.Path.GetFileName(result.ZipPath);
                ShowMenuFeedback($"{result.Entries.Count} files → {name}");
                ShareDiagnosticsZip(result.ZipPath, name);
                global::Android.Util.Log.Info("VpnRouter",
                    $"AND-DIAG-EXPORT: wrote {result.ZipPath} ({result.Entries.Count} entries, {result.Warnings.Count} warnings)");
            }
            else
            {
                ShowMenuFeedback("Diagnostics export failed");
                global::Android.Util.Log.Warn("VpnRouter",
                    $"AND-DIAG-EXPORT: failed — {string.Join("; ", result.Warnings)}");
            }
        }
        catch (System.Exception ex)
        {
            ShowMenuFeedback("Diagnostics export error");
            global::Android.Util.Log.Warn("VpnRouter",
                $"AND-DIAG-EXPORT: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ShowLogViewer()
    {
        if (_logOverlay is null) return;
        if (_logViewerTitle is not null) _logViewerTitle.Text = "singbox.log";
        if (_logViewerRefreshBtn is not null) _logViewerRefreshBtn.IsVisible = true;
        LoadLogContent();
        _logOverlay.IsVisible = true;
    }



    private void OnLogViewerCloseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_logOverlay is not null) _logOverlay.IsVisible = false;
    }

    private void OnLogViewerRefreshClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        LoadLogContent();
    }

    private void LoadLogContent()
    {
        if (_logViewerContent is null) return;
        try
        {
            var logPath = AndroidDiagnosticsExporter.ResolveSingboxLogPath();

            if (logPath is null || !System.IO.File.Exists(logPath))
            {
                ShowLogEmptyState(Localization.LogViewerEmpty);
                return;
            }

            const int MaxBytes = 50_000;
            string text;
            using (var fs = System.IO.File.Open(logPath, System.IO.FileMode.Open,
                                                System.IO.FileAccess.Read,
                                                System.IO.FileShare.ReadWrite))
            {
                if (fs.Length <= MaxBytes)
                {
                    using var sr = new System.IO.StreamReader(fs);
                    text = sr.ReadToEnd();
                }
                else
                {
                    fs.Seek(-MaxBytes, System.IO.SeekOrigin.End);
                    using var sr = new System.IO.StreamReader(fs);
                    sr.ReadLine();
                    text = sr.ReadToEnd();
                }
            }

            if (string.IsNullOrEmpty(text))
            {
                ShowLogEmptyState(Localization.LogViewerEmpty);
                return;
            }

            _logViewerContent.Text = text;
            if (_logViewerEmptyState is not null) _logViewerEmptyState.IsVisible = false;
            if (_logViewerScroller is not null)
            {
                _logViewerScroller.IsVisible = true;
                Dispatcher.UIThread.Post(() =>
                {
                    if (_logViewerScroller is null) return;
                    _logViewerScroller.Offset = new Vector(
                        _logViewerScroller.Offset.X,
                        _logViewerScroller.Extent.Height);
                }, DispatcherPriority.Background);
            }
        }
        catch (Exception ex)
        {
            ShowLogEmptyState(string.Format(Localization.LogViewerError,
                ex.GetType().Name, ex.Message));
        }
    }

    private void ShowLogEmptyState(string message)
    {
        if (_logViewerEmptyState is not null)
        {
            _logViewerEmptyState.Text = message;
            _logViewerEmptyState.IsVisible = true;
        }
        if (_logViewerScroller is not null) _logViewerScroller.IsVisible = false;
    }

    private void CopyToClipboard(string label, string text)
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            var clipboard = ctx.GetSystemService(global::Android.Content.Context.ClipboardService)
                            as global::Android.Content.ClipboardManager;
            if (clipboard is null) return;
            var clip = global::Android.Content.ClipData.NewPlainText(label, text);
            clipboard.PrimaryClip = clip;
        }
        catch
        {
        }
    }

    private async void ShowMenuFeedback(string text)
    {
        if (_menuFeedback is null) return;
        _menuFeedback.Text = text;
        _menuFeedback.IsVisible = true;
        try
        {
            await System.Threading.Tasks.Task.Delay(MenuFeedbackDismissMs);
            if (_menuFeedback is not null && _menuFeedback.Text == text)
            {
                _menuFeedback.IsVisible = false;
            }
        }
        catch { }
    }
}
