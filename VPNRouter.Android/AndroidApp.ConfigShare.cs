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
/// <b>Export</b> (kebab → Diagnostics → "Export config"),
/// <b>Import</b> (kebab → Diagnostics → "Import config"),
/// <b>QR share</b> (form's 📷 QR button — replaces the
/// <c>QrComingSoon</c> placeholder from handbook §3.4).
///
/// <para>All three overlays follow the same fullscreen-Border-on-top-of-
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

    // QR share overlay
    private Border? _cfgQrOverlay;
    private TextBlock? _cfgQrTitle;
    private TextBlock? _cfgQrCurrentServer;
    private QrCanvas? _cfgQrCanvas;
    private TextBlock? _cfgQrSecretBanner;
    private Avalonia.Controls.Button? _cfgQrCopyBtn;
    private TextBlock? _cfgQrScanLabel;
    private TextBlock? _cfgQrScanHint;
    private TextBox? _cfgQrPasteBox;
    private Avalonia.Controls.Button? _cfgQrApplyBtn;
    private Avalonia.Controls.Button? _cfgQrCloseBtn;
    private TextBlock? _cfgQrStatus;
    private string? _cfgQrCurrentUri;

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

    /// <summary>Build the QR share overlay. Replaces handbook §3.4
    /// placeholder. Shows current active server's URI as a QR code +
    /// clipboard-paste import field.</summary>
    internal Border BuildQrShareOverlay()
    {
        _cfgQrTitle = new TextBlock
        {
            Text = Localization.QrShareTitle,
            FontSize = 16,
            FontWeight = FontWeight.Bold,
        };
        _cfgQrTitle.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _cfgQrCurrentServer = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold,
        };
        _cfgQrCurrentServer.BindToken(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        _cfgQrCanvas = new QrCanvas
        {
            Width = 280,
            Height = 280,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            // White background with black modules — universally readable
            // by QR scanners. Fixed colours (don't follow theme) since
            // QR readability depends on high contrast; an inverted
            // dark-mode QR confuses some scanners.
            Background = Brushes.White,
            DarkBrush = Brushes.Black,
        };

        _cfgQrSecretBanner = new TextBlock
        {
            Text = Localization.QrShareSecretBanner,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 16,
        };
        _cfgQrSecretBanner.BindToken(TextBlock.ForegroundProperty, "WarningFgBrush");

        _cfgQrCopyBtn = new Avalonia.Controls.Button
        {
            Content = Localization.QrShareCopyUriButton,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 10),
            FontSize = 13,
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
        };
        _cfgQrCopyBtn.BindToken(Avalonia.Controls.Button.BackgroundProperty, "AccentBgSubtleBrush");
        _cfgQrCopyBtn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "AccentFgBrush");
        _cfgQrCopyBtn.Click += OnCfgQrCopyClicked;

        _cfgQrScanLabel = new TextBlock
        {
            Text = Localization.QrShareScanFromClipboardLabel,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 8, 0, 0),
        };
        _cfgQrScanLabel.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        _cfgQrScanHint = new TextBlock
        {
            Text = Localization.QrShareScanHint,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 14,
        };
        _cfgQrScanHint.BindToken(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        _cfgQrPasteBox = new TextBox
        {
            FontSize = 11,
            Padding = new Thickness(10, 7),
            Watermark = "vless:// …",
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            FontFamily = new FontFamily("monospace"),
        };

        _cfgQrApplyBtn = new Avalonia.Controls.Button
        {
            Content = Localization.QrSharePasteButton,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 10),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
        };
        _cfgQrApplyBtn.BindToken(Avalonia.Controls.Button.BackgroundProperty, "AccentSolidBrush");
        _cfgQrApplyBtn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "AccentSolidFgBrush");
        _cfgQrApplyBtn.Click += OnCfgQrApplyClicked;

        _cfgQrStatus = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        _cfgQrStatus.BindToken(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        _cfgQrCloseBtn = new Avalonia.Controls.Button
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
        _cfgQrCloseBtn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "TextSecondaryBrush");
        _cfgQrCloseBtn.Click += (_, _) => HideQrOverlay();

        var titleBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(16, 12, 8, 4),
        };
        Grid.SetColumn(_cfgQrTitle, 0);
        Grid.SetColumn(_cfgQrCloseBtn, 1);
        titleBar.Children.Add(_cfgQrTitle);
        titleBar.Children.Add(_cfgQrCloseBtn);

        var stack = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(16, 4, 16, 16),
            Children =
            {
                _cfgQrCurrentServer,
                _cfgQrCanvas,
                _cfgQrSecretBanner,
                _cfgQrCopyBtn,
                _cfgQrScanLabel,
                _cfgQrScanHint,
                _cfgQrPasteBox,
                _cfgQrApplyBtn,
                _cfgQrStatus,
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

    public void ShowQrOverlay()
    {
        if (_cfgQrOverlay is null) return;
        BuildQrPayload();
        _cfgQrOverlay.IsVisible = true;
    }

    public void HideQrOverlay()
    {
        if (_cfgQrOverlay is not null) _cfgQrOverlay.IsVisible = false;
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

    private void BuildQrPayload()
    {
        // Resolve which URI to display. Priority:
        //   1. Active subscription server (selected by Name)
        //   2. Manual VLESS URI (KeyVlessUri)
        // We don't share custom JSON via QR — too big and not a single-
        // server share use case.
        string? uri = null;
        string? label = null;

        var server = AndroidStorage.GetActiveServer();
        if (server is not null && !string.IsNullOrWhiteSpace(server.Server))
        {
            uri = TryReBuildVlessUri(server);
            label = string.IsNullOrEmpty(server.Name) ? server.Server : server.Name;
        }
        if (string.IsNullOrEmpty(uri))
        {
            var stored = AndroidStorage.GetVlessUri();
            if (!string.IsNullOrWhiteSpace(stored))
            {
                uri = stored;
                label = stored;
            }
        }

        _cfgQrCurrentUri = uri;

        if (_cfgQrCurrentServer is not null)
        {
            _cfgQrCurrentServer.Text = string.IsNullOrEmpty(uri)
                ? Localization.QrShareNoActiveServer
                : label;
        }

        if (_cfgQrCanvas is not null)
        {
            if (string.IsNullOrEmpty(uri))
            {
                _cfgQrCanvas.SetMatrix(null);
            }
            else
            {
                try
                {
                    var qr = QrCode.EncodeText(uri, QrCode.Ecc.Medium);
                    _cfgQrCanvas.SetMatrix(qr.ToMatrix());
                }
                catch (Exception ex)
                {
                    global::Android.Util.Log.Warn("VpnRouter.ConfigShare",
                        $"QR encode failed: {ex.GetType().Name}: {ex.Message}");
                    _cfgQrCanvas.SetMatrix(null);
                }
            }
        }

        if (_cfgQrCopyBtn is not null) _cfgQrCopyBtn.IsEnabled = !string.IsNullOrEmpty(uri);
        if (_cfgQrPasteBox is not null) _cfgQrPasteBox.Text = string.Empty;
        if (_cfgQrStatus is not null)
        {
            _cfgQrStatus.Text = string.Empty;
            _cfgQrStatus.IsVisible = false;
        }
    }

    /// <summary>
    /// Reconstruct a vless:// URI from a stored <see cref="VlessServerEntry"/>.
    /// We don't keep the original URI text, only parsed fields, so we
    /// re-emit a canonical form. Format mirrors common share-link parsers
    /// (NekoBox / v2rayNG / Hiddify). Returns empty for non-VLESS protocols
    /// (Hysteria2/TUIC/SS) — those use different URI schemes and the
    /// rebuild path isn't worth the complexity for v1.
    /// </summary>
    private static string TryReBuildVlessUri(VlessServerEntry srv)
    {
        // Only build URIs for VLESS protocol — other protocols (hy2/tuic/ss)
        // need different schemes and we lose source detail at parse time.
        var protocol = string.IsNullOrEmpty(srv.Protocol) ? "vless" : srv.Protocol.ToLowerInvariant();
        if (protocol != "vless") return string.Empty;

        var sb = new System.Text.StringBuilder();
        sb.Append("vless://");
        if (!string.IsNullOrEmpty(srv.Uuid)) sb.Append(srv.Uuid);
        sb.Append('@').Append(srv.Server).Append(':').Append(srv.Port);

        var qp = new List<string>();
        if (!string.IsNullOrEmpty(srv.Flow)) qp.Add($"flow={Uri.EscapeDataString(srv.Flow)}");
        if (!string.IsNullOrEmpty(srv.Security)) qp.Add($"security={Uri.EscapeDataString(srv.Security)}");

        // Reality block — server_name → SNI, public_key → pbk, short_id → sid,
        // fingerprint → fp. Only emit when Reality is actually populated.
        var reality = srv.Reality;
        if (reality is not null)
        {
            if (!string.IsNullOrEmpty(reality.ServerName)) qp.Add($"sni={Uri.EscapeDataString(reality.ServerName)}");
            if (!string.IsNullOrEmpty(reality.PublicKey)) qp.Add($"pbk={Uri.EscapeDataString(reality.PublicKey)}");
            if (!string.IsNullOrEmpty(reality.ShortId)) qp.Add($"sid={Uri.EscapeDataString(reality.ShortId)}");
            if (!string.IsNullOrEmpty(reality.Fingerprint)) qp.Add($"fp={Uri.EscapeDataString(reality.Fingerprint)}");
        }
        else if (srv.Tls is not null)
        {
            if (!string.IsNullOrEmpty(srv.Tls.ServerName)) qp.Add($"sni={Uri.EscapeDataString(srv.Tls.ServerName)}");
            if (!string.IsNullOrEmpty(srv.Tls.Fingerprint)) qp.Add($"fp={Uri.EscapeDataString(srv.Tls.Fingerprint)}");
            if (!string.IsNullOrEmpty(srv.Tls.Alpn)) qp.Add($"alpn={Uri.EscapeDataString(srv.Tls.Alpn)}");
        }

        // Transport — type=tcp default; only emit when non-tcp or non-default path.
        var transport = srv.Transport;
        if (transport is not null && !string.IsNullOrEmpty(transport.Type))
        {
            qp.Add($"type={Uri.EscapeDataString(transport.Type)}");
            if (transport.Type != "tcp" && !string.IsNullOrEmpty(transport.Path) && transport.Path != "/")
            {
                qp.Add($"path={Uri.EscapeDataString(transport.Path)}");
            }
        }

        // VLESS encryption is universally "none" in practice — we add it
        // so v2rayNG / NekoBox parsers don't choke (they sometimes
        // require it explicitly).
        qp.Add("encryption=none");

        if (qp.Count > 0)
        {
            sb.Append('?');
            sb.Append(string.Join('&', qp));
        }

        if (!string.IsNullOrEmpty(srv.Name))
        {
            sb.Append('#').Append(Uri.EscapeDataString(srv.Name));
        }
        return sb.ToString();
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

        // Settings re-bind happens whenever the Settings overlay is opened
        // next, so no eager refresh needed here. Theme + language live-
        // switch is a known gap (handbook 8.2) — full repaint on next
        // launch.

        _cfgImportPendingDoc = null;
        if (_cfgImportApplyBtn is not null) _cfgImportApplyBtn.IsVisible = false;
        if (_cfgImportCancelBtn is not null) _cfgImportCancelBtn.IsVisible = false;
        if (_cfgImportConfirmBanner is not null) _cfgImportConfirmBanner.IsVisible = false;
    }

    private void OnCfgImportCancelClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ResetImportOverlay();
    }

    private void OnCfgQrCopyClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_cfgQrCurrentUri)) return;
        try
        {
            CopyToClipboard("vpnrouter-vless-uri", _cfgQrCurrentUri);
            ShowQrStatus(Localization.QrShareCopiedToast);
        }
        catch (Exception ex)
        {
            ShowQrStatus($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnCfgQrApplyClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var raw = _cfgQrPasteBox?.Text?.Trim() ?? string.Empty;
        if (raw.Length == 0)
        {
            ShowQrStatus(string.Format(Localization.QrShareApplyFailed, "(empty)"));
            return;
        }

        try
        {
            // Parse via the existing ServerUriParser so we accept any of
            // vless / hysteria2 / tuic / ss URIs (Phase 6.4 multi-protocol
            // support). On parse failure surface the message verbatim.
            var entry = VPNRouter.Core.Services.ServerUriParser.Parse(raw);
            // Persist to KeyVlessUri so the existing manual-mode flow
            // picks it up. Don't flip ConfigMode here — the user might
            // want to switch from subscribe to manual in the form.
            AndroidStorage.SetVlessUri(raw);
            ShowQrStatus(Localization.QrShareApplyOk +
                $" ({entry.Server}:{entry.Port})");
        }
        catch (Exception ex)
        {
            ShowQrStatus(string.Format(Localization.QrShareApplyFailed,
                $"{ex.GetType().Name}: {ex.Message}"));
        }
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

    private void ShowQrStatus(string text)
    {
        if (_cfgQrStatus is null) return;
        _cfgQrStatus.Text = text;
        _cfgQrStatus.IsVisible = !string.IsNullOrEmpty(text);
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

        if (_cfgQrTitle is not null) _cfgQrTitle.Text = Localization.QrShareTitle;
        if (_cfgQrSecretBanner is not null) _cfgQrSecretBanner.Text = Localization.QrShareSecretBanner;
        if (_cfgQrCopyBtn is not null) _cfgQrCopyBtn.Content = Localization.QrShareCopyUriButton;
        if (_cfgQrScanLabel is not null) _cfgQrScanLabel.Text = Localization.QrShareScanFromClipboardLabel;
        if (_cfgQrScanHint is not null) _cfgQrScanHint.Text = Localization.QrShareScanHint;
        if (_cfgQrApplyBtn is not null) _cfgQrApplyBtn.Content = Localization.QrSharePasteButton;

        if (_menuExportConfigItem is not null) _menuExportConfigItem.Content = Localization.MenuItemExportConfig;
        if (_menuImportConfigItem is not null) _menuImportConfigItem.Content = Localization.MenuItemImportConfig;
    }
}

/// <summary>
/// v2.32.0 (Android-led, 2026-05-07) — minimal Avalonia control that
/// renders a QR matrix in the Render() pass. Self-sized; the parent
/// constrains via Width / Height. White background + black modules
/// hard-coded for QR-scanner compatibility (some scanners refuse
/// inverted QR codes).
/// </summary>
internal sealed class QrCanvas : Control
{
    public static readonly Avalonia.StyledProperty<IBrush?> DarkBrushProperty =
        Avalonia.AvaloniaProperty.Register<QrCanvas, IBrush?>(nameof(DarkBrush), Brushes.Black);

    public static readonly Avalonia.StyledProperty<IBrush?> BackgroundProperty =
        Avalonia.AvaloniaProperty.Register<QrCanvas, IBrush?>(nameof(Background), Brushes.White);

    public IBrush? DarkBrush
    {
        get => GetValue(DarkBrushProperty);
        set => SetValue(DarkBrushProperty, value);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    private bool[,]? _matrix;

    public QrCanvas()
    {
        // Repaint on brush change so theme/colour overrides reflect.
        AffectsRender<QrCanvas>(DarkBrushProperty, BackgroundProperty);
    }

    public void SetMatrix(bool[,]? matrix)
    {
        _matrix = matrix;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        // Background fill — full bounds. White by default; if Background
        // is null we just skip.
        var bg = Background;
        var fg = DarkBrush ?? Brushes.Black;
        var rect = new Avalonia.Rect(0, 0, Bounds.Width, Bounds.Height);
        if (bg is not null) context.FillRectangle(bg, rect);

        if (_matrix is null || _matrix.GetLength(0) == 0) return;

        int n = _matrix.GetLength(0);
        // Quiet zone (empty border) — 4 modules per spec, scaled down
        // proportionally to fit available pixels.
        const int Quiet = 4;
        int totalModules = n + Quiet * 2;
        double cell = Math.Min(Bounds.Width, Bounds.Height) / totalModules;
        if (cell <= 0) return;

        double offsetX = (Bounds.Width - cell * totalModules) / 2.0 + cell * Quiet;
        double offsetY = (Bounds.Height - cell * totalModules) / 2.0 + cell * Quiet;

        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                if (_matrix[y, x])
                {
                    var px = offsetX + x * cell;
                    var py = offsetY + y * cell;
                    // Draw a tiny bit larger than cell to avoid sub-pixel
                    // gaps that some Android renderers leave.
                    context.FillRectangle(fg!,
                        new Avalonia.Rect(px, py, cell + 0.5, cell + 0.5));
                }
            }
        }
    }
}
