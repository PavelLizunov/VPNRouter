using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Linq;

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
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _logViewerTitle.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _logViewerCloseBtn = new Avalonia.Controls.Button
        {
            Content = "✕",
            FontSize = 16,
            Width = 36,
            Height = 36,
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
            Content = "⟳",
            FontSize = 16,
            Width = 36,
            Height = 36,
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
            FontSize = 9,
            TextWrapping = TextWrapping.NoWrap,
            Padding = new Thickness(8),
        };
        _logViewerContent.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _logViewerEmptyState = new TextBlock
        {
            FontSize = 12,
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
        LoadLogContent();
        _logOverlay.IsVisible = true;
    }

    private void OnMenuViewCrashLogClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        if (_logOverlay is null) return;
        if (_logViewerTitle is not null)
            _logViewerTitle.Text = Localization.MenuItemViewCrashLog;
        LoadCrashLogContent();
        _logOverlay.IsVisible = true;
    }

    private void LoadCrashLogContent()
    {
        if (_logViewerContent is null) return;
        try
        {
            var crashesDir = System.IO.Path.Combine(
                VPNRouter.Core.AppPaths.DataDir, "crashes");
            if (!System.IO.Directory.Exists(crashesDir))
            {
                ShowLogEmptyState(Localization.CrashLogEmpty);
                return;
            }

            var files = System.IO.Directory.GetFiles(crashesDir, "*.txt");
            if (files.Length == 0)
            {
                ShowLogEmptyState(Localization.CrashLogEmpty);
                return;
            }

            var newest = files
                .Select(p => new System.IO.FileInfo(p))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .First();

            const int MaxBytes = 50_000;
            string text;
            using (var fs = newest.OpenRead())
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
                    text = "(truncated to last 50 KB)\n\n" + sr.ReadToEnd();
                }
            }

            text = $"# {newest.Name}\n# {newest.LastWriteTime:yyyy-MM-dd HH:mm:ss} " +
                   $"(of {files.Length} total)\n\n" + text;

            _logViewerContent.Text = text;
            if (_logViewerEmptyState is not null) _logViewerEmptyState.IsVisible = false;
            if (_logViewerScroller is not null)
            {
                _logViewerScroller.IsVisible = true;
                Dispatcher.UIThread.Post(() =>
                {
                    if (_logViewerScroller is null) return;
                    _logViewerScroller.Offset = new Vector(
                        _logViewerScroller.Offset.X, 0);
                }, DispatcherPriority.Background);
            }
        }
        catch (Exception ex)
        {
            ShowLogEmptyState(string.Format(Localization.LogViewerError,
                ex.GetType().Name, ex.Message));
        }
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
        catch {  }
    }
}
