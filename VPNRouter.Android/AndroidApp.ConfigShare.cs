using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Android;

/// <summary>
/// v2.32.0 (Android-led, 2026-05-07) — config share overlays:
/// <b>Export</b> (kebab → Diagnostics → "Export config") and
/// <b>Import</b> (kebab → Diagnostics → "Import config").
///
/// <para>Both overlays follow the same fullscreen-Border-on-top-of-
/// scroller pattern as the log viewer (Phase 7.4) and Settings overlay
/// (AND-2): hidden by default, shown imperatively, dismissed via a ✕
/// close button. No XAML — built in code so the same Tokens binding
/// flow as the rest of AndroidApp applies.</para>
///
/// <para>Wiring lives in this partial; field declarations + menu wiring
/// in <c>AndroidApp.axaml.cs</c> are intentionally minimal so this
/// feature can be feature-flagged or pulled out cleanly later.</para>
/// </summary>
public partial class AndroidApp
{
    // Export overlay
    private Border? _cfgExportOverlay;
    private TextBlock? _cfgExportTitle;
    private TextBlock? _cfgExportDesc;
    private Avalonia.Controls.CheckBox? _cfgExportIncludeSettings;
    private Avalonia.Controls.CheckBox? _cfgExportIncludePerApp;
    private TextBlock? _cfgExportSecretBanner;
    private Avalonia.Controls.Button? _cfgExportSaveBtn;
    private Avalonia.Controls.Button? _cfgExportCloseBtn;
    private TextBlock? _cfgExportStatus;
    private Avalonia.Controls.Button? _menuExportConfigItem;

    // Import overlay
    private Border? _cfgImportOverlay;
    private TextBlock? _cfgImportTitle;
    private TextBlock? _cfgImportDesc;
    private Avalonia.Controls.Button? _cfgImportPickBtn;
    private TextBlock? _cfgImportPreviewLabel;
    private TextBlock? _cfgImportPreview;
    private Avalonia.Controls.CheckBox? _cfgImportApplySettings;
    private Avalonia.Controls.CheckBox? _cfgImportApplyPerApp;
    private TextBlock? _cfgImportConfirmBanner;
    private Avalonia.Controls.Button? _cfgImportApplyBtn;
    private Avalonia.Controls.Button? _cfgImportCancelBtn;
    private Avalonia.Controls.Button? _cfgImportCloseBtn;
    private TextBlock? _cfgImportStatus;
    private ConfigShareDocument? _cfgImportPendingDoc;
    private Avalonia.Controls.Button? _menuImportConfigItem;

    // ── Build overlays ─────────────────────────────────────────────────

    /// <summary>Build the Export overlay. Hidden until the user picks
    /// "Export config" from the kebab menu.</summary>
    internal Border BuildExportOverlay()
    {
        _cfgExportTitle = new TextBlock
        {
            Text = Localization.ExportTitle,
            FontSize = 16,
            FontWeight = FontWeight.Bold,
        };
        _cfgExportTitle.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _cfgExportDesc = new TextBlock
        {
            Text = Localization.ExportDescription,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18,
        };
        _cfgExportDesc.BindToken(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        _cfgExportIncludeSettings = new Avalonia.Controls.CheckBox
        {
            Content = Localization.ExportIncludeSettings,
            FontSize = 12,
            IsChecked = false,
        };
        _cfgExportIncludeSettings.BindToken(Avalonia.Controls.CheckBox.ForegroundProperty, "TextPrimaryBrush");

        _cfgExportIncludePerApp = new Avalonia.Controls.CheckBox
        {
            Content = Localization.ExportIncludePerApp,
            FontSize = 12,
            IsChecked = false,
        };
        _cfgExportIncludePerApp.BindToken(Avalonia.Controls.CheckBox.ForegroundProperty, "TextPrimaryBrush");

        _cfgExportSecretBanner = new TextBlock
        {
            Text = Localization.ExportSecretBanner,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 16,
        };
        _cfgExportSecretBanner.BindToken(TextBlock.ForegroundProperty, "WarningFgBrush");

        _cfgExportSaveBtn = new Avalonia.Controls.Button
        {
            Content = Localization.ExportSaveButton,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 10),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
        };
        _cfgExportSaveBtn.BindToken(Avalonia.Controls.Button.BackgroundProperty, "AccentSolidBrush");
        _cfgExportSaveBtn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "AccentSolidFgBrush");
        _cfgExportSaveBtn.Click += OnCfgExportSaveClicked;

        _cfgExportStatus = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        _cfgExportStatus.BindToken(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        _cfgExportCloseBtn = new Avalonia.Controls.Button
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
        _cfgExportCloseBtn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "TextSecondaryBrush");
        _cfgExportCloseBtn.Click += (_, _) => HideExportOverlay();

        var titleBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(16, 12, 8, 4),
        };
        Grid.SetColumn(_cfgExportTitle, 0);
        Grid.SetColumn(_cfgExportCloseBtn, 1);
        titleBar.Children.Add(_cfgExportTitle);
        titleBar.Children.Add(_cfgExportCloseBtn);

        var stack = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(16, 4, 16, 16),
            Children =
            {
                _cfgExportDesc,
                _cfgExportIncludeSettings,
                _cfgExportIncludePerApp,
                _cfgExportSecretBanner,
                _cfgExportSaveBtn,
                _cfgExportStatus,
            },
        };

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(titleBar, Dock.Top);
        dock.Children.Add(titleBar);
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = stack,
        };
        dock.Children.Add(scroller);

        var overlay = new Border
        {
            IsVisible = false,
            Child = dock,
        };
        overlay.BindToken(Border.BackgroundProperty, "SurfaceAppBrush");
        return overlay;
    }

    /// <summary>Build the Import overlay. Hidden until the user picks
    /// "Import config" from the kebab menu.</summary>
    internal Border BuildImportOverlay()
    {
        _cfgImportTitle = new TextBlock
        {
            Text = Localization.ImportTitle,
            FontSize = 16,
            FontWeight = FontWeight.Bold,
        };
        _cfgImportTitle.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _cfgImportDesc = new TextBlock
        {
            Text = Localization.ImportDescription,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18,
        };
        _cfgImportDesc.BindToken(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        _cfgImportPickBtn = new Avalonia.Controls.Button
        {
            Content = Localization.ImportPickButton,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 10),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
        };
        _cfgImportPickBtn.BindToken(Avalonia.Controls.Button.BackgroundProperty, "AccentBgSubtleBrush");
        _cfgImportPickBtn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "AccentFgBrush");
        _cfgImportPickBtn.BindToken(Avalonia.Controls.Button.BorderBrushProperty, "BorderAccentBrush");
        _cfgImportPickBtn.BorderThickness = new Thickness(1);
        _cfgImportPickBtn.Click += OnCfgImportPickClicked;

        _cfgImportPreviewLabel = new TextBlock
        {
            Text = Localization.ImportPreviewLabel,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            IsVisible = false,
        };
        _cfgImportPreviewLabel.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        _cfgImportPreview = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        _cfgImportPreview.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _cfgImportApplySettings = new Avalonia.Controls.CheckBox
        {
            Content = Localization.ImportApplySettings,
            FontSize = 12,
            IsChecked = false,
            IsVisible = false,
        };
        _cfgImportApplySettings.BindToken(Avalonia.Controls.CheckBox.ForegroundProperty, "TextPrimaryBrush");

        _cfgImportApplyPerApp = new Avalonia.Controls.CheckBox
        {
            Content = Localization.ImportApplyPerApp,
            FontSize = 12,
            IsChecked = false,
            IsVisible = false,
        };
        _cfgImportApplyPerApp.BindToken(Avalonia.Controls.CheckBox.ForegroundProperty, "TextPrimaryBrush");

        _cfgImportConfirmBanner = new TextBlock
        {
            Text = Localization.ImportConfirmReplace,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 16,
            IsVisible = false,
        };
        _cfgImportConfirmBanner.BindToken(TextBlock.ForegroundProperty, "WarningFgBrush");

        _cfgImportApplyBtn = new Avalonia.Controls.Button
        {
            Content = Localization.ImportApplyButton,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 10),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            IsVisible = false,
        };
        _cfgImportApplyBtn.BindToken(Avalonia.Controls.Button.BackgroundProperty, "AccentSolidBrush");
        _cfgImportApplyBtn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "AccentSolidFgBrush");
        _cfgImportApplyBtn.Click += OnCfgImportApplyClicked;

        _cfgImportCancelBtn = new Avalonia.Controls.Button
        {
            Content = Localization.ImportCancelButton,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 10),
            FontSize = 13,
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            IsVisible = false,
        };
        _cfgImportCancelBtn.BindToken(Avalonia.Controls.Button.BackgroundProperty, "SurfaceSunkenBrush");
        _cfgImportCancelBtn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "TextSecondaryBrush");
        _cfgImportCancelBtn.Click += OnCfgImportCancelClicked;

        _cfgImportStatus = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        _cfgImportStatus.BindToken(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        _cfgImportCloseBtn = new Avalonia.Controls.Button
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
        _cfgImportCloseBtn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "TextSecondaryBrush");
        _cfgImportCloseBtn.Click += (_, _) => HideImportOverlay();

        var titleBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(16, 12, 8, 4),
        };
        Grid.SetColumn(_cfgImportTitle, 0);
        Grid.SetColumn(_cfgImportCloseBtn, 1);
        titleBar.Children.Add(_cfgImportTitle);
        titleBar.Children.Add(_cfgImportCloseBtn);

        var stack = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(16, 4, 16, 16),
            Children =
            {
                _cfgImportDesc,
                _cfgImportPickBtn,
                _cfgImportPreviewLabel,
                _cfgImportPreview,
                _cfgImportApplySettings,
                _cfgImportApplyPerApp,
                _cfgImportConfirmBanner,
                _cfgImportApplyBtn,
                _cfgImportCancelBtn,
                _cfgImportStatus,
            },
        };

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(titleBar, Dock.Top);
        dock.Children.Add(titleBar);
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = stack,
        };
        dock.Children.Add(scroller);

        var overlay = new Border
        {
            IsVisible = false,
            Child = dock,
        };
        overlay.BindToken(Border.BackgroundProperty, "SurfaceAppBrush");
        return overlay;
    }

    // ── Show / hide ────────────────────────────────────────────────────

    public void ShowExportOverlay()
    {
        if (_cfgExportOverlay is null) return;
        ResetExportOverlay();
        _cfgExportOverlay.IsVisible = true;
    }

    public void HideExportOverlay()
    {
        if (_cfgExportOverlay is not null) _cfgExportOverlay.IsVisible = false;
    }

    public void ShowImportOverlay()
    {
        if (_cfgImportOverlay is null) return;
        ResetImportOverlay();
        _cfgImportOverlay.IsVisible = true;
    }

    public void HideImportOverlay()
    {
        if (_cfgImportOverlay is not null) _cfgImportOverlay.IsVisible = false;
    }

    private void ResetExportOverlay()
    {
        if (_cfgExportStatus is not null)
        {
            _cfgExportStatus.Text = string.Empty;
            _cfgExportStatus.IsVisible = false;
        }
        if (_cfgExportSaveBtn is not null) _cfgExportSaveBtn.IsEnabled = true;
    }

    private void ResetImportOverlay()
    {
        _cfgImportPendingDoc = null;
        if (_cfgImportPreviewLabel is not null) _cfgImportPreviewLabel.IsVisible = false;
        if (_cfgImportPreview is not null) _cfgImportPreview.IsVisible = false;
        if (_cfgImportApplySettings is not null)
        {
            _cfgImportApplySettings.IsVisible = false;
            _cfgImportApplySettings.IsChecked = false;
        }
        if (_cfgImportApplyPerApp is not null)
        {
            _cfgImportApplyPerApp.IsVisible = false;
            _cfgImportApplyPerApp.IsChecked = false;
        }
        if (_cfgImportConfirmBanner is not null) _cfgImportConfirmBanner.IsVisible = false;
        if (_cfgImportApplyBtn is not null) _cfgImportApplyBtn.IsVisible = false;
        if (_cfgImportCancelBtn is not null) _cfgImportCancelBtn.IsVisible = false;
        if (_cfgImportStatus is not null)
        {
            _cfgImportStatus.Text = string.Empty;
            _cfgImportStatus.IsVisible = false;
        }
        if (_cfgImportPickBtn is not null) _cfgImportPickBtn.IsEnabled = true;
    }

    // ── Click handlers ─────────────────────────────────────────────────

    private void OnCfgExportSaveClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_cfgExportSaveBtn is not null) _cfgExportSaveBtn.IsEnabled = false;
        try
        {
            var includeSettings = _cfgExportIncludeSettings?.IsChecked == true;
            var includePerApp = _cfgExportIncludePerApp?.IsChecked == true;

            var doc = AndroidConfigShare.BuildSnapshot(includeSettings, includePerApp);
            var json = ConfigShareDocument.Serialize(doc);
            var name = ConfigShareDocument.SuggestFilename();

            var activity = MainActivity.Instance;
            if (activity is null)
            {
                ShowExportStatus(string.Format(Localization.ExportFailed,
                    "Activity not available"));
                return;
            }

            MainActivity.PendingExportCallback = (ok, message) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_cfgExportSaveBtn is not null) _cfgExportSaveBtn.IsEnabled = true;
                    if (ok)
                    {
                        ShowExportStatus(string.Format(Localization.ExportSuccess, message ?? "(saved)"));
                    }
                    else if (string.Equals(message, "cancelled", StringComparison.OrdinalIgnoreCase))
                    {
                        ShowExportStatus(Localization.ExportPickerCancelled);
                    }
                    else
                    {
                        ShowExportStatus(string.Format(Localization.ExportFailed, message ?? "(unknown)"));
                    }
                });
            };

            activity.RequestExportConfigShare(json, name);
        }
        catch (Exception ex)
        {
            if (_cfgExportSaveBtn is not null) _cfgExportSaveBtn.IsEnabled = true;
            ShowExportStatus(string.Format(Localization.ExportFailed,
                $"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    private void OnCfgImportPickClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_cfgImportPickBtn is not null) _cfgImportPickBtn.IsEnabled = false;
        try
        {
            var activity = MainActivity.Instance;
            if (activity is null)
            {
                ShowImportStatus(string.Format(Localization.ImportFailedRead, "Activity not available"));
                return;
            }
            MainActivity.PendingImportCallback = (ok, payload) =>
            {
                Dispatcher.UIThread.Post(() => HandleImportPicked(ok, payload));
            };
            activity.RequestImportConfigShare();
        }
        catch (Exception ex)
        {
            if (_cfgImportPickBtn is not null) _cfgImportPickBtn.IsEnabled = true;
            ShowImportStatus(string.Format(Localization.ImportFailedRead,
                $"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    private void HandleImportPicked(bool ok, string? payload)
    {
        if (_cfgImportPickBtn is not null) _cfgImportPickBtn.IsEnabled = true;

        if (!ok)
        {
            if (string.Equals(payload, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                ShowImportStatus(Localization.ImportPickerCancelled);
            }
            else
            {
                ShowImportStatus(string.Format(Localization.ImportFailedRead, payload ?? "(unknown)"));
            }
            return;
        }

        var parseResult = ConfigShareDocument.TryParse(payload);
        if (!parseResult.Ok || parseResult.Document is null)
        {
            ShowImportStatus(string.Format(Localization.ImportFailedParse,
                parseResult.Error ?? "(unknown)"));
            return;
        }

        _cfgImportPendingDoc = parseResult.Document;

        var preview = parseResult.Document.BuildPreview(Localization.Ru);
        if (_cfgImportPreviewLabel is not null) _cfgImportPreviewLabel.IsVisible = true;
        if (_cfgImportPreview is not null)
        {
            _cfgImportPreview.Text = preview;
            _cfgImportPreview.IsVisible = true;
        }

        if (_cfgImportApplySettings is not null)
        {
            _cfgImportApplySettings.IsVisible = parseResult.Document.Settings is not null;
            _cfgImportApplySettings.IsChecked = false;
        }
        if (_cfgImportApplyPerApp is not null)
        {
            _cfgImportApplyPerApp.IsVisible = parseResult.Document.PerAppFilter is not null;
            _cfgImportApplyPerApp.IsChecked = false;
        }
        if (_cfgImportConfirmBanner is not null) _cfgImportConfirmBanner.IsVisible = true;
        if (_cfgImportApplyBtn is not null) _cfgImportApplyBtn.IsVisible = true;
        if (_cfgImportCancelBtn is not null) _cfgImportCancelBtn.IsVisible = true;
        if (_cfgImportStatus is not null) _cfgImportStatus.IsVisible = false;
    }

    private void OnCfgImportApplyClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var doc = _cfgImportPendingDoc;
        if (doc is null) return;

        var applySettings = _cfgImportApplySettings?.IsChecked == true;
        var applyPerApp = _cfgImportApplyPerApp?.IsChecked == true;

        var result = AndroidConfigShare.ApplySnapshot(doc, applySettings, applyPerApp);

        if (!result.Ok)
        {
            ShowImportStatus(string.Format(Localization.ImportFailed, result.Error ?? "(unknown)"));
            return;
        }

        if (result.IsPartial && !string.IsNullOrEmpty(result.Error))
        {
            ShowImportStatus(string.Format(Localization.ImportPartial, result.Error));
        }
        else
        {
            var backupHint = result.BackupPath ?? "(none)";
            ShowImportStatus(string.Format(Localization.ImportSuccess, backupHint));
        }

        // Refresh the visible UI so changes are reflected immediately —
        // server list, ConfigMode, language, etc. Fire-and-forget; the
        // overlay stays open so the user reads the success/partial banner.
        try { ReloadServerListFromStorage(); }
        catch { /* best-effort */ }
        try { RefreshConfigModeUiFromStorage(); }
        catch { /* best-effort */ }

        // F6 follow-up (2026-06-16) — ApplySnapshot may have changed PerAppMode
        // (the routing source of truth) via the applied per-app block. Re-seed the
        // Simple-page split/full radios — mirroring CloseAdvancedShell / ApplyProfile
        // — so an import that changes routing can't leave the Simple page showing a
        // mode that contradicts what Advanced→Settings will render on its next open
        // (the exact cross-surface routing drift F6 fixes, just reached via the
        // import path). Setting IsChecked re-fires the idempotent
        // OnTunnelModeRadioChanged (no write when PerAppMode already matches) and
        // refreshes the "Choose apps…" stack visibility. The Advanced→Settings
        // radios self-heal via ReseedNetworkTabState on the next shell open.
        var routing = AndroidStorage.GetRoutingMode();
        if (_splitRadio is not null) _splitRadio.IsChecked = routing == "split";
        if (_fullRadio is not null) _fullRadio.IsChecked = routing == "full";
        UpdatePerAppFormCountLabel();

        // Theme + language live-switch on import remains a known gap (handbook
        // 8.2) — full repaint on next launch.

        _cfgImportPendingDoc = null;
        if (_cfgImportApplyBtn is not null) _cfgImportApplyBtn.IsVisible = false;
        if (_cfgImportCancelBtn is not null) _cfgImportCancelBtn.IsVisible = false;
        if (_cfgImportConfirmBanner is not null) _cfgImportConfirmBanner.IsVisible = false;
    }

    private void OnCfgImportCancelClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ResetImportOverlay();
    }

    // ── Shared helpers ─────────────────────────────────────────────────

    private void ShowExportStatus(string text)
    {
        if (_cfgExportStatus is null) return;
        _cfgExportStatus.Text = text;
        _cfgExportStatus.IsVisible = !string.IsNullOrEmpty(text);
    }

    private void ShowImportStatus(string text)
    {
        if (_cfgImportStatus is null) return;
        _cfgImportStatus.Text = text;
        _cfgImportStatus.IsVisible = !string.IsNullOrEmpty(text);
    }

    /// <summary>
    /// Best-effort UI refresh after import — re-reads current ConfigMode
    /// and adjusts the segmented mode selector + visible sections in the
    /// inline form. No-op if the form's fields aren't constructed yet.
    /// </summary>
    private void RefreshConfigModeUiFromStorage()
    {
        var mode = AndroidStorage.GetConfigMode();
        _ccMode = mode;
        if (_ccUriSection is not null) _ccUriSection.IsVisible = mode != "custom";
        if (_ccCustomSection is not null) _ccCustomSection.IsVisible = mode == "custom";

        if (_ccCustomInput is not null && mode == "custom")
        {
            _ccCustomInput.Text = AndroidStorage.GetCustomConfigJson() ?? string.Empty;
        }

        if (_serverInput is not null && mode == "manual")
        {
            _serverInput.Text = AndroidStorage.GetVlessUri() ?? string.Empty;
        }

        // Re-style the segmented buttons so the active segment matches
        // the imported value.
        if (_ccModeSubBtn is not null) StyleSegmentButton(_ccModeSubBtn, mode == "subscribe");
        if (_ccModeManualBtn is not null) StyleSegmentButton(_ccModeManualBtn, mode == "manual");
        if (_ccModeCustomBtn is not null) StyleSegmentButton(_ccModeCustomBtn, mode == "custom");
    }

    /// <summary>Reload the in-memory subscription / server list views so
    /// the import is reflected without a relaunch. Defined defensively —
    /// subscribers / server lists may not have been built yet during
    /// app launch.</summary>
    private void ReloadServerListFromStorage()
    {
        try
        {
            // The Subscribe overlay rebuilds its model from storage on
            // each open; we don't need to push anything if it isn't open.
            // The inline form server list reads from AndroidStorage on
            // render — Free Configs overlay is similar. So a no-op here
            // is OK for correctness, though we could explicitly trigger
            // ReloadSubsList()/ReloadServerList() if those methods exist.
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Refresh localized strings whenever the language toggles. Mirrors
    /// the field-by-field refresh in <c>ToggleLanguageAndRefresh</c>.
    /// Called from the main toggle so every overlay's static text
    /// follows the new language.
    /// </summary>
    internal void RefreshConfigShareLocalization()
    {
        if (_cfgExportTitle is not null) _cfgExportTitle.Text = Localization.ExportTitle;
        if (_cfgExportDesc is not null) _cfgExportDesc.Text = Localization.ExportDescription;
        if (_cfgExportIncludeSettings is not null) _cfgExportIncludeSettings.Content = Localization.ExportIncludeSettings;
        if (_cfgExportIncludePerApp is not null) _cfgExportIncludePerApp.Content = Localization.ExportIncludePerApp;
        if (_cfgExportSecretBanner is not null) _cfgExportSecretBanner.Text = Localization.ExportSecretBanner;
        if (_cfgExportSaveBtn is not null) _cfgExportSaveBtn.Content = Localization.ExportSaveButton;

        if (_cfgImportTitle is not null) _cfgImportTitle.Text = Localization.ImportTitle;
        if (_cfgImportDesc is not null) _cfgImportDesc.Text = Localization.ImportDescription;
        if (_cfgImportPickBtn is not null) _cfgImportPickBtn.Content = Localization.ImportPickButton;
        if (_cfgImportPreviewLabel is not null) _cfgImportPreviewLabel.Text = Localization.ImportPreviewLabel;
        if (_cfgImportApplySettings is not null) _cfgImportApplySettings.Content = Localization.ImportApplySettings;
        if (_cfgImportApplyPerApp is not null) _cfgImportApplyPerApp.Content = Localization.ImportApplyPerApp;
        if (_cfgImportConfirmBanner is not null) _cfgImportConfirmBanner.Text = Localization.ImportConfirmReplace;
        if (_cfgImportApplyBtn is not null) _cfgImportApplyBtn.Content = Localization.ImportApplyButton;
        if (_cfgImportCancelBtn is not null) _cfgImportCancelBtn.Content = Localization.ImportCancelButton;

        if (_menuExportConfigItem is not null) _menuExportConfigItem.Content = Localization.MenuItemExportConfig;
        if (_menuImportConfigItem is not null) _menuImportConfigItem.Content = Localization.MenuItemImportConfig;
    }
}
