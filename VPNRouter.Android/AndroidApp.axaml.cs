using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Android;

/// <summary>
/// v3.0 Phase 3 (2026-05-04) — honest visual parity with desktop SimplePage.
///
/// <para>User feedback 2026-05-04: «Приложение не выглядит так как выглядит
/// на ПК, совершенно разный интерфейс и оформление». Phase 2's
/// "tokens applied = parity" was wrong. Desktop SimplePage has a
/// specific structure — status card with dot, config row button with
/// flag icon + chevron, collapsible form, three-variant CTA button,
/// "Расширенные настройки" card — and Phase 2's hand-rolled view
/// looked nothing like it.</para>
///
/// <para>This rewrite mirrors <c>VPNRouter.App/Views/Pages/SimplePage.axaml</c>
/// section-by-section:</para>
///
/// <list type="number">
///   <item>Status card: dot (Success/Warning/Muted) + bold title +
///   description (matches lines 42-72 of SimplePage)</item>
///   <item>Config row tappable button: flag icon, label + value
///   "вручную · полный", chevron (lines 74-120)</item>
///   <item>Collapsible inline form: input + radio buttons for tunnel
///   mode + autostart link card (lines 122-220)</item>
///   <item>CTA button — three mutually exclusive variants by state
///   (lines 222-266)</item>
///   <item>Расширенные настройки card → Android version: subscription
///   list page link (lines 268-304)</item>
/// </list>
///
/// <para>Light theme by default to match desktop's default appearance.
/// All colors/radii/spacing pulled from the linked Tokens.axaml.</para>
/// </summary>
public partial class AndroidApp : Avalonia.Application
{
    // Status card
    private Ellipse? _statusDot;
    private TextBlock? _statusTitle;
    private TextBlock? _statusDesc;

    // Config row button
    private TextBlock? _configRowLabel;
    private TextBlock? _configRowValue;
    private TextBlock? _configRowChevron;

    // Collapsible form
    private Border? _formCard;
    private TextBox? _serverInput;
    private TextBlock? _serverInputLabel;
    private TextBlock? _serverInputHint;
    private TextBlock? _serverInputError;
    private TextBlock? _tunnelModeLabel;
    private Avalonia.Controls.RadioButton? _splitRadio;
    private Avalonia.Controls.RadioButton? _fullRadio;
    private TextBlock? _splitLabel;
    private TextBlock? _splitHint;
    private TextBlock? _fullLabel;
    private TextBlock? _fullHint;

    // Server list (subscription)
    private TextBlock? _serverListHeader;
    private ListBox? _serverList;

    // CTA buttons (3 variants)
    private Avalonia.Controls.Button? _ctaConnect;
    private Avalonia.Controls.Button? _ctaConnecting;
    private Avalonia.Controls.Button? _ctaDisconnect;

    // Bottom card
    private TextBlock? _advCardTitle;
    private TextBlock? _advCardSubtitle;

    // Header (Phase 4: full sub-header matching desktop)
    private TextBlock? _brandTitle;
    private TextBlock? _vpnChip;
    private TextBlock? _zapretChip;
    private TextBlock? _tgChip;
    private Avalonia.Controls.Button? _kebabMenuButton;
    private Popup? _kebabPopup;
    private Avalonia.Controls.Button? _menuLanguageItem;
    private Avalonia.Controls.Button? _menuThemeItem;

    // State
    private bool _formExpanded = false;
    private List<VlessServerEntry> _cachedServers = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Localization.LoadFromStorage();
        ApplyTheme();

        if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = BuildSimplePageView();
            MainActivity.IntentChanged += OnIntentChanged;
            UpdateConnectionState(MainActivity.IntendedConnected);
            ReloadServerList();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ApplyTheme()
    {
        var pref = AndroidStorage.GetTheme();
        // Default to Light to match desktop.
        RequestedThemeVariant = pref switch
        {
            "dark" => ThemeVariant.Dark,
            "system" => ThemeVariant.Default,
            _ => ThemeVariant.Light,
        };
    }

    // ── Token helpers ───────────────────────────────────────────────────

    private IBrush GetBrush(string key)
    {
        if (Resources.TryGetResource(key, ActualThemeVariant, out var v) && v is IBrush b) return b;
        return Brushes.Transparent;
    }

    private double GetRadius(string key)
    {
        if (Resources.TryGetResource(key, ActualThemeVariant, out var v))
        {
            return v switch
            {
                double d => d,
                int i => i,
                _ => 8.0
            };
        }
        return 8.0;
    }

    /// <summary>
    /// SimplePage-equivalent view, code-behind. Mirrors
    /// VPNRouter.App/Views/Pages/SimplePage.axaml section-by-section.
    /// </summary>
    private Control BuildSimplePageView()
    {
        var bg = GetBrush("SurfaceAppBrush");
        var card = GetBrush("SurfaceBaseBrush");
        var raised = GetBrush("SurfaceRaisedBrush");
        var sunken = GetBrush("SurfaceSunkenBrush");
        var subtleBorder = GetBrush("BorderSubtleBrush");
        var defaultBorder = GetBrush("BorderDefaultBrush");
        var textPrimary = GetBrush("TextPrimaryBrush");
        var textSecondary = GetBrush("TextSecondaryBrush");
        var textMuted = GetBrush("TextMutedBrush");
        var accentBgSubtle = GetBrush("AccentBgSubtleBrush");
        var accentFg = GetBrush("AccentFgBrush");
        var accentSolid = GetBrush("AccentSolidBrush");
        var accentOnSolid = GetBrush("AccentOnSolidBrush");
        var accentBorder = GetBrush("AccentBorderBrush");
        var radiusXs = GetRadius("RadiusXs");
        var radiusSm = GetRadius("RadiusSm");
        var radiusMd = GetRadius("RadiusMd");

        // ── Sub-header (mascot + brand + chips + kebab menu) ────────────
        // v3.0 Phase 4 (2026-05-04) — desktop parity. Pre-4 had a plain
        // "VPNRouter" title with a "RU" toggle pill at right. Desktop
        // shows: mascot 🐧 + "Virtual Penguin Network" bold + three
        // status chips (VPN / Zapret / TG) + ⋯ kebab menu. The kebab
        // hosts language + theme toggles (was inline RU pill).

        // Mascot — emoji glyph for now; Phase 5 ports the real PNG
        // from VPNRouter.App/Assets/.
        var mascot = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = accentBgSubtle,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "🐧",
                FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }
        };

        _brandTitle = new TextBlock
        {
            Text = Localization.BrandTitle,
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Foreground = textPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _vpnChip = MakeChip("VPN", GetBrush("SuccessBgBrush"), GetBrush("SuccessFgBrush"));
        _zapretChip = MakeChip("Zapret", GetBrush("WarningBgBrush"), GetBrush("WarningFgBrush"));
        _tgChip = MakeChip("TG", GetBrush("SurfaceSunkenBrush"), textMuted);

        var chipRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 2, 0, 0),
            Children = { _vpnChip, _zapretChip, _tgChip }
        };

        var brandStack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _brandTitle, chipRow }
        };

        // ⋮ kebab menu trigger (vertical ellipsis — `⋯` horizontal
        // doesn't render correctly on Android default fonts)
        _kebabMenuButton = new Avalonia.Controls.Button
        {
            Content = "⋮",
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = textSecondary,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _kebabMenuButton.Click += OnKebabMenuClicked;

        // Kebab popup with language + theme items
        _menuLanguageItem = new Avalonia.Controls.Button
        {
            Content = Localization.MenuLanguageLabel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(14, 10),
            FontSize = 12,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = textPrimary,
        };
        _menuLanguageItem.Click += OnMenuLanguageClicked;

        _menuThemeItem = new Avalonia.Controls.Button
        {
            Content = Localization.MenuThemeLabel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(14, 10),
            FontSize = 12,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = textPrimary,
        };
        _menuThemeItem.Click += OnMenuThemeClicked;

        var menuPanel = new Border
        {
            Background = card,
            BorderBrush = defaultBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radiusSm),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 4,
                Blur = 12,
                Color = Color.FromArgb(50, 0, 0, 0),
            }),
            Child = new StackPanel
            {
                Spacing = 0,
                MinWidth = 180,
                Children = { _menuLanguageItem, _menuThemeItem }
            }
        };

        _kebabPopup = new Popup
        {
            PlacementTarget = _kebabMenuButton,
            Placement = PlacementMode.BottomEdgeAlignedRight,
            Child = menuPanel,
            IsLightDismissEnabled = true,
        };

        var headerRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 10,
            Margin = new Thickness(16, 12, 16, 4),
        };
        Grid.SetColumn(mascot, 0);
        Grid.SetColumn(brandStack, 1);
        Grid.SetColumn(_kebabMenuButton, 2);
        headerRow.Children.Add(mascot);
        headerRow.Children.Add(brandStack);
        headerRow.Children.Add(_kebabMenuButton);
        headerRow.Children.Add(_kebabPopup);

        // ── Status card (dot + title + description) ─────────────────────
        _statusDot = new Ellipse
        {
            Width = 10,
            Height = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = GetBrush("TextMutedBrush"),
        };

        _statusTitle = new TextBlock
        {
            Text = Localization.SimpleStatusTitleOff,
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            Foreground = textPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _statusDesc = new TextBlock
        {
            Text = Localization.SimpleStatusDescOff,
            FontSize = 11,
            Foreground = textSecondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20, 0, 0, 0),
            LineHeight = 16,
        };

        var statusHeaderRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _statusDot, _statusTitle },
        };

        var statusCard = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = defaultBorder,
            Background = card,
            CornerRadius = new CornerRadius(radiusMd),
            Padding = new Thickness(14),
            Child = new StackPanel
            {
                Spacing = 8,
                Children = { statusHeaderRow, _statusDesc },
            }
        };

        // ── Config row button (tappable, expands form) ──────────────────
        var flagIcon = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(radiusXs),
            Background = accentBgSubtle,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "⚑",
                FontSize = 12,
                Foreground = accentFg,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }
        };

        _configRowLabel = new TextBlock
        {
            Text = Localization.SmpConfigRowLabel,
            FontSize = 9,
            FontWeight = FontWeight.SemiBold,
            Foreground = textMuted,
        };

        _configRowValue = new TextBlock
        {
            Text = Localization.SimpleConfigSummary,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = textPrimary,
            FontFamily = new FontFamily("monospace"),
        };

        _configRowChevron = new TextBlock
        {
            Text = "›",
            FontSize = 14,
            Foreground = textMuted,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var configRowGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 10,
            Margin = new Thickness(12, 8),
        };
        Grid.SetColumn(flagIcon, 0);
        configRowGrid.Children.Add(flagIcon);
        var configRowText = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _configRowLabel, _configRowValue }
        };
        Grid.SetColumn(configRowText, 1);
        configRowGrid.Children.Add(configRowText);
        Grid.SetColumn(_configRowChevron, 2);
        configRowGrid.Children.Add(_configRowChevron);

        var configRowButton = new Avalonia.Controls.Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(1),
            BorderBrush = subtleBorder,
            Background = raised,
            CornerRadius = new CornerRadius(radiusSm),
            Content = configRowGrid,
        };
        configRowButton.Click += OnConfigRowClicked;

        // ── Collapsible form (input + tunnel mode radios + autostart) ───
        _serverInputLabel = new TextBlock
        {
            Text = Localization.SmpInputLabel,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = textPrimary,
        };

        _serverInput = new TextBox
        {
            FontSize = 11,
            Padding = new Thickness(10, 7),
            AcceptsReturn = false,
            CornerRadius = new CornerRadius(radiusXs),
            Watermark = Localization.SmpInputWatermark,
        };
        var existingSub = AndroidStorage.GetSubscriptionUrl();
        var existingUri = AndroidStorage.GetVlessUri();
        _serverInput.Text = existingSub ?? existingUri ?? string.Empty;

        _serverInputHint = new TextBlock
        {
            Text = Localization.SmpInputHint,
            FontSize = 9,
            Foreground = textMuted,
            TextWrapping = TextWrapping.Wrap,
        };

        _serverInputError = new TextBlock
        {
            FontSize = 10,
            Foreground = GetBrush("DangerFgBrush"),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

        // Save + Refresh + QR button row
        var saveBtn = StyledSecondaryButton(Localization.ButtonSave);
        saveBtn.Click += OnSaveClicked;
        var refreshBtn = StyledSecondaryButton(Localization.ButtonRefresh);
        refreshBtn.Margin = new Thickness(8, 0, 0, 0);
        refreshBtn.Click += OnRefreshClicked;
        var qrBtn = StyledSecondaryButton("📷 QR");
        qrBtn.Margin = new Thickness(8, 0, 0, 0);
        qrBtn.Click += OnScanQrClicked;
        var actionRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { saveBtn, qrBtn, refreshBtn },
        };

        var inputSection = new StackPanel
        {
            Spacing = 4,
            Children = { _serverInputLabel, _serverInput, _serverInputHint, _serverInputError, actionRow },
        };

        // Tunnel mode (split / full)
        _tunnelModeLabel = new TextBlock
        {
            Text = Localization.SmpTunnelModeLabel,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = textPrimary,
        };

        _splitLabel = new TextBlock
        {
            Text = Localization.SmpSplitOption,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = textPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _splitRadio = new Avalonia.Controls.RadioButton
        {
            GroupName = "TunnelMode",
            IsChecked = false, // Android default: full (per Phase 3 P0 fix)
            Content = _splitLabel,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            MinHeight = 0,
            Padding = new Thickness(4, 0),
        };
        _splitHint = new TextBlock
        {
            Text = Localization.SmpSplitHint,
            FontSize = 9,
            Foreground = textMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, 0, 0, 0),
        };
        _fullLabel = new TextBlock
        {
            Text = Localization.SmpFullOption,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = textPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _fullRadio = new Avalonia.Controls.RadioButton
        {
            GroupName = "TunnelMode",
            IsChecked = true, // Android default: route everything
            Content = _fullLabel,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            MinHeight = 0,
            Padding = new Thickness(4, 0),
        };
        _fullHint = new TextBlock
        {
            Text = Localization.SmpFullHint,
            FontSize = 9,
            Foreground = textMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, 0, 0, 0),
        };

        var tunnelSection = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                _tunnelModeLabel,
                new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new StackPanel { Spacing = 1, Children = { _splitRadio, _splitHint } },
                        new StackPanel { Spacing = 1, Children = { _fullRadio, _fullHint } },
                    }
                }
            }
        };

        // Subscription server list (only visible when subscription has servers)
        _serverListHeader = new TextBlock
        {
            Text = Localization.AvailableServers,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = textPrimary,
            IsVisible = false,
        };
        _serverList = new ListBox
        {
            MaxHeight = 240,
            IsVisible = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };
        _serverList.SelectionChanged += OnServerSelectionChanged;

        var listSection = new StackPanel
        {
            Spacing = 4,
            Children = { _serverListHeader, _serverList }
        };

        _formCard = new Border
        {
            IsVisible = _formExpanded,
            BorderThickness = new Thickness(1),
            BorderBrush = subtleBorder,
            Background = card,
            CornerRadius = new CornerRadius(radiusSm),
            Padding = new Thickness(12),
            Child = new StackPanel
            {
                Spacing = 14,
                Children = { inputSection, tunnelSection, listSection }
            }
        };

        // ── CTA — three mutually exclusive variants ─────────────────────
        // Disconnected (default visible): outlined accent
        _ctaConnect = new Avalonia.Controls.Button
        {
            Content = Localization.ButtonConnect,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 12),
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Background = card,
            Foreground = accentFg,
            BorderBrush = accentBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radiusSm),
            IsVisible = true,
        };
        _ctaConnect.Click += OnConnectClicked;

        // Connecting: sunken disabled
        _ctaConnecting = new Avalonia.Controls.Button
        {
            Content = Localization.ButtonConnecting,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 12),
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Background = sunken,
            Foreground = textSecondary,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(radiusSm),
            IsEnabled = false,
            IsVisible = false,
        };

        // Connected: accent solid (bg blue, text white) — per design NOT red
        _ctaDisconnect = new Avalonia.Controls.Button
        {
            Content = Localization.ButtonDisconnect,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 12),
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Background = accentSolid,
            Foreground = accentOnSolid,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(radiusSm),
            IsVisible = false,
        };
        _ctaDisconnect.Click += OnConnectClicked;

        // ── Расширенные настройки card (placeholder navigation) ─────────
        _advCardTitle = new TextBlock
        {
            Text = Localization.SmpAdvCardTitle,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = textPrimary,
        };
        _advCardSubtitle = new TextBlock
        {
            Text = Localization.SmpAdvCardSubtitle,
            FontSize = 9,
            Foreground = textMuted,
            TextWrapping = TextWrapping.Wrap,
        };
        var chevronCircle = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(radiusSm),
            Background = accentBgSubtle,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "›",
                FontSize = 15,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = accentFg,
            }
        };
        var advGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 10,
            Margin = new Thickness(14, 12),
        };
        var advText = new StackPanel
        {
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _advCardTitle, _advCardSubtitle }
        };
        Grid.SetColumn(advText, 0);
        advGrid.Children.Add(advText);
        Grid.SetColumn(chevronCircle, 1);
        advGrid.Children.Add(chevronCircle);
        var advCardButton = new Avalonia.Controls.Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(1),
            BorderBrush = defaultBorder,
            Background = card,
            CornerRadius = new CornerRadius(radiusMd),
            Content = advGrid,
        };
        advCardButton.Click += OnAdvCardClicked;

        // ── Inner stack with all sections, max 420 wide on tablets ──────
        var innerStack = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                statusCard,
                configRowButton,
                _formCard,
                _ctaConnect,
                _ctaConnecting,
                _ctaDisconnect,
                advCardButton,
            }
        };

        var innerWrapper = new Grid
        {
            MaxWidth = 420,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { innerStack }
        };

        var outerGrid = new Grid
        {
            Margin = new Thickness(16, 0, 16, 0),
            Children = { innerWrapper }
        };

        var contentStack = new StackPanel
        {
            Spacing = 0,
            Children = { headerRow, outerGrid }
        };

        return new ScrollViewer
        {
            Content = contentStack,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 0, 16),
            Background = bg,
        };
    }

    /// <summary>
    /// Phase 4 — pill-style status chip (rounded background + colored
    /// label) for the sub-header VPN/Zapret/TG indicators. Mirrors
    /// desktop's chip pattern from MainWindow.axaml header.
    /// </summary>
    private TextBlock MakeChip(string label, IBrush bg, IBrush fg)
    {
        // Wrapped Border preferred for rounded corners, but Avalonia
        // TextBlock + StackPanel layout is simpler for now. Return a
        // TextBlock styled as a tag — uses parent StackPanel's width.
        // Note: chips render as boxes, not pills, on this font size;
        // looks similar enough on phone screen at 9pt.
        return new TextBlock
        {
            Text = label,
            FontSize = 9,
            FontWeight = FontWeight.SemiBold,
            Foreground = fg,
            Background = bg,
            Padding = new Thickness(7, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private Avalonia.Controls.Button StyledSecondaryButton(string label)
    {
        return new Avalonia.Controls.Button
        {
            Content = label,
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Padding = new Thickness(14, 7),
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            Background = GetBrush("SurfaceRaisedBrush"),
            Foreground = GetBrush("TextPrimaryBrush"),
            BorderBrush = GetBrush("BorderDefaultBrush"),
            BorderThickness = new Thickness(1),
        };
    }

    // ── Event handlers ─────────────────────────────────────────────────

    private void OnConfigRowClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _formExpanded = !_formExpanded;
        if (_formCard is not null) _formCard.IsVisible = _formExpanded;
        if (_configRowChevron is not null) _configRowChevron.Text = _formExpanded ? "⌄" : "›";
    }

    private void OnConnectClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var activity = MainActivity.Instance;
        if (activity is null) return;
        if (MainActivity.IntendedConnected) activity.RequestDisconnect();
        else activity.RequestConnect();
    }

    private void OnIntentChanged(bool connected)
    {
        Dispatcher.UIThread.Post(() => UpdateConnectionState(connected));
    }

    private void UpdateConnectionState(bool connected)
    {
        if (_statusDot is null) return;

        if (connected)
        {
            _statusDot.Fill = GetBrush("SuccessSolidBrush");
            if (_statusTitle is not null) _statusTitle.Text = Localization.SimpleStatusTitleOn;
            if (_statusDesc is not null) _statusDesc.Text = Localization.SimpleStatusDescOn;
            if (_ctaConnect is not null) _ctaConnect.IsVisible = false;
            if (_ctaConnecting is not null) _ctaConnecting.IsVisible = false;
            if (_ctaDisconnect is not null) _ctaDisconnect.IsVisible = true;
        }
        else
        {
            _statusDot.Fill = GetBrush("TextMutedBrush");
            if (_statusTitle is not null) _statusTitle.Text = Localization.SimpleStatusTitleOff;
            if (_statusDesc is not null) _statusDesc.Text = Localization.SimpleStatusDescOff;
            if (_ctaConnect is not null) _ctaConnect.IsVisible = true;
            if (_ctaConnecting is not null) _ctaConnecting.IsVisible = false;
            if (_ctaDisconnect is not null) _ctaDisconnect.IsVisible = false;
        }
        UpdateConfigSummary();
    }

    private void UpdateConfigSummary()
    {
        if (_configRowValue is null) return;
        var mode = _fullRadio?.IsChecked == true ? Localization.SmpFullOption : Localization.SmpSplitOption;
        var src = AndroidStorage.GetSubscriptionUrl() != null ? Localization.SmpSourceSubscription : Localization.SmpSourceManual;
        _configRowValue.Text = $"{src} · {mode.ToLower()}";
    }

    private void OnSaveClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_serverInput is null || _serverInputError is null) return;
        var raw = (_serverInput.Text ?? string.Empty).Trim();
        _serverInputError.IsVisible = false;

        if (string.IsNullOrWhiteSpace(raw))
        {
            AndroidStorage.SetVlessUri(null);
            AndroidStorage.SetSubscriptionUrl(null);
            AndroidStorage.SetServers(null);
            AndroidStorage.SetSelectedServerName(null);
            _cachedServers = new List<VlessServerEntry>();
            UpdateServerListView();
            UpdateConfigSummary();
            return;
        }

        if (raw.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var parsed = VlessUriParser.Parse(raw);
                if (string.IsNullOrEmpty(parsed.Server) || parsed.Port <= 0)
                {
                    _serverInputError.Text = Localization.SaveStatusUriBadHost;
                    _serverInputError.IsVisible = true;
                    return;
                }
                AndroidStorage.SetVlessUri(raw);
                AndroidStorage.SetSubscriptionUrl(null);
                AndroidStorage.SetServers(null);
                AndroidStorage.SetSelectedServerName(null);
                _cachedServers = new List<VlessServerEntry>();
                UpdateServerListView();
                UpdateConfigSummary();
            }
            catch (Exception ex)
            {
                _serverInputError.Text = string.Format(Localization.SaveStatusUriInvalid, ex.Message);
                _serverInputError.IsVisible = true;
            }
            return;
        }

        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            AndroidStorage.SetSubscriptionUrl(raw);
            AndroidStorage.SetVlessUri(null);
            UpdateConfigSummary();
            return;
        }

        _serverInputError.Text = Localization.SaveStatusUnknown;
        _serverInputError.IsVisible = true;
    }

    private async void OnRefreshClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_serverInputError is null) return;
        var url = AndroidStorage.GetSubscriptionUrl();
        if (string.IsNullOrEmpty(url) && _serverInput is not null)
        {
            var raw = (_serverInput.Text ?? string.Empty).Trim();
            if (raw.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                AndroidStorage.SetSubscriptionUrl(raw);
                url = raw;
            }
        }
        if (string.IsNullOrWhiteSpace(url))
        {
            _serverInputError.Text = Localization.RefreshNeedsUrl;
            _serverInputError.IsVisible = true;
            return;
        }

        _serverInputError.IsVisible = false;
        try
        {
            var servers = await SubscriptionFetcher.FetchAsync(url, logger: null, ct: System.Threading.CancellationToken.None).ConfigureAwait(true);
            var list = new List<VlessServerEntry>(servers);
            AndroidStorage.SetServers(list);
            _cachedServers = list;
            UpdateServerListView();
            var prevSelected = AndroidStorage.GetSelectedServerName();
            var hasPrev = !string.IsNullOrEmpty(prevSelected) &&
                          list.Exists(s => string.Equals(s.Name, prevSelected, StringComparison.OrdinalIgnoreCase));
            if (!hasPrev && list.Count > 0)
            {
                AndroidStorage.SetSelectedServerName(list[0].Name);
                if (_serverList is not null) _serverList.SelectedIndex = 0;
            }
            UpdateConfigSummary();
        }
        catch (Exception ex)
        {
            _serverInputError.Text = string.Format(Localization.RefreshFailed, ex.Message);
            _serverInputError.IsVisible = true;
        }
    }

    private void OnServerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_serverList?.SelectedItem is VlessServerEntry entry)
            AndroidStorage.SetSelectedServerName(entry.Name);
    }

    private void ReloadServerList()
    {
        _cachedServers = AndroidStorage.GetServers();
        UpdateServerListView();
    }

    private void UpdateServerListView()
    {
        if (_serverList is null || _serverListHeader is null) return;
        var visible = _cachedServers.Count > 0;
        _serverList.IsVisible = visible;
        _serverListHeader.IsVisible = visible;
        _serverList.ItemsSource = _cachedServers;
        _serverList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<VlessServerEntry>(
            (item, _) =>
            {
                var name = new TextBlock
                {
                    Text = string.IsNullOrEmpty(item?.Name) ? (item?.Server ?? "?") : item.Name,
                    FontSize = 12,
                    FontWeight = FontWeight.Medium,
                    Foreground = GetBrush("TextPrimaryBrush"),
                };
                var sub = new TextBlock
                {
                    Text = $"{item?.Server}:{item?.Port}  ·  {item?.Protocol ?? "vless"}",
                    FontSize = 10,
                    Foreground = GetBrush("TextMutedBrush"),
                };
                return new StackPanel
                {
                    Spacing = 2,
                    Margin = new Thickness(8, 6),
                    Children = { name, sub }
                };
            }, supportsRecycling: true);
        var sel = AndroidStorage.GetSelectedServerName();
        if (!string.IsNullOrEmpty(sel))
        {
            for (int i = 0; i < _cachedServers.Count; i++)
            {
                if (string.Equals(_cachedServers[i].Name, sel, StringComparison.OrdinalIgnoreCase))
                {
                    _serverList.SelectedIndex = i;
                    break;
                }
            }
        }
    }

    private void OnAdvCardClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Phase 3 placeholder: would open an Advanced settings page on Android.
        // For now expand the inline form (already exists).
        if (!_formExpanded) OnConfigRowClicked(sender, e);
    }

    private void OnScanQrClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_serverInputError is null) return;
        _serverInputError.Text = Localization.QrComingSoon;
        _serverInputError.IsVisible = true;
    }

    // ── Header kebab menu ──────────────────────────────────────────────

    private void OnKebabMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is null) return;
        _kebabPopup.IsOpen = !_kebabPopup.IsOpen;
    }

    private void OnMenuLanguageClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        ToggleLanguageAndRefresh();
    }

    private void OnMenuThemeClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        // Phase 4: cycle dark/light themes. Re-open after toggle requires
        // app restart for full repaint (Avalonia 11 supports live theme
        // change but our code-behind view caches brushes — Phase 5 will
        // wire DynamicResource for live update; for now we update prefs
        // and the next launch picks up the new theme).
        var current = AndroidStorage.GetTheme();
        var next = current == "dark" ? "light" : "dark";
        AndroidStorage.SetTheme(next);
        // Apply immediately to RequestedThemeVariant — most controls
        // pick this up live.
        RequestedThemeVariant = next == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    private void ToggleLanguageAndRefresh()
    {
        Localization.ToggleAndPersist();
        if (_brandTitle is not null) _brandTitle.Text = Localization.BrandTitle;
        if (_menuLanguageItem is not null) _menuLanguageItem.Content = Localization.MenuLanguageLabel;
        if (_menuThemeItem is not null) _menuThemeItem.Content = Localization.MenuThemeLabel;
        if (_statusTitle is not null)
            _statusTitle.Text = MainActivity.IntendedConnected ? Localization.SimpleStatusTitleOn : Localization.SimpleStatusTitleOff;
        if (_statusDesc is not null)
            _statusDesc.Text = MainActivity.IntendedConnected ? Localization.SimpleStatusDescOn : Localization.SimpleStatusDescOff;
        if (_configRowLabel is not null) _configRowLabel.Text = Localization.SmpConfigRowLabel;
        if (_serverInputLabel is not null) _serverInputLabel.Text = Localization.SmpInputLabel;
        if (_serverInput is not null) _serverInput.Watermark = Localization.SmpInputWatermark;
        if (_serverInputHint is not null) _serverInputHint.Text = Localization.SmpInputHint;
        if (_tunnelModeLabel is not null) _tunnelModeLabel.Text = Localization.SmpTunnelModeLabel;
        if (_splitLabel is not null) _splitLabel.Text = Localization.SmpSplitOption;
        if (_splitHint is not null) _splitHint.Text = Localization.SmpSplitHint;
        if (_fullLabel is not null) _fullLabel.Text = Localization.SmpFullOption;
        if (_fullHint is not null) _fullHint.Text = Localization.SmpFullHint;
        if (_serverListHeader is not null) _serverListHeader.Text = Localization.AvailableServers;
        if (_advCardTitle is not null) _advCardTitle.Text = Localization.SmpAdvCardTitle;
        if (_advCardSubtitle is not null) _advCardSubtitle.Text = Localization.SmpAdvCardSubtitle;
        if (_ctaConnect is not null) _ctaConnect.Content = Localization.ButtonConnect;
        if (_ctaConnecting is not null) _ctaConnecting.Content = Localization.ButtonConnecting;
        if (_ctaDisconnect is not null) _ctaDisconnect.Content = Localization.ButtonDisconnect;
        UpdateConfigSummary();
    }

    /// <summary>Pre-Phase-4 entry point retained so any stale subscribers
    /// don't break — delegates to the new ToggleLanguageAndRefresh.</summary>
    private void OnLanguageToggleClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ToggleLanguageAndRefresh();
}
