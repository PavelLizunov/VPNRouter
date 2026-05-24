using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Linq;

namespace VPNRouter.Android;

/// <summary>
/// Phase 2C (Wave 9, 2026-05-18) — notification surfaces extracted from
/// <c>AndroidApp.axaml.cs</c>. The Android port has no true Android-level
/// notifications surfaced from this layer (those live in
/// <c>VpnRouterService.java</c>), but it does host three in-app
/// notification mechanisms that the user sees as transient or modal
/// feedback:
///
/// <list type="bullet">
///   <item><strong>Toast banner</strong> — <see cref="ShowMenuFeedback"/>
///   surfaces a transient sub-status-card message for kebab-menu actions
///   (log path copied, settings reset, update placeholder). Auto-clears
///   after ~3 s.</item>
///   <item><strong>Log viewer overlay</strong> — <see cref="BuildLogOverlay"/>
///   +<see cref="ShowLogViewer"/> +<see cref="LoadLogContent"/>. Reads the
///   last 50 KB of <c>singbox.log</c> into a monospace ScrollViewer.</item>
///   <item><strong>Crash log viewer</strong> — same overlay, different
///   source: <see cref="LoadCrashLogContent"/> reads the most recent file
///   from <c>AppPaths.DataDir/crashes/</c>. Both the C# CrashReporter and
///   the VpnRouterService Java uncaught-handler write there.</item>
///   <item><strong>Recovery notice surfacing</strong> —
///   <see cref="ConsumeAndSurfaceRecoveryNotice"/> composes SettingsLoader
///   / AndroidStorage / safe-mode notices into a single toast on startup.</item>
/// </list>
///
/// <para>Update banner (<c>_updateBanner</c>) lives in
/// <c>AndroidApp.AutoUpdate.cs</c>; status-card error one-liner +
/// diagnostics health-check (<c>_statusHealthCheck</c>,
/// <c>_statusErrorOneLiner</c>) live in <c>AndroidApp.VpnLifecycle.cs</c>.
/// This partial intentionally limits itself to the three surfaces above
/// that share the "user sees a transient message" semantic.</para>
/// </summary>
public partial class AndroidApp
{
    // v2.37.0-r8 — magic-number extraction. Menu feedback toast (kebab
    // menu action confirmations, log path copy, etc.) auto-dismisses after
    // this window so a stale «Скопировано» doesn't loiter across subsequent
    // actions. 3s is long enough to read a short status string.
    private const int MenuFeedbackDismissMs = 3000;

    /// <summary>
    /// v2.32.0 SR-1/2/3/4 — pull whatever recovery notices accumulated
    /// during this launch (bad SharedPrefs JSON deserialise, unknown
    /// enum reset, persistent safe-mode flag from a previous chronic
    /// crash run) and surface them via the existing menu-feedback
    /// banner. Kept tiny + try/catch'd: the banner is informational,
    /// not load-bearing.
    /// </summary>
    private void ConsumeAndSurfaceRecoveryNotice()
    {
        // Order: SettingsLoader notice (desktop-style YAML, currently
        // always null on Android — kept for forward compat), then
        // AndroidStorage notice (our actual per-key SR-1/3/4 stamps),
        // then safe-mode banner (SR-2 tier-3, persisted across crashes).
        var coreNotice = VPNRouter.Core.Services.SettingsLoader.ConsumeRecoveryNotice();
        var androidNotice = AndroidStorage.ConsumeRecoveryNotice();
        var safeMode = AndroidStorage.ConsumeSafeModeBanner();
        // v2.32.3 (Z:\kanareik incident) — placeholder-prune count from
        // PruneKnownPlaceholdersOnce. Distinct from the generic recovery
        // notice because the message+CTA is specific.
        var placeholderCount = AndroidStorage.GetPlaceholderPruneCount();

        var parts = new System.Collections.Generic.List<string>(4);
        if (!string.IsNullOrWhiteSpace(coreNotice)) parts.Add(coreNotice);
        if (!string.IsNullOrWhiteSpace(androidNotice)) parts.Add(androidNotice);
        if (placeholderCount > 0)
        {
            // Distinguish "all servers were placeholders" from "some were"
            // by peeking at remaining storage. If nothing's left after the
            // prune, surface the "add a real server" CTA; otherwise the
            // milder count-only banner. Same Strings keys as desktop.
            var anyServerLeft =
                (AndroidStorage.GetServers().Count > 0) ||
                AndroidStorage.GetSubscriptions().Any(s => s?.Servers?.Count > 0);
            parts.Add(anyServerLeft
                ? string.Format(Localization.PlaceholderPruneBanner, placeholderCount)
                : Localization.PlaceholderPruneBannerAllGone);
            // One-shot — clear so we don't re-surface on next launch.
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

    private void ShowLogViewer()
    {
        if (_logOverlay is null) return;
        if (_logViewerTitle is not null) _logViewerTitle.Text = "singbox.log";
        LoadLogContent();
        _logOverlay.IsVisible = true;
    }

    /// <summary>
    /// v2.32.0 (AND-CRASH-HOOK, 2026-05-08) — Diagnostics → "View crash
    /// log". Opens the same overlay as the singbox.log viewer but loads
    /// the most recent file from <c>AppPaths.DataDir/crashes/</c>. Both
    /// the C# CrashReporter (<c>crash-*.txt</c>) and the VpnRouterService
    /// Java uncaught-handler (<c>java-crash-*.txt</c>) write here, so a
    /// single entry-point covers both origin paths.
    /// </summary>
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

            // Cap at 50 KB to match singbox.log viewer — crash files are
            // typically <10 KB, but a malformed multi-MB report would
            // OOM the GC if we slurped it whole.
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

            // Header line so the user/support sees which file they're
            // looking at when several crashes accumulate.
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

    /// <summary>
    /// v3.0 Phase 7.4 — read the log file's tail (≤50 KB) into the
    /// viewer's TextBlock. Caps the read so a multi-megabyte log file
    /// doesn't OOM the GC. If the file doesn't exist or is empty,
    /// surface an empty-state hint instead of a blank pane.
    /// </summary>
    private void LoadLogContent()
    {
        if (_logViewerContent is null) return;
        try
        {
            var ctx = global::Android.App.Application.Context;
            var extDir = ctx.GetExternalFilesDir(null);
            var logPath = extDir is not null
                ? System.IO.Path.Combine(extDir.AbsolutePath, "singbox.log")
                : null;

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
                    // First line will be partial — drop it.
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
                // Scroll to bottom so the most-recent lines are visible
                // immediately. Defer to the next layout pass via
                // Dispatcher to give the TextBlock a chance to measure.
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
            // Clipboard unavailable on some restricted devices — silently ignore.
        }
    }

    /// <summary>
    /// Surfaces a short transient message under the status card. Used by
    /// the Phase 7.2 menu actions (log path copied, settings reset done,
    /// update placeholder, error). Auto-clears after ~3 s.
    /// </summary>
    private async void ShowMenuFeedback(string text)
    {
        if (_menuFeedback is null) return;
        _menuFeedback.Text = text;
        _menuFeedback.IsVisible = true;
        try
        {
            // v2.37.0-r8 — extracted from inline `Task.Delay(3000)` to
            // named constant. 3s is long enough to read a short menu
            // feedback string («Скопировано», «Открыто», etc.) without
            // loitering through subsequent actions.
            await System.Threading.Tasks.Task.Delay(MenuFeedbackDismissMs);
            if (_menuFeedback is not null && _menuFeedback.Text == text)
            {
                _menuFeedback.IsVisible = false;
            }
        }
        catch { /* swallow */ }
    }
}
