using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Android;

/// <summary>
/// v3.0 Phase 8.2 (2026-05-07) — code-side equivalent of XAML's
/// <c>{DynamicResource KeyName}</c>. Used to wire token brushes to
/// Avalonia controls built in code-behind so they auto-repaint on
/// <c>Application.RequestedThemeVariant</c> change without manually
/// walking the visual tree.
///
/// <para><see cref="DynamicResourceExtension"/> implements
/// <see cref="IBinding"/>, so handing it to <c>AvaloniaObjectExtensions.Bind</c>
/// installs a live binding that resolves the resource through the
/// element's logical parent chain and re-resolves on theme change.</para>
/// </summary>
internal static class StyledElementResourceExtensions
{
    /// <summary>
    /// Bind <paramref name="prop"/> on <paramref name="element"/> to the
    /// dynamic resource at <paramref name="key"/>. Returns the element
    /// for fluent chaining. Replaces any prior binding at the same
    /// property+priority.
    /// </summary>
    public static T BindToken<T>(this T element, AvaloniaProperty prop, string key)
        where T : AvaloniaObject
    {
        element.Bind(prop, new DynamicResourceExtension(key));
        return element;
    }
}

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
    // v3.0 Phase 8.2 (2026-05-07) — Image is invariant under DynamicResource
    // because Bitmap source is bytes, not a brush. Theme switch must
    // re-call LoadMascot() to get the inverted Bgra8888 variant. Stored
    // in a field so ApplyTheme(string) can flip Source.
    private Image? _mascotImage;
    private Avalonia.Controls.Button? _kebabMenuButton;
    private Popup? _kebabPopup;
    // v3.0 Phase 7.3 — segmented control buttons (RU|EN, Light|Dark)
    // replacing the v3.0 Phase 4 single-toggle buttons. User flagged
    // 2026-05-04: "toogle на android отличаеться от pc версии".
    // Desktop (MainWindow.axaml:430-459) has 2-segment grids that
    // SET a specific value rather than toggle. Android now mirrors.
    private Avalonia.Controls.Button? _menuLangRu;
    private Avalonia.Controls.Button? _menuLangEn;
    private Avalonia.Controls.Button? _menuThemeLight;
    private Avalonia.Controls.Button? _menuThemeDark;
    // Phase 7.2 — additional menu items (Diagnostics + Troubleshooting + About)
    private Avalonia.Controls.Button? _menuOpenLogItem;
    private Avalonia.Controls.Button? _menuCopyLogPathItem;
    private Avalonia.Controls.Button? _menuUpdateCheckItem;
    private Avalonia.Controls.Button? _menuResetSettingsItem;
    private Avalonia.Controls.Button? _menuVersionItem;
    private Avalonia.Controls.Button? _menuRepoItem;
    // v2.32.0 — Free Configs entry point lives in the kebab menu (no
    // dedicated tab on Android — single-screen layout).
    private Avalonia.Controls.Button? _menuFreeConfigsItem;
    // Localized section header TextBlocks — kept so language toggle can refresh them.
    private TextBlock? _menuSectionView;
    private TextBlock? _menuSectionDiagnostics;
    private TextBlock? _menuSectionTroubleshooting;
    private TextBlock? _menuSectionAbout;
    private TextBlock? _menuSectionFreeConfigs;
    // Tracks Reset confirm flow: first tap → confirm prompt, second tap → wipe.
    private bool _resetConfirmPending = false;
    // Banner that surfaces transient kebab-menu feedback (Update toast,
    // log-path copied, settings reset done, etc.) without a real Snackbar.
    private TextBlock? _menuFeedback;

    // v3.0 Phase 7.4 — in-app log viewer overlay. Shown when user taps
    // Diagnostics > "Открыть лог" / "Open log". Reads last 50 KB of
    // singbox.log into a monospace ScrollViewer. Closed via × button.
    private Border? _logOverlay;
    private TextBlock? _logViewerContent;
    private TextBlock? _logViewerEmptyState;
    private ScrollViewer? _logViewerScroller;
    private TextBlock? _logViewerTitle;
    private Avalonia.Controls.Button? _logViewerCloseBtn;
    private Avalonia.Controls.Button? _logViewerRefreshBtn;

    // v2.32.0 — Settings overlay mirroring desktop NetworkPage 4 sub-sections
    // (Routing / Leak protection / Updates / Autostart). Triggered from kebab
    // menu Diagnostics > "Настройки" / "Settings". Same fullscreen Border
    // overlay pattern as Phase 7.4 log viewer + 7.5 per-app picker.
    private Border? _settingsOverlay;
    private Avalonia.Controls.RadioButton? _settingsSplitRadio;
    private Avalonia.Controls.RadioButton? _settingsFullRadio;
    private Avalonia.Controls.CheckBox? _settingsBypassRu;
    private Avalonia.Controls.CheckBox? _settingsBlockOnVpnFail;
    private Avalonia.Controls.ComboBox? _settingsDnsStrategy;
    private Avalonia.Controls.CheckBox? _settingsReceivePrereleases;
    private TextBlock? _settingsCurrentVersion;
    private Avalonia.Controls.CheckBox? _settingsAutostartVpn;
    private Avalonia.Controls.CheckBox? _settingsAutostartZapret;
    private Avalonia.Controls.CheckBox? _settingsAutostartTgProxy;
    private Avalonia.Controls.Button? _menuSettingsItem;
    private bool _settingsLoading = false;

    // v3.0 Phase 7.5 — per-app filter picker overlay (handbook §5.5).
    // Tap "Selected apps" radio → "Choose apps…" button → this overlay.
    // ListBox of installed apps with a search filter + system-apps
    // toggle. CheckBox per row, indeterminate state shown only during
    // initial Set() seeding (~50 ms).
    private Border? _appPickerOverlay;
    private TextBox? _appPickerSearch;
    private Avalonia.Controls.CheckBox? _appPickerSystemToggle;
    private TextBlock? _appPickerCount;
    private ListBox? _appPickerList;
    private Avalonia.Controls.Button? _appPickerSaveBtn;
    private Avalonia.Controls.Button? _appPickerCloseBtn;
    private Avalonia.Controls.Button? _perAppPickButton;
    private TextBlock? _perAppCountLabel;
    private List<AppListLoader.AppEntry> _appPickerCache = new();
    private HashSet<string> _appPickerSelected = new(System.StringComparer.OrdinalIgnoreCase);
    private bool _appPickerSystemAppsVisible = false;
    // v3.0 v2.32.0 (2026-05-07) — exclude-mode UI inside the picker
    // overlay. Storage already round-trips "include" / "exclude" through
    // VpnRouterService.java's addAllowedApplication / addDisallowedApplication
    // branches; this is the missing UI surface that lets a user pick.
    // The two segment buttons sit above the search box; selection drives
    // the hint TextBlock below them and the count label on the form.
    private Avalonia.Controls.Button? _appPickerModeIncludeBtn;
    private Avalonia.Controls.Button? _appPickerModeExcludeBtn;
    private TextBlock? _appPickerModeLabel;
    private TextBlock? _appPickerModeHint;
    private string _appPickerMode = "include";

    // State
    private bool _formExpanded = false;
    private List<VlessServerEntry> _cachedServers = new();

    /// <summary>
    /// v3.0 Phase 7.1 (2026-05-04) — chip semantic state. Mirrors desktop's
    /// status-chip pattern (`On` = green, `Connecting` = yellow + pulse,
    /// `Off` = gray). Pre-7.1 chips were static decoration; user requested
    /// they reflect the real connection lifecycle:
    /// <list type="bullet">
    ///   <item><c>VPN</c>: Off → user taps Connect → Connecting → tunnel
    ///   broadcast UP → On. Reverts to Off on TUNNEL_DOWN / TUNNEL_ERROR.</item>
    ///   <item><c>Zapret</c>, <c>TG</c>: stay Off (Android port doesn't
    ///   support those features yet — chips reserved for parity with
    ///   desktop layout).</item>
    /// </list>
    /// </summary>
    private enum ChipState { Off, Connecting, On }
    private ChipState _vpnChipState = ChipState.Off;
    private System.Threading.CancellationTokenSource? _vpnPulseCts;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Localization.LoadFromStorage();
        ApplyTheme();

        if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime singleView)
        {
            // v3.0 Phase 7.3 (handbook §5.4) — auto-expand the config form
            // on first launch when nothing has been configured yet, so
            // user can paste their VPN URI without an extra tap on the
            // chevron. Mirrors desktop's first-launch behaviour. If the
            // user has either a manual URI or a subscription saved, keep
            // the form collapsed (default).
            var hasManual = !string.IsNullOrEmpty(AndroidStorage.GetVlessUri());
            var hasSubscription = !string.IsNullOrEmpty(AndroidStorage.GetSubscriptionUrl());
            _formExpanded = !hasManual && !hasSubscription;

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

    // v3.0 Phase 8.2 (2026-05-07) — most brushes in BuildSimplePageView
    // now ride BindToken (DynamicResource) so theme switches auto-repaint.
    // The helper below remains for the AndroidApp.SubscribePage /
    // AndroidApp.FreeConfigs partials (AND-1 / AND-3 ports) — they were
    // merged before Phase 8.2 landed and snapshot the brushes at build
    // time. Migrating those call sites to BindToken is a follow-up; for
    // now this keeps them building. GetRadius stays unchanged because
    // radii are theme-invariant.
    private IBrush GetBrush(string key)
    {
        if (Resources.TryGetResource(key, ActualThemeVariant, out var v) && v is IBrush b)
            return b;
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
        // v3.0 Phase 8.2 (2026-05-07) — every Background / Foreground /
        // BorderBrush / Fill below goes through BindToken (DynamicResource)
        // so theme switches auto-repaint the visual tree. Cached brush
        // locals from pre-8.2 are gone; only the radii (theme-invariant)
        // stay as locals.
        var radiusXs = GetRadius("RadiusXs");
        var radiusSm = GetRadius("RadiusSm");
        var radiusMd = GetRadius("RadiusMd");

        // ── Sub-header (mascot + brand + chips + kebab menu) ────────────
        // v3.0 Phase 4 (2026-05-04) — desktop parity. Pre-4 had a plain
        // "VPNRouter" title with a "RU" toggle pill at right. Desktop
        // shows: mascot 🐧 + "Virtual Penguin Network" bold + three
        // status chips (VPN / Zapret / TG) + ⋯ kebab menu. The kebab
        // hosts language + theme toggles (was inline RU pill).

        // v3.0 Phase 5 — real PNG mascot with theme-aware RGB inversion.
        // Mirrors desktop's MainWindowViewModel.LogoSource pattern:
        //   - Light theme: penguin_mascot.png as-is (black lineart on
        //     transparent bg)
        //   - Dark theme: RGB-inverted copy (white lineart on transparent)
        // Inversion preserves alpha so anti-aliased edges stay clean.
        // v3.0 Phase 8.2 — store on field so ApplyTheme(string) can call
        // _mascotImage.Source = LoadMascot() to switch between original
        // and RGB-inverted bitmap variants.
        _mascotImage = new Image
        {
            Source = LoadMascot(),
            Stretch = Stretch.Uniform,
            Width = 26,
            Height = 26,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapInterpolationMode(_mascotImage, BitmapInterpolationMode.HighQuality);
        var mascot = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true,
            Child = _mascotImage,
        };
        mascot.BindToken(Border.BackgroundProperty, "AccentBgSubtleBrush");

        _brandTitle = new TextBlock
        {
            Text = Localization.BrandTitle,
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _brandTitle.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        // v3.0 Phase 7.1 — start all chips in Off state. VPN chip transitions
        // through Connecting → On as the tunnel comes up. Zapret + TG stay Off
        // because those features aren't ported yet.
        // v3.0 Phase 8.2 — chips ride DynamicResource via MakeChip's key
        // parameters so they auto-repaint on theme variant change.
        _vpnChip = MakeChip("VPN", "SurfaceSunkenBrush", "TextMutedBrush");
        _zapretChip = MakeChip("Zapret", "SurfaceSunkenBrush", "TextMutedBrush");
        _tgChip = MakeChip("TG", "SurfaceSunkenBrush", "TextMutedBrush");

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
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _kebabMenuButton.BindToken(Avalonia.Controls.Button.ForegroundProperty, "TextSecondaryBrush");
        _kebabMenuButton.Click += OnKebabMenuClicked;

        // v3.0 Phase 7.2 + 7.3 (2026-05-04) — full kebab menu with 4 sections
        // mirroring desktop's MainWindow.axaml ContextMenu (lines 414-512).
        //   • Вид           — Theme segmented (Light|Dark) + Language segmented (RU|EN)
        //   • Диагностика   — Open log / Copy log path / Update check
        //   • Устранение    — Reset settings (with confirm step)
        //   • О приложении  — Version + GitHub repo link
        // 7.3 swap (user-flagged "toogle на android отличаеться от pc"):
        // single-toggle buttons → 2-segment grids that SET a value
        // directly (idempotent — clicking the active segment is a no-op).

        // Theme segmented row: Light | Dark
        var isDark = AndroidStorage.GetTheme() == "dark";
        _menuThemeLight = MakeSegmentButton(Localization.MenuSegLight, !isDark, OnMenuThemeLightClicked);
        _menuThemeDark  = MakeSegmentButton(Localization.MenuSegDark,   isDark,  OnMenuThemeDarkClicked);
        var themeRow = MakeSegmentRow(_menuThemeLight, _menuThemeDark);

        // Language segmented row: RU | EN
        _menuLangRu = MakeSegmentButton(Localization.MenuSegRu, Localization.Ru, OnMenuLangRuClicked);
        _menuLangEn = MakeSegmentButton(Localization.MenuSegEn, !Localization.Ru, OnMenuLangEnClicked);
        var langRow = MakeSegmentRow(_menuLangRu, _menuLangEn);

        // Diagnostics + Troubleshooting + About items stay as full-width
        // labelled buttons.
        // v2.32.0 — "Настройки" / "Settings" added to Diagnostics so the user
        // can reach the 4-section Settings overlay (mirrors desktop NetworkPage:
        // Routing / Leak / Updates / Autostart) without scrolling past the
        // collapsed form. Listed first in the section as the most-used entry.
        _menuSettingsItem = MakeMenuItem(Localization.MenuItemSettings,
                                         "TextPrimaryBrush", OnMenuSettingsClicked);
        _menuOpenLogItem  = MakeMenuItem(Localization.MenuItemOpenLogs,
                                         "TextPrimaryBrush", OnMenuOpenLogClicked);
        _menuCopyLogPathItem = MakeMenuItem(Localization.MenuItemCopyLogPath,
                                            "TextPrimaryBrush", OnMenuCopyLogPathClicked);
        _menuUpdateCheckItem = MakeMenuItem(Localization.MenuItemUpdateCheck,
                                            "TextPrimaryBrush", OnMenuUpdateCheckClicked);
        _menuResetSettingsItem = MakeMenuItem(Localization.MenuItemResetSettings,
                                              "TextPrimaryBrush", OnMenuResetSettingsClicked);
        _menuVersionItem = MakeMenuItem(
            $"{Localization.MenuItemVersion} {VPNRouter.Core.AppVersion.Version}",
            "TextMutedBrush", null);
        _menuRepoItem = MakeMenuItem(Localization.MenuItemRepoLink,
                                     "TextPrimaryBrush", OnMenuRepoClicked);

        var menuStack = new StackPanel
        {
            Spacing = 0,
            MinWidth = 240,
        };

        // v2.32.0 — Free Configs entry. Sits between Вид and Диагностика
        // so it's discoverable without scrolling. Tap → close popup +
        // open the Free Configs overlay.
        _menuFreeConfigsItem = MakeMenuItem(Localization.MenuItemOpenFreeConfigs,
                                            "TextPrimaryBrush", OnMenuFreeConfigsClicked);

        AppendMenuSectionWithControls(menuStack, Localization.MenuSectionView,
                                      new Control[] { themeRow, langRow });
        AppendMenuSection(menuStack, Localization.MenuSectionFreeConfigs,
                          new[] { _menuFreeConfigsItem });
        AppendMenuSection(menuStack, Localization.MenuSectionDiagnostics,
                          new[] { _menuSettingsItem, _menuOpenLogItem, _menuCopyLogPathItem, _menuUpdateCheckItem });
        AppendMenuSection(menuStack, Localization.MenuSectionTroubleshooting,
                          new[] { _menuResetSettingsItem });
        AppendMenuSection(menuStack, Localization.MenuSectionAbout,
                          new[] { _menuVersionItem, _menuRepoItem });

        var menuPanel = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radiusSm),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 4,
                Blur = 12,
                Color = Color.FromArgb(50, 0, 0, 0),
            }),
            Padding = new Thickness(0, 4),
            Child = menuStack,
        };
        menuPanel.BindToken(Border.BackgroundProperty, "SurfaceBaseBrush");
        menuPanel.BindToken(Border.BorderBrushProperty, "BorderDefaultBrush");

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
        };
        // v3.0 Phase 8.2 — UpdateConnectionState re-binds Fill on every
        // state flip; the initial value tracks the Off state.
        _statusDot.BindToken(Avalonia.Controls.Shapes.Shape.FillProperty, "TextMutedBrush");

        _statusTitle = new TextBlock
        {
            Text = Localization.SimpleStatusTitleOff,
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _statusTitle.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _statusDesc = new TextBlock
        {
            Text = Localization.SimpleStatusDescOff,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20, 0, 0, 0),
            LineHeight = 16,
        };
        _statusDesc.BindToken(TextBlock.ForegroundProperty, "TextSecondaryBrush");

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
            CornerRadius = new CornerRadius(radiusMd),
            Padding = new Thickness(14),
            Child = new StackPanel
            {
                Spacing = 8,
                Children = { statusHeaderRow, _statusDesc },
            }
        };
        statusCard.BindToken(Border.BackgroundProperty, "SurfaceBaseBrush");
        statusCard.BindToken(Border.BorderBrushProperty, "BorderDefaultBrush");

        // ── Config row button (tappable, expands form) ──────────────────
        var flagGlyph = new TextBlock
        {
            Text = "⚑",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        flagGlyph.BindToken(TextBlock.ForegroundProperty, "AccentFgBrush");
        var flagIcon = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(radiusXs),
            VerticalAlignment = VerticalAlignment.Center,
            Child = flagGlyph,
        };
        flagIcon.BindToken(Border.BackgroundProperty, "AccentBgSubtleBrush");

        _configRowLabel = new TextBlock
        {
            Text = Localization.SmpConfigRowLabel,
            FontSize = 9,
            FontWeight = FontWeight.SemiBold,
        };
        _configRowLabel.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        _configRowValue = new TextBlock
        {
            Text = Localization.SimpleConfigSummary,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            FontFamily = new FontFamily("monospace"),
        };
        _configRowValue.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _configRowChevron = new TextBlock
        {
            // v3.0 Phase 7.3 — initial glyph follows _formExpanded so the
            // chevron points down when the form is auto-expanded on
            // first launch (mirrors OnConfigRowClicked's flip logic).
            Text = _formExpanded ? "⌄" : "›",
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _configRowChevron.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

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
            CornerRadius = new CornerRadius(radiusSm),
            Content = configRowGrid,
        };
        configRowButton.BindToken(Avalonia.Controls.Button.BackgroundProperty, "SurfaceRaisedBrush");
        configRowButton.BindToken(Avalonia.Controls.Button.BorderBrushProperty, "BorderSubtleBrush");
        configRowButton.Click += OnConfigRowClicked;

        // ── Collapsible form (input + tunnel mode radios + autostart) ───
        _serverInputLabel = new TextBlock
        {
            Text = Localization.SmpInputLabel,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
        };
        _serverInputLabel.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

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
            TextWrapping = TextWrapping.Wrap,
        };
        _serverInputHint.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        _serverInputError = new TextBlock
        {
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        _serverInputError.BindToken(TextBlock.ForegroundProperty, "DangerFgBrush");

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
        };
        _tunnelModeLabel.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _splitLabel = new TextBlock
        {
            Text = Localization.SmpSplitOption,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _splitLabel.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _splitRadio = new Avalonia.Controls.RadioButton
        {
            GroupName = "TunnelMode",
            // v3.0 Phase 7.5 — radio state seeded from stored per-app
            // mode. v2.32.0 expanded: any non-"off" mode (include OR
            // exclude) keeps split selected; the picker overlay refines
            // include vs exclude inside the split branch.
            IsChecked = AndroidStorage.GetPerAppMode() != "off",
            Content = _splitLabel,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            MinHeight = 0,
            Padding = new Thickness(4, 0),
        };
        _splitRadio.IsCheckedChanged += OnTunnelModeRadioChanged;
        _splitHint = new TextBlock
        {
            Text = Localization.SmpSplitHint,
            FontSize = 9,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, 0, 0, 0),
        };
        _splitHint.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
        _fullLabel = new TextBlock
        {
            Text = Localization.SmpFullOption,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _fullLabel.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _fullRadio = new Avalonia.Controls.RadioButton
        {
            GroupName = "TunnelMode",
            // v3.0 Phase 7.5 — full mode = mode == "off".
            // v2.32.0: was `!= "include"` which silently selected full when
            // mode was "exclude" — wrong, exclude is still split.
            IsChecked = AndroidStorage.GetPerAppMode() == "off",
            Content = _fullLabel,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            MinHeight = 0,
            Padding = new Thickness(4, 0),
        };
        _fullRadio.IsCheckedChanged += OnTunnelModeRadioChanged;
        _fullHint = new TextBlock
        {
            Text = Localization.SmpFullHint,
            FontSize = 9,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, 0, 0, 0),
        };
        _fullHint.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        // v3.0 Phase 7.5 — "Choose apps…" button + selection counter
        // pair, only visible when "Selected apps" radio is checked.
        // Tap → opens the app picker overlay defined later in this file.
        _perAppPickButton = StyledSecondaryButton(Localization.PerAppPickButton);
        _perAppPickButton.Click += OnPerAppPickButtonClicked;
        _perAppPickButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        _perAppPickButton.Margin = new Thickness(24, 4, 0, 0);

        var initialPerAppCount = AndroidStorage.GetPerAppPackages().Count;
        var initialMode = AndroidStorage.GetPerAppMode();
        var initialCountFmt = initialMode == "exclude"
            ? Localization.PerAppCountExclude
            : Localization.PerAppCountInclude;
        _perAppCountLabel = new TextBlock
        {
            Text = string.Format(initialCountFmt, initialPerAppCount),
            FontSize = 9,
            Margin = new Thickness(24, 2, 0, 0),
        };
        _perAppCountLabel.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        var perAppStack = new StackPanel
        {
            Spacing = 0,
            // v2.32.0: visible whenever split is on (mode != off), not just
            // include. Exclude mode also needs the "Choose apps…" button.
            IsVisible = AndroidStorage.GetPerAppMode() != "off",
            Children = { _perAppPickButton, _perAppCountLabel },
        };
        // Tag the stack so OnTunnelModeRadioChanged can flip its
        // visibility — using Tag avoids storing yet another field.
        _splitRadio.Tag = perAppStack;

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
                        new StackPanel { Spacing = 1, Children = { _splitRadio, _splitHint, perAppStack } },
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
            IsVisible = false,
        };
        _serverListHeader.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");
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
            CornerRadius = new CornerRadius(radiusSm),
            Padding = new Thickness(12),
            Child = new StackPanel
            {
                Spacing = 14,
                Children = { inputSection, tunnelSection, listSection }
            }
        };
        _formCard.BindToken(Border.BackgroundProperty, "SurfaceBaseBrush");
        _formCard.BindToken(Border.BorderBrushProperty, "BorderSubtleBrush");

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
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radiusSm),
            IsVisible = true,
        };
        _ctaConnect.BindToken(Avalonia.Controls.Button.BackgroundProperty, "SurfaceBaseBrush");
        _ctaConnect.BindToken(Avalonia.Controls.Button.ForegroundProperty, "AccentFgBrush");
        _ctaConnect.BindToken(Avalonia.Controls.Button.BorderBrushProperty, "AccentBorderBrush");
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
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(radiusSm),
            IsEnabled = false,
            IsVisible = false,
        };
        _ctaConnecting.BindToken(Avalonia.Controls.Button.BackgroundProperty, "SurfaceSunkenBrush");
        _ctaConnecting.BindToken(Avalonia.Controls.Button.ForegroundProperty, "TextSecondaryBrush");

        // Connected: accent solid (bg blue, text white) — per design NOT red
        _ctaDisconnect = new Avalonia.Controls.Button
        {
            Content = Localization.ButtonDisconnect,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 12),
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(radiusSm),
            IsVisible = false,
        };
        _ctaDisconnect.BindToken(Avalonia.Controls.Button.BackgroundProperty, "AccentSolidBrush");
        _ctaDisconnect.BindToken(Avalonia.Controls.Button.ForegroundProperty, "AccentOnSolidBrush");
        _ctaDisconnect.Click += OnConnectClicked;

        // ── Расширенные настройки card (placeholder navigation) ─────────
        _advCardTitle = new TextBlock
        {
            Text = Localization.SmpAdvCardTitle,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
        };
        _advCardTitle.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _advCardSubtitle = new TextBlock
        {
            Text = Localization.SmpAdvCardSubtitle,
            FontSize = 9,
            TextWrapping = TextWrapping.Wrap,
        };
        _advCardSubtitle.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
        var chevronGlyph = new TextBlock
        {
            Text = "›",
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        chevronGlyph.BindToken(TextBlock.ForegroundProperty, "AccentFgBrush");
        var chevronCircle = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(radiusSm),
            VerticalAlignment = VerticalAlignment.Center,
            Child = chevronGlyph,
        };
        chevronCircle.BindToken(Border.BackgroundProperty, "AccentBgSubtleBrush");
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
            CornerRadius = new CornerRadius(radiusMd),
            Content = advGrid,
        };
        advCardButton.BindToken(Avalonia.Controls.Button.BackgroundProperty, "SurfaceBaseBrush");
        advCardButton.BindToken(Avalonia.Controls.Button.BorderBrushProperty, "BorderDefaultBrush");
        advCardButton.Click += OnAdvCardClicked;

        // v3.0 Phase 7.2 — transient feedback banner that surfaces the
        // result of kebab-menu actions (log path copied, settings reset,
        // update placeholder). Hidden by default; ShowMenuFeedback shows
        // for ~3 s then hides.
        _menuFeedback = new TextBlock
        {
            Text = string.Empty,
            FontSize = 11,
            Padding = new Thickness(12, 8),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        _menuFeedback.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
        _menuFeedback.BindToken(TextBlock.BackgroundProperty, "SurfaceSunkenBrush");

        // ── Inner stack with all sections, max 420 wide on tablets ──────
        var innerStack = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                statusCard,
                _menuFeedback,
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

        var mainScroller = new ScrollViewer
        {
            Content = contentStack,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 0, 16),
        };
        mainScroller.BindToken(ScrollViewer.BackgroundProperty, "SurfaceAppBrush");

        // v3.0 Phase 7.4 (2026-05-04) — fullscreen log-viewer overlay
        // sits on top of the main content stack. Hidden by default; the
        // Diagnostics > "Open log" menu action reads singbox.log into
        // _logViewerContent and flips IsVisible=true.
        _logOverlay = BuildLogOverlay();
        // v3.0 Phase 7.5 (2026-05-04) — fullscreen per-app picker
        // overlay. Triggered from the "Choose apps…" button in the form.
        _appPickerOverlay = BuildAppPickerOverlay();
        // v2.32.0 (AND-1) — fullscreen Subscribe overlay (multi-
        // subscription parity with desktop SubscribePage). Triggered from
        // the "Расширенные настройки" advanced card. Defined in
        // AndroidApp.SubscribePage.cs partial.
        _subsOverlay = BuildSubsOverlay();

        // v2.32.0 (AND-3) — fullscreen Free Configs overlay. Triggered from the
        // "Бесплатные конфиги" / "Free configs" entry in the kebab menu.
        // See AndroidApp.FreeConfigs.cs + plans/v2.32.0-android-free-configs.md.
        _fcOverlay = BuildFreeConfigsOverlay();

        // v2.32.0 (AND-2) — fullscreen Settings overlay (4-section parity with
        // desktop NetworkPage). Triggered from kebab > Diagnostics > "Настройки".
        _settingsOverlay = BuildSettingsOverlay();

        return new Grid
        {
            Children = { mainScroller, _logOverlay, _appPickerOverlay, _subsOverlay, _fcOverlay, _settingsOverlay }
        };
    }

    /// <summary>
    /// v3.0 Phase 7.4 (2026-05-04) — build the in-app log viewer overlay.
    /// Layout: top title bar (× close, refresh, "singbox.log" title) +
    /// a horizontally + vertically scrollable monospace TextBlock that
    /// renders the last ~50 KB of the log file. Closes the handbook §5.6
    /// gap (in-app logs viewer) so users can debug without adb.
    /// </summary>
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

    // ── v2.32.0 Settings overlay (mirrors desktop NetworkPage) ──────────
    //
    // Fullscreen Border layered over the main ScrollViewer (same pattern as
    // Phase 7.4 log viewer). 4 stacked sub-sections in the order they
    // appear on desktop: Routing → Leak protection → Updates → Autostart.
    // Each control wires straight to AndroidStorage on change so there's
    // no Apply button — autosave matches the desktop NetworkPage's
    // "Auto-saved" footer behaviour (Strings.SettingsAutosaved).

    private Border BuildSettingsOverlay()
    {
        // Title bar — title text + close button. Same shape as log viewer.
        var titleText = new TextBlock
        {
            Text = Localization.SettingsTitle,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = GetBrush("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var closeBtn = new Avalonia.Controls.Button
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
            Foreground = GetBrush("TextSecondaryBrush"),
        };
        closeBtn.Click += OnSettingsCloseClicked;

        var titleBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(8, 4, 4, 4),
        };
        Grid.SetColumn(titleText, 0);
        Grid.SetColumn(closeBtn, 1);
        titleBar.Children.Add(titleText);
        titleBar.Children.Add(closeBtn);

        var titleBarBorder = new Border
        {
            Background = GetBrush("SurfaceRaisedBrush"),
            BorderBrush = GetBrush("BorderSubtleBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 4),
            Child = titleBar,
        };

        // Stacked sub-sections. Each returns a Border wrapping the controls
        // for that section so the visual grouping mirrors desktop's
        // "Border + StackPanel" cards.
        var inner = new StackPanel
        {
            Spacing = 18,
            Margin = new Thickness(16, 12, 16, 16),
            Children =
            {
                BuildSettingsRoutingSection(),
                BuildSettingsLeakSection(),
                BuildSettingsUpdatesSection(),
                BuildSettingsAutostartSection(),
            }
        };

        var scroller = new ScrollViewer
        {
            Content = inner,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = GetBrush("SurfaceAppBrush"),
        };

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(titleBarBorder, Dock.Top);
        dock.Children.Add(titleBarBorder);
        dock.Children.Add(scroller);

        return new Border
        {
            Background = GetBrush("SurfaceAppBrush"),
            IsVisible = false,
            Child = dock,
        };
    }

    /// <summary>
    /// Routing sub-section: split/full radio cards + Russian-traffic bypass.
    /// Mirrors desktop NetworkPage.axaml lines 237-309 (Routing block).
    /// </summary>
    private Control BuildSettingsRoutingSection()
    {
        var sectionTitle = MakeSectionTitle(Localization.SettingsSectionRouting);
        var description = new TextBlock
        {
            Text = Localization.RoutingDescription,
            FontSize = 11,
            Foreground = GetBrush("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        };

        var routingMode = AndroidStorage.GetRoutingMode();

        _settingsSplitRadio = new Avalonia.Controls.RadioButton
        {
            GroupName = "SettingsRouting",
            IsChecked = routingMode == "split",
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0),
        };
        _settingsSplitRadio.IsCheckedChanged += OnSettingsRoutingChanged;
        var splitCard = MakeRadioCard(_settingsSplitRadio,
            Localization.SplitTunnelTitle, Localization.SplitTunnelSubtitle);

        _settingsFullRadio = new Avalonia.Controls.RadioButton
        {
            GroupName = "SettingsRouting",
            IsChecked = routingMode == "full",
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0),
        };
        _settingsFullRadio.IsCheckedChanged += OnSettingsRoutingChanged;
        var fullCard = MakeRadioCard(_settingsFullRadio,
            Localization.FullTunnelTitle, Localization.FullTunnelSubtitle);

        _settingsBypassRu = new Avalonia.Controls.CheckBox
        {
            IsChecked = AndroidStorage.GetBypassRussianTraffic(),
            MinHeight = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
        };
        _settingsBypassRu.IsCheckedChanged += OnSettingsBypassRuChanged;
        var bypassCard = MakeCheckboxCard(_settingsBypassRu,
            Localization.BypassRussianTrafficLabel, Localization.BypassRussianTrafficHint);

        var stack = new StackPanel
        {
            Spacing = 10,
            Children = { sectionTitle, description, splitCard, fullCard, bypassCard }
        };
        return WrapSection(stack);
    }

    /// <summary>
    /// Leak protection sub-section: block_on_vpn_fail toggle + DNS strategy
    /// ComboBox. Desktop has 4 checkboxes (StrictMode / ForceIpv4 /
    /// FlushDns / StrictDns); on Android most map to either no-op or to
    /// the VpnService.Builder layer. We surface the ones that map cleanly
    /// to the Android stack and label them honestly.
    /// </summary>
    private Control BuildSettingsLeakSection()
    {
        var sectionTitle = MakeSectionTitle(Localization.SettingsSectionLeak);

        _settingsBlockOnVpnFail = new Avalonia.Controls.CheckBox
        {
            IsChecked = AndroidStorage.GetBlockOnVpnFail(),
            MinHeight = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _settingsBlockOnVpnFail.IsCheckedChanged += OnSettingsBlockOnVpnFailChanged;
        var blockGrid = MakeLabeledCheckboxRow(_settingsBlockOnVpnFail,
            Localization.BlockOnVpnFailLabel, Localization.BlockOnVpnFailHint);

        // DNS strategy ComboBox — three values, mirrors desktop's choices.
        _settingsDnsStrategy = new Avalonia.Controls.ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 12,
            ItemsSource = new[]
            {
                Localization.DnsStrategyIpv4Only,
                Localization.DnsStrategyPreferIpv4,
                Localization.DnsStrategyPreferIpv6,
            },
            SelectedIndex = AndroidStorage.GetDnsStrategy() switch
            {
                "prefer_ipv4" => 1,
                "prefer_ipv6" => 2,
                _ => 0,
            },
        };
        _settingsDnsStrategy.SelectionChanged += OnSettingsDnsStrategyChanged;

        var dnsHeader = new TextBlock
        {
            Text = Localization.DnsStrategyHeader,
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Opacity = 0.8,
            Margin = new Thickness(0, 6, 0, 0),
        };
        var dnsHint = new TextBlock
        {
            Text = Localization.DnsStrategyHint,
            FontSize = 10,
            Foreground = GetBrush("TextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };

        var stack = new StackPanel
        {
            Spacing = 8,
            Children = { sectionTitle, blockGrid, dnsHeader, _settingsDnsStrategy, dnsHint }
        };
        return WrapSection(stack);
    }

    /// <summary>
    /// Updates sub-section: prerelease channel toggle + current version
    /// label + manual check button. Mirrors desktop NetworkPage 1881-1928.
    /// On Android the Check button reuses the same placeholder behaviour
    /// as the kebab > Diagnostics > "Check for updates" entry — Android
    /// auto-update is out of v3.0 alpha scope (handbook §6).
    /// </summary>
    private Control BuildSettingsUpdatesSection()
    {
        var sectionTitle = MakeSectionTitle(Localization.SettingsSectionUpdates);

        var channelHeader = new TextBlock
        {
            Text = Localization.UpdateChannelHeader,
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
        };

        _settingsReceivePrereleases = new Avalonia.Controls.CheckBox
        {
            IsChecked = AndroidStorage.GetUpdateChannel() == "experimental",
            MinHeight = 0,
            Padding = new Thickness(4, 0),
            Content = new TextBlock
            {
                Text = Localization.ReceivePrereleasesLabel,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
            }
        };
        _settingsReceivePrereleases.IsCheckedChanged += OnSettingsChannelChanged;

        // Current version + Check button row.
        _settingsCurrentVersion = new TextBlock
        {
            Text = VPNRouter.Core.AppVersion.Version,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = GetBrush("TextPrimaryBrush"),
        };
        var versionLabel = new TextBlock
        {
            Text = Localization.CurrentVersionLabel,
            FontSize = 10,
            Opacity = 0.7,
        };
        var versionStack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { versionLabel, _settingsCurrentVersion }
        };

        var checkBtn = new Avalonia.Controls.Button
        {
            Content = Localization.CheckForUpdatesButton,
            FontSize = 10,
            Padding = new Thickness(10, 5),
            MinHeight = 0,
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            VerticalAlignment = VerticalAlignment.Center,
        };
        checkBtn.Click += OnSettingsCheckUpdatesClicked;

        var versionRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
            Margin = new Thickness(0, 6, 0, 0),
        };
        Grid.SetColumn(versionStack, 0);
        Grid.SetColumn(checkBtn, 1);
        versionRow.Children.Add(versionStack);
        versionRow.Children.Add(checkBtn);

        var stack = new StackPanel
        {
            Spacing = 6,
            Children = { sectionTitle, channelHeader, _settingsReceivePrereleases, versionRow }
        };
        return WrapSection(stack);
    }

    /// <summary>
    /// Autostart sub-section: 3 toggles (VPN / Zapret / TgProxy) + DBG-3
    /// status badge per the same predicate as desktop's
    /// <c>ComputeAutostartStatus</c>. On Android there's no
    /// BOOT_COMPLETED receiver wired and no Service-mode equivalent of
    /// the Windows VPNRouterService, so the VPN toggle is permanently
    /// in the ⛔ tier ("Will not fire: needs BOOT_COMPLETED + Service").
    /// Zapret + TgProxy stay in the "not ported" tier — those features
    /// are Windows-only on the desktop port today.
    /// Persistence is real so a future BootCompletedReceiver can read
    /// the flags without a migration.
    /// </summary>
    private Control BuildSettingsAutostartSection()
    {
        var sectionTitle = MakeSectionTitle(Localization.SettingsSectionAutostart);
        var bootHeader = new TextBlock
        {
            Text = Localization.AutostartBootSectionTitle,
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Foreground = GetBrush("TextMutedBrush"),
        };
        var bootSub = new TextBlock
        {
            Text = Localization.AutostartBootSectionSub,
            FontSize = 10,
            Foreground = GetBrush("TextMutedBrush"),
            Opacity = 0.85,
            TextWrapping = TextWrapping.Wrap,
        };

        // Per-component checkbox + status badge stacks. Status text
        // mirrors the desktop ComputeAutostartStatus three-tier badge.
        _settingsAutostartVpn = new Avalonia.Controls.CheckBox
        {
            IsChecked = AndroidStorage.GetAutostartVpn(),
            MinHeight = 0,
            Padding = new Thickness(4, 0),
            Content = new TextBlock
            {
                Text = Localization.AutostartLabelVpn,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
            }
        };
        _settingsAutostartVpn.IsCheckedChanged += OnSettingsAutostartVpnChanged;
        var vpnStack = MakeAutostartRow(_settingsAutostartVpn,
            Localization.AutostartStatusNoBoot, "DangerFgBrush");

        _settingsAutostartZapret = new Avalonia.Controls.CheckBox
        {
            IsChecked = AndroidStorage.GetAutostartZapret(),
            MinHeight = 0,
            Padding = new Thickness(4, 0),
            Content = new TextBlock
            {
                Text = Localization.AutostartLabelZapret,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
            }
        };
        _settingsAutostartZapret.IsCheckedChanged += OnSettingsAutostartZapretChanged;
        var zapretStack = MakeAutostartRow(_settingsAutostartZapret,
            Localization.AutostartZapretNotPorted, "DangerFgBrush");

        _settingsAutostartTgProxy = new Avalonia.Controls.CheckBox
        {
            IsChecked = AndroidStorage.GetAutostartTgProxy(),
            MinHeight = 0,
            Padding = new Thickness(4, 0),
            Content = new TextBlock
            {
                Text = Localization.AutostartLabelTgProxy,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
            }
        };
        _settingsAutostartTgProxy.IsCheckedChanged += OnSettingsAutostartTgProxyChanged;
        var tgStack = MakeAutostartRow(_settingsAutostartTgProxy,
            Localization.AutostartTgProxyNotPorted, "DangerFgBrush");

        var stack = new StackPanel
        {
            Spacing = 8,
            Children = { sectionTitle, bootHeader, bootSub, vpnStack, zapretStack, tgStack }
        };
        return WrapSection(stack);
    }

    // ── Settings overlay layout helpers ─────────────────────────────────

    private TextBlock MakeSectionTitle(string text) => new TextBlock
    {
        Text = text,
        FontWeight = FontWeight.Bold,
        FontSize = 13,
        Foreground = GetBrush("TextPrimaryBrush"),
    };

    private Border WrapSection(Control content) => new Border
    {
        Padding = new Thickness(12),
        CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
        Background = GetBrush("SurfaceBaseBrush"),
        BorderBrush = GetBrush("BorderSubtleBrush"),
        BorderThickness = new Thickness(1),
        Child = content,
    };

    /// <summary>
    /// "Radio-card" pattern from desktop NetworkPage — Border with a 24,*
    /// Grid (radio left, title+subtitle stack right). Whole card click
    /// flips the radio.
    /// </summary>
    private Border MakeRadioCard(Avalonia.Controls.RadioButton radio, string title, string subtitle)
    {
        var titleText = new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Foreground = GetBrush("TextPrimaryBrush"),
        };
        var subText = new TextBlock
        {
            Text = subtitle,
            FontSize = 10,
            Foreground = GetBrush("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        };
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("24,*"),
            ColumnSpacing = 8,
        };
        Grid.SetColumn(radio, 0);
        var rightStack = new StackPanel { Spacing = 2, Children = { titleText, subText } };
        Grid.SetColumn(rightStack, 1);
        grid.Children.Add(radio);
        grid.Children.Add(rightStack);

        var card = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
        card.PointerPressed += (_, __) =>
        {
            // Tap anywhere on the card flips the radio (desktop card click
            // semantics). Idempotent: clicking an already-active card
            // is a no-op since IsChecked → true is no change.
            radio.IsChecked = true;
        };
        return card;
    }

    /// <summary>"Checkbox-card" — same shape as MakeRadioCard but for a CheckBox.</summary>
    private Border MakeCheckboxCard(Avalonia.Controls.CheckBox cb, string title, string subtitle)
    {
        var titleText = new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Foreground = GetBrush("TextPrimaryBrush"),
        };
        var subText = new TextBlock
        {
            Text = subtitle,
            FontSize = 10,
            Foreground = GetBrush("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        };
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("24,*"),
            ColumnSpacing = 8,
        };
        Grid.SetColumn(cb, 0);
        var rightStack = new StackPanel { Spacing = 2, Children = { titleText, subText } };
        Grid.SetColumn(rightStack, 1);
        grid.Children.Add(cb);
        grid.Children.Add(rightStack);

        var card = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
        card.PointerPressed += (_, __) =>
        {
            cb.IsChecked = !(cb.IsChecked == true);
        };
        return card;
    }

    /// <summary>
    /// 24,* grid with checkbox + bold label + wrap-text hint underneath.
    /// Used in Leak section where labels are short and don't deserve a
    /// full radio-card look.
    /// </summary>
    private StackPanel MakeLabeledCheckboxRow(Avalonia.Controls.CheckBox cb, string label, string hint)
    {
        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        var hintText = new TextBlock
        {
            Text = hint,
            FontSize = 10,
            Foreground = GetBrush("TextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(28, 0, 0, 0),
        };
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("24,*"),
            ColumnSpacing = 6,
        };
        Grid.SetColumn(cb, 0);
        Grid.SetColumn(labelText, 1);
        grid.Children.Add(cb);
        grid.Children.Add(labelText);

        return new StackPanel
        {
            Spacing = 2,
            Children = { grid, hintText }
        };
    }

    /// <summary>
    /// Autostart row: checkbox on top, status badge below indented to align
    /// under the label text. Mirrors desktop NetworkPage 2071-2150 — the
    /// status TextBlock is colored per its tier (Success / Warning / Danger).
    /// </summary>
    private StackPanel MakeAutostartRow(Avalonia.Controls.CheckBox cb, string statusText, string statusBrushKey)
    {
        var status = new TextBlock
        {
            Text = statusText,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 9,
            Margin = new Thickness(22, 0, 0, 0),
            Foreground = GetBrush(statusBrushKey),
        };
        return new StackPanel
        {
            Spacing = 2,
            Children = { cb, status }
        };
    }

    // ── Settings overlay event handlers ─────────────────────────────────

    private void OnMenuSettingsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        ShowSettings();
    }

    private void ShowSettings()
    {
        if (_settingsOverlay is null) return;
        // Re-seed control state so the overlay reflects the current
        // persisted values (in case another path updated them — e.g. the
        // form's Selected-apps radio writes per_app_mode independently).
        _settingsLoading = true;
        try
        {
            var routing = AndroidStorage.GetRoutingMode();
            if (_settingsSplitRadio is not null) _settingsSplitRadio.IsChecked = routing == "split";
            if (_settingsFullRadio is not null) _settingsFullRadio.IsChecked = routing == "full";
            if (_settingsBypassRu is not null) _settingsBypassRu.IsChecked = AndroidStorage.GetBypassRussianTraffic();
            if (_settingsBlockOnVpnFail is not null) _settingsBlockOnVpnFail.IsChecked = AndroidStorage.GetBlockOnVpnFail();
            if (_settingsDnsStrategy is not null)
            {
                _settingsDnsStrategy.SelectedIndex = AndroidStorage.GetDnsStrategy() switch
                {
                    "prefer_ipv4" => 1,
                    "prefer_ipv6" => 2,
                    _ => 0,
                };
            }
            if (_settingsReceivePrereleases is not null)
                _settingsReceivePrereleases.IsChecked = AndroidStorage.GetUpdateChannel() == "experimental";
            if (_settingsCurrentVersion is not null) _settingsCurrentVersion.Text = VPNRouter.Core.AppVersion.Version;
            if (_settingsAutostartVpn is not null) _settingsAutostartVpn.IsChecked = AndroidStorage.GetAutostartVpn();
            if (_settingsAutostartZapret is not null) _settingsAutostartZapret.IsChecked = AndroidStorage.GetAutostartZapret();
            if (_settingsAutostartTgProxy is not null) _settingsAutostartTgProxy.IsChecked = AndroidStorage.GetAutostartTgProxy();
        }
        finally
        {
            _settingsLoading = false;
        }
        _settingsOverlay.IsVisible = true;
    }

    private void OnSettingsCloseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsOverlay is not null) _settingsOverlay.IsVisible = false;
    }

    private void OnSettingsRoutingChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsLoading) return;
        // RadioButton group fires IsCheckedChanged on both the now-off and
        // now-on members; we react only to the new "on" state to avoid
        // double-write. Falls back to "split" if neither radio is checked
        // (initial transient state during construction).
        var splitOn = _settingsSplitRadio?.IsChecked == true;
        var fullOn = _settingsFullRadio?.IsChecked == true;
        if (!splitOn && !fullOn) return;
        var newMode = splitOn ? "split" : "full";
        if (AndroidStorage.GetRoutingMode() != newMode)
            AndroidStorage.SetRoutingMode(newMode);
    }

    private void OnSettingsBypassRuChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsLoading || _settingsBypassRu is null) return;
        AndroidStorage.SetBypassRussianTraffic(_settingsBypassRu.IsChecked == true);
    }

    private void OnSettingsBlockOnVpnFailChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsLoading || _settingsBlockOnVpnFail is null) return;
        AndroidStorage.SetBlockOnVpnFail(_settingsBlockOnVpnFail.IsChecked == true);
    }

    private void OnSettingsDnsStrategyChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
    {
        if (_settingsLoading || _settingsDnsStrategy is null) return;
        var value = _settingsDnsStrategy.SelectedIndex switch
        {
            1 => "prefer_ipv4",
            2 => "prefer_ipv6",
            _ => "ipv4_only",
        };
        AndroidStorage.SetDnsStrategy(value);
    }

    private void OnSettingsChannelChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsLoading || _settingsReceivePrereleases is null) return;
        AndroidStorage.SetUpdateChannel(_settingsReceivePrereleases.IsChecked == true ? "experimental" : "stable");
    }

    private void OnSettingsCheckUpdatesClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Same placeholder as the kebab > Diagnostics > "Check for updates"
        // entry. Auto-update on Android needs PackageInstaller +
        // REQUEST_INSTALL_PACKAGES — out of v3.0 alpha scope.
        ShowMenuFeedback(Localization.MenuItemUpdateComingSoon);
    }

    private void OnSettingsAutostartVpnChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsLoading || _settingsAutostartVpn is null) return;
        AndroidStorage.SetAutostartVpn(_settingsAutostartVpn.IsChecked == true);
    }

    private void OnSettingsAutostartZapretChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsLoading || _settingsAutostartZapret is null) return;
        AndroidStorage.SetAutostartZapret(_settingsAutostartZapret.IsChecked == true);
    }

    private void OnSettingsAutostartTgProxyChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsLoading || _settingsAutostartTgProxy is null) return;
        AndroidStorage.SetAutostartTgProxy(_settingsAutostartTgProxy.IsChecked == true);
    }

    // ── Mascot loading + theme-aware inversion ──────────────────────────

    private static Bitmap? _mascotLight;
    private static Bitmap? _mascotDark;

    /// <summary>
    /// v3.0 Phase 5 — load + cache mascot bitmap, RGB-inverted on dark
    /// theme. Lifted from desktop's MainWindowViewModel.TryBuildInvertedLogo:
    /// Bgra8888/Unpremul preserves alpha so edges stay anti-aliased
    /// after the channel flip.
    /// </summary>
    private Bitmap LoadMascot()
    {
        if (_mascotLight is null)
        {
            try
            {
                var stream = AssetLoader.Open(new Uri("avares://VPNRouter.Android/Assets/penguin_mascot.png"));
                _mascotLight = new Bitmap(stream);
            }
            catch
            {
                // Fallback transparent 1x1 — won't be visible but keeps
                // the layout from crashing.
                var wb = new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96),
                    PixelFormat.Bgra8888, AlphaFormat.Unpremul);
                _mascotLight = wb;
            }
        }
        if (ActualThemeVariant == ThemeVariant.Dark)
        {
            _mascotDark ??= TryBuildInverted(_mascotLight) ?? _mascotLight;
            return _mascotDark;
        }
        return _mascotLight;
    }

    private static Bitmap? TryBuildInverted(Bitmap source)
    {
        try
        {
            var size = source.PixelSize;
            var wb = new WriteableBitmap(size, source.Dpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using var fb = wb.Lock();
            int byteCount = fb.RowBytes * size.Height;
            source.CopyPixels(new PixelRect(size), fb.Address, byteCount, fb.RowBytes);
            var bytes = new byte[byteCount];
            System.Runtime.InteropServices.Marshal.Copy(fb.Address, bytes, 0, byteCount);
            // BGRA pixels — invert B, G, R; leave A alone
            for (int i = 0; i + 3 < bytes.Length; i += 4)
            {
                bytes[i + 0] = (byte)(255 - bytes[i + 0]); // B
                bytes[i + 1] = (byte)(255 - bytes[i + 1]); // G
                bytes[i + 2] = (byte)(255 - bytes[i + 2]); // R
            }
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, fb.Address, byteCount);
            return wb;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Phase 4 — pill-style status chip (rounded background + colored
    /// label) for the sub-header VPN/Zapret/TG indicators. Mirrors
    /// desktop's chip pattern from MainWindow.axaml header.
    ///
    /// <para>v3.0 Phase 8.2 — takes brush KEYS (not brushes) so the
    /// foreground + background ride <see cref="DynamicResourceExtension"/>
    /// and auto-repaint on theme variant change.</para>
    /// </summary>
    private TextBlock MakeChip(string label, string bgKey, string fgKey)
    {
        // Wrapped Border preferred for rounded corners, but Avalonia
        // TextBlock + StackPanel layout is simpler for now. Return a
        // TextBlock styled as a tag — uses parent StackPanel's width.
        // Note: chips render as boxes, not pills, on this font size;
        // looks similar enough on phone screen at 9pt.
        var tb = new TextBlock
        {
            Text = label,
            FontSize = 9,
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(7, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        tb.BindToken(TextBlock.ForegroundProperty, fgKey);
        tb.BindToken(TextBlock.BackgroundProperty, bgKey);
        return tb;
    }

    private Avalonia.Controls.Button StyledSecondaryButton(string label)
    {
        var btn = new Avalonia.Controls.Button
        {
            Content = label,
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Padding = new Thickness(14, 7),
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            BorderThickness = new Thickness(1),
        };
        btn.BindToken(Avalonia.Controls.Button.BackgroundProperty, "SurfaceRaisedBrush");
        btn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "TextPrimaryBrush");
        btn.BindToken(Avalonia.Controls.Button.BorderBrushProperty, "BorderDefaultBrush");
        return btn;
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
        if (MainActivity.IntendedConnected)
        {
            activity.RequestDisconnect();
        }
        else
        {
            // v3.0 Phase 7.1 — flip VPN chip to Connecting immediately so
            // the user gets feedback while the system VPN consent dialog
            // is on screen (most visible during first-launch consent
            // flow). IntentChanged(true) will follow and transition Off →
            // skipped → On in the normal happy path; on consent decline
            // or TUNNEL_ERROR it bounces back to Off.
            SetVpnChipState(ChipState.Connecting);
            activity.RequestConnect();
        }
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
            // v3.0 Phase 8.2 — Fill goes through DynamicResource so a
            // theme switch while connected re-resolves SuccessSolidBrush
            // to the new variant's value automatically.
            _statusDot.BindToken(Avalonia.Controls.Shapes.Shape.FillProperty, "SuccessSolidBrush");
            if (_statusTitle is not null) _statusTitle.Text = Localization.SimpleStatusTitleOn;
            if (_statusDesc is not null) _statusDesc.Text = Localization.SimpleStatusDescOn;
            if (_ctaConnect is not null) _ctaConnect.IsVisible = false;
            if (_ctaConnecting is not null) _ctaConnecting.IsVisible = false;
            if (_ctaDisconnect is not null) _ctaDisconnect.IsVisible = true;
            SetVpnChipState(ChipState.On);
        }
        else
        {
            _statusDot.BindToken(Avalonia.Controls.Shapes.Shape.FillProperty, "TextMutedBrush");
            if (_statusTitle is not null) _statusTitle.Text = Localization.SimpleStatusTitleOff;
            if (_statusDesc is not null) _statusDesc.Text = Localization.SimpleStatusDescOff;
            if (_ctaConnect is not null) _ctaConnect.IsVisible = true;
            if (_ctaConnecting is not null) _ctaConnecting.IsVisible = false;
            if (_ctaDisconnect is not null) _ctaDisconnect.IsVisible = false;
            SetVpnChipState(ChipState.Off);
        }
        UpdateConfigSummary();
    }

    /// <summary>
    /// v3.0 Phase 7.1 (2026-05-04) — flip VPN chip background + foreground
    /// (and start/stop the Connecting pulse animation) to reflect the
    /// current tunnel lifecycle phase. Idempotent: calling with the same
    /// state is a no-op.
    ///
    /// <para>v3.0 Phase 8.2 (2026-05-07) — chip brushes go through
    /// <see cref="StyledElementResourceExtensions.BindToken"/> so they
    /// auto-repaint on theme variant change. The <paramref name="force"/>
    /// flag lets <see cref="ApplyTheme(string)"/> re-issue the bindings
    /// even when state hasn't changed (a theme flip needs to retain the
    /// active state but re-pick the new variant's color).</para>
    /// </summary>
    private void SetVpnChipState(ChipState state, bool force = false)
    {
        if (_vpnChip is null) return;
        if (_vpnChipState == state && !force) return;
        _vpnChipState = state;

        // Stop any in-flight pulse first — Connecting → On, Connecting → Off,
        // Off → On all need to clear the animation that was driving Opacity.
        // On a forced re-bind we still want to restart the pulse if state
        // is Connecting so the breathing animation stays in sync.
        _vpnPulseCts?.Cancel();
        _vpnPulseCts = null;
        _vpnChip.Opacity = 1.0;

        string bgKey, fgKey;
        switch (state)
        {
            case ChipState.On:
                bgKey = "SuccessBgBrush";
                fgKey = "SuccessFgBrush";
                break;
            case ChipState.Connecting:
                bgKey = "WarningBgBrush";
                fgKey = "WarningFgBrush";
                StartChipPulse(_vpnChip);
                break;
            default: // Off
                bgKey = "SurfaceSunkenBrush";
                fgKey = "TextMutedBrush";
                break;
        }
        _vpnChip.BindToken(TextBlock.BackgroundProperty, bgKey);
        _vpnChip.BindToken(TextBlock.ForegroundProperty, fgKey);
    }

    /// <summary>
    /// v3.0 Phase 7.1 — drive a soft "breathing" Opacity animation
    /// (1.0 ↔ 0.55 over 1.2 s, cycling indefinitely). Cancelled via
    /// <c>_vpnPulseCts</c>. Avalonia's Animation API handles the easing
    /// curve — we just kick off the loop.
    /// </summary>
    private void StartChipPulse(Visual target)
    {
        var cts = new System.Threading.CancellationTokenSource();
        _vpnPulseCts = cts;
        var anim = new Avalonia.Animation.Animation
        {
            Duration = System.TimeSpan.FromMilliseconds(1200),
            IterationCount = Avalonia.Animation.IterationCount.Infinite,
            PlaybackDirection = Avalonia.Animation.PlaybackDirection.Alternate,
            Easing = new Avalonia.Animation.Easings.QuadraticEaseInOut(),
            Children =
            {
                new Avalonia.Animation.KeyFrame
                {
                    Cue = new Avalonia.Animation.Cue(0d),
                    Setters = { new Avalonia.Styling.Setter(Visual.OpacityProperty, 1.0) },
                },
                new Avalonia.Animation.KeyFrame
                {
                    Cue = new Avalonia.Animation.Cue(1d),
                    Setters = { new Avalonia.Styling.Setter(Visual.OpacityProperty, 0.55) },
                },
            },
        };
        // Fire-and-forget — the animation drives the visual and gets
        // cancelled when cts.Cancel() is called from SetVpnChipState.
        _ = anim.RunAsync(target, cts.Token);
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

        // v3.0 Phase 6.4 (2026-05-04) — accept all supported share-link
        // schemes (vless, hysteria2, hy2, tuic, ss), not just vless. The
        // parser does the actual scheme-dispatch; we only need a coarse
        // is-this-a-share-link gate before deciding URI vs subscription.
        if (ServerUriParser.IsSupportedScheme(raw))
        {
            try
            {
                var parsed = ServerUriParser.Parse(raw);
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

    private async void ReloadServerList()
    {
        // v3.0 Phase 7.6 (2026-05-04) — disk + JSON deserialize off the
        // UI thread. SharedPreferences GetString is fast (cached), but
        // JsonConvert.DeserializeObject<List<VlessServerEntry>> on a
        // 100-entry subscription cache can stall the UI for 100-200 ms
        // on slower phones, contributing to the "app lags" complaint.
        // Move to Task.Run; UI updates on the captured context.
        try
        {
            _cachedServers = await System.Threading.Tasks.Task.Run(AndroidStorage.GetServers);
        }
        catch
        {
            _cachedServers = new List<VlessServerEntry>();
        }
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
                };
                name.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                var sub = new TextBlock
                {
                    Text = $"{item?.Server}:{item?.Port}  ·  {item?.Protocol ?? "vless"}",
                    FontSize = 10,
                };
                sub.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
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
        // v2.32.0 (2026-05-07) — Phase 3 placeholder replaced with
        // Subscribe overlay open. The advanced card subtitle promises
        // "Серверы · Подписки · Маршрутизация · Логи"; subscriptions are
        // now real. (Servers + Routing + Logs остаются placeholder'ом
        // под inline form / kebab menu / log overlay соответственно;
        // subscription management was the only orphan here.)
        OpenSubsOverlay();
    }

    private void OnScanQrClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_serverInputError is null) return;
        _serverInputError.Text = Localization.QrComingSoon;
        _serverInputError.IsVisible = true;
    }

    // ── Header kebab menu ──────────────────────────────────────────────

    /// <summary>
    /// v3.0 Phase 7.2 — generic factory for a kebab-menu row. Stretches
    /// horizontally, left-aligns content, transparent background. The
    /// click handler is optional (e.g. version row is non-interactive).
    /// </summary>
    private Avalonia.Controls.Button MakeMenuItem(
        string label,
        string foregroundKey,
        EventHandler<Avalonia.Interactivity.RoutedEventArgs>? onClick)
    {
        var btn = new Avalonia.Controls.Button
        {
            Content = label,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(14, 8),
            FontSize = 12,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            IsHitTestVisible = onClick is not null,
        };
        btn.BindToken(Avalonia.Controls.Button.ForegroundProperty, foregroundKey);
        if (onClick is not null) btn.Click += onClick;
        return btn;
    }

    /// <summary>
    /// v3.0 Phase 7.3 (2026-05-04) — segment button factory. Mirrors
    /// desktop's <c>Classes="segment" Classes.active="..."</c> CSS:
    /// active segment uses the accent surface + accent foreground;
    /// inactive uses the base surface + secondary foreground.
    /// </summary>
    private Avalonia.Controls.Button MakeSegmentButton(
        string label,
        bool active,
        EventHandler<Avalonia.Interactivity.RoutedEventArgs> onClick)
    {
        var btn = new Avalonia.Controls.Button
        {
            Content = label,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 6),
            FontSize = 12,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
        };
        // v3.0 Phase 8.2 — initial bindings; StyleSegmentButton replaces
        // them on selection change so the active+inactive split moves
        // (token keys differ between the two states).
        StyleSegmentButton(btn, active);
        btn.Click += onClick;
        return btn;
    }

    /// <summary>
    /// v3.0 Phase 7.3 — wrap two segment buttons in a 2-column grid with
    /// equal width and small gap, mirroring desktop's
    /// <c>Grid ColumnDefinitions="*,*" ColumnSpacing="2"</c>.
    /// </summary>
    private Grid MakeSegmentRow(Avalonia.Controls.Button left, Avalonia.Controls.Button right)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 4,
            Margin = new Thickness(14, 4, 14, 4),
        };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>
    /// v3.0 Phase 7.3 — overload of <see cref="AppendMenuSection"/> that
    /// accepts arbitrary <see cref="Control"/> items (not just Buttons),
    /// so segment-control rows fit the same flow.
    /// </summary>
    private void AppendMenuSectionWithControls(
        StackPanel stack,
        string headerText,
        Control[] items)
    {
        var header = new TextBlock
        {
            Text = headerText,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(14, 8, 14, 4),
        };
        header.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
        if (headerText == Localization.MenuSectionView) _menuSectionView = header;
        else if (headerText == Localization.MenuSectionDiagnostics) _menuSectionDiagnostics = header;
        else if (headerText == Localization.MenuSectionTroubleshooting) _menuSectionTroubleshooting = header;
        else if (headerText == Localization.MenuSectionAbout) _menuSectionAbout = header;
        else if (headerText == Localization.MenuSectionFreeConfigs) _menuSectionFreeConfigs = header;

        stack.Children.Add(header);

        var divider = new Border
        {
            Height = 1,
            Margin = new Thickness(14, 0, 14, 4),
        };
        divider.BindToken(Border.BackgroundProperty, "BorderSubtleBrush");
        stack.Children.Add(divider);

        foreach (var item in items)
        {
            stack.Children.Add(item);
        }
    }

    /// <summary>
    /// v3.0 Phase 7.2 — append a section to the kebab menu stack:
    /// header TextBlock + thin divider + the supplied items + bottom
    /// spacer. Section header TextBlocks are stored on the field
    /// (_menuSectionView etc.) so language toggle can refresh them.
    /// </summary>
    private void AppendMenuSection(
        StackPanel stack,
        string headerText,
        Avalonia.Controls.Button[] items)
    {
        var header = new TextBlock
        {
            Text = headerText,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(14, 8, 14, 4),
        };
        header.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
        // Cache by header text so ToggleLanguageAndRefresh can find it.
        if (headerText == Localization.MenuSectionView) _menuSectionView = header;
        else if (headerText == Localization.MenuSectionDiagnostics) _menuSectionDiagnostics = header;
        else if (headerText == Localization.MenuSectionTroubleshooting) _menuSectionTroubleshooting = header;
        else if (headerText == Localization.MenuSectionAbout) _menuSectionAbout = header;
        else if (headerText == Localization.MenuSectionFreeConfigs) _menuSectionFreeConfigs = header;

        stack.Children.Add(header);

        var divider = new Border
        {
            Height = 1,
            Margin = new Thickness(14, 0, 14, 4),
        };
        divider.BindToken(Border.BackgroundProperty, "BorderSubtleBrush");
        stack.Children.Add(divider);

        foreach (var item in items)
        {
            stack.Children.Add(item);
        }
    }

    private void OnKebabMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is null) return;
        _kebabPopup.IsOpen = !_kebabPopup.IsOpen;
        // Reset the Reset-confirm flow when the menu is reopened so a
        // stale "All settings will be cleared. Continue?" prompt doesn't
        // accidentally trigger on next tap.
        if (_kebabPopup.IsOpen)
        {
            _resetConfirmPending = false;
            if (_menuResetSettingsItem is not null)
                _menuResetSettingsItem.Content = Localization.MenuItemResetSettings;
        }
    }

    // v3.0 Phase 7.3 — segmented control click handlers. Each one SETS
    // a specific value (no-op if already active) instead of toggling.
    // Matches desktop's SetThemeLight / SetThemeDark / SetLanguageRussian
    // / SetLanguageEnglish commands. Popup stays open so the user can
    // see the segment switch visually.

    private void OnMenuLangRuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Localization.Ru) return; // already active — no-op
        ApplyLanguage(true);
    }

    private void OnMenuLangEnClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!Localization.Ru) return;
        ApplyLanguage(false);
    }

    private void OnMenuThemeLightClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ApplyTheme("light");
    }

    private void OnMenuThemeDarkClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ApplyTheme("dark");
    }

    /// <summary>
    /// v3.0 Phase 7.3 — set RU or EN explicitly + refresh all the
    /// labels through ToggleLanguageAndRefresh + repaint segment
    /// active state. Idempotent.
    /// </summary>
    private void ApplyLanguage(bool ru)
    {
        if (Localization.Ru == ru) return;
        Localization.ToggleAndPersist();
        ToggleLanguageAndRefresh();
        RepaintLanguageSegment();
    }

    private void ApplyTheme(string mode)
    {
        var current = AndroidStorage.GetTheme();
        if (current == mode) return;
        AndroidStorage.SetTheme(mode);
        RequestedThemeVariant = mode == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;

        // v3.0 Phase 8.2 (2026-05-07) — every property bound via
        // BindToken auto-resolves to the new theme's value through
        // Avalonia's DynamicResource pipeline. The two surfaces that
        // can't ride DynamicResource still need manual refresh:
        //   1) Mascot Bitmap — Bgra8888 byte buffer, must re-load to
        //      get the inverted dark variant (mirrors desktop's
        //      MainWindowViewModel.LogoSource pattern).
        //   2) Active-segment chrome — StyleSegmentButton/SetVpnChipState
        //      pick a different brush KEY for active vs inactive, so
        //      they need to re-bind to the right key (the theme
        //      variant change alone wouldn't move the active segment).
        if (_mascotImage is not null)
        {
            _mascotImage.Source = LoadMascot();
        }
        RepaintThemeSegment();
        RepaintLanguageSegment();
        SetVpnChipState(_vpnChipState, force: true);
        UpdateConnectionState(MainActivity.IntendedConnected);
    }

    /// <summary>
    /// v3.0 Phase 7.3 — refresh segment colors after a theme change so
    /// the active segment moves to the new selection.
    /// </summary>
    private void RepaintThemeSegment()
    {
        var isDark = AndroidStorage.GetTheme() == "dark";
        StyleSegmentButton(_menuThemeLight, !isDark);
        StyleSegmentButton(_menuThemeDark, isDark);
    }

    private void RepaintLanguageSegment()
    {
        StyleSegmentButton(_menuLangRu, Localization.Ru);
        StyleSegmentButton(_menuLangEn, !Localization.Ru);
    }

    private void StyleSegmentButton(Avalonia.Controls.Button? btn, bool active)
    {
        if (btn is null) return;
        btn.FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal;
        // v3.0 Phase 8.2 — re-bind via DynamicResource so the button
        // tracks ThemeVariant changes between calls. New bindings
        // replace any prior binding at LocalValue priority on the same
        // property.
        btn.BindToken(Avalonia.Controls.Button.BackgroundProperty,
            active ? "AccentBgSubtleBrush" : "SurfaceSunkenBrush");
        btn.BindToken(Avalonia.Controls.Button.ForegroundProperty,
            active ? "AccentFgBrush" : "TextSecondaryBrush");
        btn.BindToken(Avalonia.Controls.Button.BorderBrushProperty,
            active ? "BorderAccentBrush" : "BorderSubtleBrush");
    }

    /// <summary>
    /// v3.0 Phase 7.4 (2026-05-04) — Diagnostics > Open log. Reads the
    /// last 50 KB of <c>getExternalFilesDir()/singbox.log</c> into the
    /// in-app overlay viewer. Pre-7.4 this only copied the path to the
    /// clipboard, which closed handbook §5.6 only formally — users on
    /// device couldn't actually read the log without `adb`.
    /// </summary>
    private void OnMenuOpenLogClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        ShowLogViewer();
    }

    private void ShowLogViewer()
    {
        if (_logOverlay is null) return;
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

    // ── Phase 7.5 — Per-app filter UI (handbook §5.5) ───────────────────

    private void OnTunnelModeRadioChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // IsChecked changes fire on BOTH the previously-selected and the
        // newly-selected radio when group toggles, so dedupe by checking
        // the actual state.
        var splitOn = _splitRadio?.IsChecked == true;

        // v3.0 v2.32.0 — when the user toggles split ON, restore the last
        // active per-app mode ("include" or "exclude"); first-time users
        // get "include" via the GetPerAppLastMode default. Toggling split
        // OFF writes "off" to the active mode but preserves last-mode so
        // the next ON toggle is sticky.
        if (splitOn)
        {
            var current = AndroidStorage.GetPerAppMode();
            if (current == "off")
            {
                var restored = AndroidStorage.GetPerAppLastMode();
                AndroidStorage.SetPerAppMode(restored);
            }
        }
        else
        {
            if (AndroidStorage.GetPerAppMode() != "off")
            {
                AndroidStorage.SetPerAppMode("off");
            }
        }

        // Show/hide the "Choose apps…" sub-stack we tagged on the split
        // radio in BuildSimplePageView.
        if (_splitRadio?.Tag is StackPanel perAppStack)
        {
            perAppStack.IsVisible = splitOn;
        }

        UpdatePerAppFormCountLabel();
    }

    /// <summary>
    /// v3.0 v2.32.0 (2026-05-07) — keeps the form-side "Selected: N" label
    /// in sync with the saved package count + the active mode. The label
    /// suffix differs by mode so a user glancing at the form can tell
    /// whether "Selected: 3" means "3 apps go via VPN" (include) or
    /// "3 apps bypass VPN" (exclude). Called from
    /// <see cref="OnTunnelModeRadioChanged"/> + <see cref="OnAppPickerSaveClicked"/>.
    /// </summary>
    private void UpdatePerAppFormCountLabel()
    {
        if (_perAppCountLabel is null) return;
        var count = AndroidStorage.GetPerAppPackages().Count;
        var mode = AndroidStorage.GetPerAppMode();
        var fmt = mode == "exclude"
            ? Localization.PerAppCountExclude
            : Localization.PerAppCountInclude;
        _perAppCountLabel.Text = string.Format(fmt, count);
    }

    private void OnPerAppPickButtonClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ShowAppPicker();
    }

    private async void ShowAppPicker()
    {
        if (_appPickerOverlay is null) return;

        // Seed the selection set from storage so check states match what
        // the user previously saved.
        _appPickerSelected = new HashSet<string>(AndroidStorage.GetPerAppPackages(),
                                                 System.StringComparer.OrdinalIgnoreCase);

        // v3.0 v2.32.0 — seed the picker mode. If storage is currently
        // "off" (user opened the picker after toggling split on but before
        // mode persisted), restore the last active mode; default to
        // "include" via GetPerAppLastMode for first-run.
        var storedMode = AndroidStorage.GetPerAppMode();
        _appPickerMode = storedMode switch
        {
            "include" => "include",
            "exclude" => "exclude",
            _ => AndroidStorage.GetPerAppLastMode(),
        };
        ApplyPickerModeVisuals();

        if (_appPickerSearch is not null) _appPickerSearch.Text = string.Empty;
        if (_appPickerSystemToggle is not null)
            _appPickerSystemToggle.IsChecked = _appPickerSystemAppsVisible;

        // Show the overlay first with a "Loading…" placeholder, then
        // load apps off the UI thread (PackageManager.GetInstalledApplications
        // can take 100-500 ms on slower devices).
        UpdateAppPickerCount();
        if (_appPickerList is not null)
        {
            _appPickerList.ItemsSource = new[] { Localization.PerAppLoading };
        }
        _appPickerOverlay.IsVisible = true;

        try
        {
            _appPickerCache = await System.Threading.Tasks.Task.Run(() =>
                _appPickerSystemAppsVisible
                    ? AppListLoader.ListAllApps()
                    : AppListLoader.ListUserApps());
        }
        catch
        {
            _appPickerCache = new List<AppListLoader.AppEntry>();
        }
        ApplyAppPickerFilter();
    }

    private void OnAppPickerCloseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_appPickerOverlay is not null) _appPickerOverlay.IsVisible = false;
    }

    private void OnAppPickerSaveClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        AndroidStorage.SetPerAppPackages(_appPickerSelected);
        // v3.0 v2.32.0 — persist mode + sticky-restore key in one step so
        // the next split-radio toggle restores the same mode.
        AndroidStorage.SetPerAppMode(_appPickerMode);
        AndroidStorage.SetPerAppLastMode(_appPickerMode);
        UpdatePerAppFormCountLabel();
        if (_appPickerOverlay is not null) _appPickerOverlay.IsVisible = false;
    }

    private void OnAppPickerModeIncludeClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_appPickerMode == "include") return;
        _appPickerMode = "include";
        ApplyPickerModeVisuals();
    }

    private void OnAppPickerModeExcludeClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_appPickerMode == "exclude") return;
        _appPickerMode = "exclude";
        ApplyPickerModeVisuals();
    }

    /// <summary>
    /// v3.0 v2.32.0 (2026-05-07) — repaints the include/exclude segment
    /// buttons + the hint TextBlock based on <see cref="_appPickerMode"/>.
    /// Mirrors how the kebab menu's theme/language segment row paints
    /// active/inactive (see <see cref="MakeSegmentButton"/>).
    /// </summary>
    private void ApplyPickerModeVisuals()
    {
        var includeActive = _appPickerMode == "include";
        var excludeActive = _appPickerMode == "exclude";
        StyleSegment(_appPickerModeIncludeBtn, includeActive);
        StyleSegment(_appPickerModeExcludeBtn, excludeActive);
        if (_appPickerModeHint is not null)
        {
            _appPickerModeHint.Text = excludeActive
                ? Localization.PerAppHintExclude
                : Localization.PerAppHintInclude;
        }
    }

    private void StyleSegment(Avalonia.Controls.Button? btn, bool active)
    {
        if (btn is null) return;
        btn.Background = active ? GetBrush("AccentBgSubtleBrush") : GetBrush("SurfaceSunkenBrush");
        btn.Foreground = active ? GetBrush("AccentFgBrush") : GetBrush("TextSecondaryBrush");
        btn.BorderBrush = active ? GetBrush("BorderAccentBrush") : GetBrush("BorderSubtleBrush");
        btn.FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal;
    }

    private void OnAppPickerSystemToggleChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var newValue = _appPickerSystemToggle?.IsChecked == true;
        if (newValue == _appPickerSystemAppsVisible) return;
        _appPickerSystemAppsVisible = newValue;
        // Reload list with the new include-system flag. This might take a
        // beat on slow devices; reuse the show flow for the loading state.
        _ = ReloadAppPickerCacheAsync();
    }

    private async System.Threading.Tasks.Task ReloadAppPickerCacheAsync()
    {
        if (_appPickerList is null) return;
        _appPickerList.ItemsSource = new[] { Localization.PerAppLoading };
        try
        {
            _appPickerCache = await System.Threading.Tasks.Task.Run(() =>
                _appPickerSystemAppsVisible
                    ? AppListLoader.ListAllApps()
                    : AppListLoader.ListUserApps());
        }
        catch
        {
            _appPickerCache = new List<AppListLoader.AppEntry>();
        }
        ApplyAppPickerFilter();
    }

    private void OnAppPickerSearchChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
    {
        ApplyAppPickerFilter();
    }

    /// <summary>
    /// Apply the current search term to <see cref="_appPickerCache"/> and
    /// refresh the ListBox with a row factory that builds CheckBox + label
    /// per visible app. Each row's CheckedChanged updates
    /// <see cref="_appPickerSelected"/> immediately so Save just persists
    /// the in-memory set.
    /// </summary>
    private void ApplyAppPickerFilter()
    {
        if (_appPickerList is null) return;
        var search = _appPickerSearch?.Text?.Trim() ?? string.Empty;
        var filtered = string.IsNullOrEmpty(search)
            ? _appPickerCache
            : _appPickerCache.Where(a =>
                a.Label.Contains(search, System.StringComparison.OrdinalIgnoreCase)
                || a.PackageName.Contains(search, System.StringComparison.OrdinalIgnoreCase)).ToList();

        var rows = filtered.Select(BuildAppRow).ToList();
        _appPickerList.ItemsSource = rows;
        UpdateAppPickerCount();
    }

    private Control BuildAppRow(AppListLoader.AppEntry app)
    {
        var label = new TextBlock
        {
            Text = app.Label,
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var pkgLine = new TextBlock
        {
            Text = app.PackageName,
            FontSize = 9,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        pkgLine.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
        var rowText = new StackPanel
        {
            Spacing = 1,
            Children = { label, pkgLine },
            VerticalAlignment = VerticalAlignment.Center,
        };

        var checkbox = new Avalonia.Controls.CheckBox
        {
            IsChecked = _appPickerSelected.Contains(app.PackageName),
            VerticalAlignment = VerticalAlignment.Center,
        };
        checkbox.IsCheckedChanged += (_, __) =>
        {
            if (checkbox.IsChecked == true)
                _appPickerSelected.Add(app.PackageName);
            else
                _appPickerSelected.Remove(app.PackageName);
            UpdateAppPickerCount();
        };

        // v3.0 v2.32.0 — real app icon to the left of the checkbox. The
        // bitmap was converted by AppListLoader on the background thread
        // (see AppIconCache), so a sync read here is safe even on cold
        // cache. When IconBitmap is null (icon load threw, or package
        // had no icon), the slot stays blank — better than a placeholder
        // glyph that'd draw user attention to a non-issue.
        var iconImage = new Image
        {
            Width = 24,
            Height = 24,
            VerticalAlignment = VerticalAlignment.Center,
            Stretch = Stretch.Uniform,
            Source = app.IconBitmap,
        };
        RenderOptions.SetBitmapInterpolationMode(iconImage, BitmapInterpolationMode.HighQuality);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
            ColumnSpacing = 8,
            Margin = new Thickness(8, 4),
        };
        Grid.SetColumn(checkbox, 0);
        Grid.SetColumn(iconImage, 1);
        Grid.SetColumn(rowText, 2);
        grid.Children.Add(checkbox);
        grid.Children.Add(iconImage);
        grid.Children.Add(rowText);

        // Tap anywhere on the row toggles the check.
        var clickable = new Border
        {
            Background = Brushes.Transparent,
            Child = grid,
        };
        clickable.PointerPressed += (_, __) =>
        {
            checkbox.IsChecked = !(checkbox.IsChecked == true);
        };
        return clickable;
    }

    private void UpdateAppPickerCount()
    {
        if (_appPickerCount is not null)
            _appPickerCount.Text = string.Format(Localization.PerAppCount, _appPickerSelected.Count);
    }

    private Border BuildAppPickerOverlay()
    {
        var title = new TextBlock
        {
            Text = Localization.PerAppTitle,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        title.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _appPickerCloseBtn = new Avalonia.Controls.Button
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
        _appPickerCloseBtn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "TextSecondaryBrush");
        _appPickerCloseBtn.Click += OnAppPickerCloseClicked;

        var titleBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(8, 4, 4, 4),
        };
        Grid.SetColumn(title, 0);
        Grid.SetColumn(_appPickerCloseBtn, 1);
        titleBar.Children.Add(title);
        titleBar.Children.Add(_appPickerCloseBtn);

        var titleBarBorder = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 4),
            Child = titleBar,
        };
        titleBarBorder.BindToken(Border.BackgroundProperty, "SurfaceRaisedBrush");
        titleBarBorder.BindToken(Border.BorderBrushProperty, "BorderSubtleBrush");

        // v3.0 v2.32.0 — include/exclude segmented control + hint, sitting
        // between the title bar and the search box. Tap include → only the
        // checked apps route via VPN; tap exclude → checked apps bypass VPN
        // (matches VpnRouterService.java's addAllowedApplication /
        // addDisallowedApplication branches).
        _appPickerModeLabel = new TextBlock
        {
            Text = Localization.PerAppPickerModeLabel,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = GetBrush("TextMutedBrush"),
            Margin = new Thickness(8, 6, 8, 2),
        };
        _appPickerModeIncludeBtn = MakeSegmentButton(
            Localization.PerAppModeInclude,
            _appPickerMode == "include",
            OnAppPickerModeIncludeClicked);
        _appPickerModeExcludeBtn = MakeSegmentButton(
            Localization.PerAppModeExclude,
            _appPickerMode == "exclude",
            OnAppPickerModeExcludeClicked);
        var modeRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 4,
            Margin = new Thickness(8, 0, 8, 4),
        };
        Grid.SetColumn(_appPickerModeIncludeBtn, 0);
        Grid.SetColumn(_appPickerModeExcludeBtn, 1);
        modeRow.Children.Add(_appPickerModeIncludeBtn);
        modeRow.Children.Add(_appPickerModeExcludeBtn);
        _appPickerModeHint = new TextBlock
        {
            Text = _appPickerMode == "exclude"
                ? Localization.PerAppHintExclude
                : Localization.PerAppHintInclude,
            FontSize = 9,
            Foreground = GetBrush("TextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 0, 8, 6),
        };

        _appPickerSearch = new TextBox
        {
            Watermark = Localization.PerAppSearchHint,
            FontSize = 12,
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            BorderThickness = new Thickness(1),
        };
        _appPickerSearch.BindToken(TextBox.BackgroundProperty, "SurfaceSunkenBrush");
        _appPickerSearch.BindToken(TextBox.BorderBrushProperty, "BorderSubtleBrush");
        _appPickerSearch.TextChanged += OnAppPickerSearchChanged;

        var systemToggleLabel = new TextBlock
        {
            Text = Localization.PerAppSystemAppsToggle,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        };
        systemToggleLabel.BindToken(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        _appPickerSystemToggle = new Avalonia.Controls.CheckBox
        {
            Content = systemToggleLabel,
            IsChecked = _appPickerSystemAppsVisible,
            MinHeight = 0,
            Padding = new Thickness(4, 0),
        };
        _appPickerSystemToggle.IsCheckedChanged += OnAppPickerSystemToggleChanged;

        _appPickerCount = new TextBlock
        {
            Text = string.Format(Localization.PerAppCount, 0),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _appPickerCount.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        var filterRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
            Margin = new Thickness(8, 6, 8, 0),
        };
        Grid.SetColumn(_appPickerSearch, 0);
        Grid.SetColumn(_appPickerCount, 1);
        filterRow.Children.Add(_appPickerSearch);
        filterRow.Children.Add(_appPickerCount);

        var togglesRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Margin = new Thickness(8, 4, 8, 4),
            Children = { _appPickerSystemToggle },
        };

        _appPickerList = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };

        _appPickerSaveBtn = new Avalonia.Controls.Button
        {
            Content = Localization.PerAppSaveButton,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 12),
            Margin = new Thickness(8, 6, 8, 8),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            BorderThickness = new Thickness(0),
        };
        _appPickerSaveBtn.BindToken(Avalonia.Controls.Button.BackgroundProperty, "AccentSolidBrush");
        _appPickerSaveBtn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "AccentOnSolidBrush");
        _appPickerSaveBtn.Click += OnAppPickerSaveClicked;

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(titleBarBorder, Dock.Top);
        DockPanel.SetDock(_appPickerModeLabel, Dock.Top);
        DockPanel.SetDock(modeRow, Dock.Top);
        DockPanel.SetDock(_appPickerModeHint, Dock.Top);
        DockPanel.SetDock(filterRow, Dock.Top);
        DockPanel.SetDock(togglesRow, Dock.Top);
        DockPanel.SetDock(_appPickerSaveBtn, Dock.Bottom);
        dock.Children.Add(titleBarBorder);
        dock.Children.Add(_appPickerModeLabel);
        dock.Children.Add(modeRow);
        dock.Children.Add(_appPickerModeHint);
        dock.Children.Add(filterRow);
        dock.Children.Add(togglesRow);
        dock.Children.Add(_appPickerSaveBtn);
        dock.Children.Add(_appPickerList);

        var overlay = new Border
        {
            IsVisible = false,
            Child = dock,
        };
        overlay.BindToken(Border.BackgroundProperty, "SurfaceAppBrush");
        return overlay;
    }

    private void OnMenuCopyLogPathClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        try
        {
            var ctx = global::Android.App.Application.Context;
            var extDir = ctx.GetExternalFilesDir(null);
            if (extDir is null)
            {
                ShowMenuFeedback(Localization.SaveStatusUnknown);
                return;
            }
            var logPath = System.IO.Path.Combine(extDir.AbsolutePath, "singbox.log");
            CopyToClipboard("singbox-log-path", logPath);
            ShowMenuFeedback(logPath);
        }
        catch (Exception ex)
        {
            ShowMenuFeedback($"Error: {ex.GetType().Name}");
        }
    }

    private void OnMenuUpdateCheckClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        // Phase 7.2 placeholder. Real auto-update on Android requires
        // the in-app updater (Google Play Asset Delivery) or sideload
        // flow with PackageInstaller. Out of scope for the v3.0
        // Android alpha — desktop UpdateChecker doesn't apply here
        // because Android's package manager refuses to install unsigned
        // APKs from arbitrary paths without REQUEST_INSTALL_PACKAGES.
        ShowMenuFeedback(Localization.MenuItemUpdateComingSoon);
    }

    private void OnMenuResetSettingsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_resetConfirmPending)
        {
            // Second tap — actually wipe.
            if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
            _resetConfirmPending = false;
            if (_menuResetSettingsItem is not null)
                _menuResetSettingsItem.Content = Localization.MenuItemResetSettings;

            try
            {
                AndroidStorage.SetVlessUri(null);
                AndroidStorage.SetSubscriptionUrl(null);
                AndroidStorage.SetServers(null);
                AndroidStorage.SetSelectedServerName(null);
                // Theme + language preserved (those are UI prefs, not
                // routing config) — same behaviour as desktop "Reset
                // routing settings" not nuking theme.
                ShowMenuFeedback(Localization.MenuItemResetDone);
            }
            catch (Exception ex)
            {
                ShowMenuFeedback($"Error: {ex.GetType().Name}");
            }
            return;
        }

        // First tap — show confirm prompt inline. Don't dismiss the
        // popup so the user can read the warning + tap the row again.
        _resetConfirmPending = true;
        if (_menuResetSettingsItem is not null)
            _menuResetSettingsItem.Content = Localization.MenuItemResetConfirm;
    }

    private void OnMenuRepoClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        try
        {
            var intent = new global::Android.Content.Intent(global::Android.Content.Intent.ActionView,
                global::Android.Net.Uri.Parse("https://github.com/PavelLizunov/VPNRouter"));
            intent.SetFlags(global::Android.Content.ActivityFlags.NewTask);
            global::Android.App.Application.Context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            ShowMenuFeedback($"Error: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// v2.32.0 — kebab menu entry that surfaces the Free Configs overlay.
    /// Closes the popup so the overlay opens cleanly above the main view.
    /// Heavy work (cache load + pool fetch) is deferred to the overlay
    /// itself.
    /// </summary>
    private void OnMenuFreeConfigsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        ShowFreeConfigsOverlay();
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
            await System.Threading.Tasks.Task.Delay(3000);
            if (_menuFeedback is not null && _menuFeedback.Text == text)
            {
                _menuFeedback.IsVisible = false;
            }
        }
        catch { /* swallow */ }
    }

    private void ToggleLanguageAndRefresh()
    {
        Localization.ToggleAndPersist();
        if (_brandTitle is not null) _brandTitle.Text = Localization.BrandTitle;
        // Phase 7.3 — segment controls re-style themselves via
        // RepaintLanguageSegment / RepaintThemeSegment; only the theme
        // segment label switches between RU/EN since it's localized.
        if (_menuThemeLight is not null) _menuThemeLight.Content = Localization.MenuSegLight;
        if (_menuThemeDark is not null) _menuThemeDark.Content = Localization.MenuSegDark;
        // RU/EN segment labels are locale-independent; nothing to update.
        // Phase 7.2 menu items
        if (_menuSettingsItem is not null) _menuSettingsItem.Content = Localization.MenuItemSettings;
        if (_menuOpenLogItem is not null) _menuOpenLogItem.Content = Localization.MenuItemOpenLogs;
        if (_menuCopyLogPathItem is not null) _menuCopyLogPathItem.Content = Localization.MenuItemCopyLogPath;
        if (_menuUpdateCheckItem is not null) _menuUpdateCheckItem.Content = Localization.MenuItemUpdateCheck;
        if (_menuResetSettingsItem is not null && !_resetConfirmPending)
            _menuResetSettingsItem.Content = Localization.MenuItemResetSettings;
        if (_menuVersionItem is not null)
            _menuVersionItem.Content = $"{Localization.MenuItemVersion} {VPNRouter.Core.AppVersion.Version}";
        if (_menuRepoItem is not null) _menuRepoItem.Content = Localization.MenuItemRepoLink;
        // Section headers
        if (_menuSectionView is not null) _menuSectionView.Text = Localization.MenuSectionView;
        if (_menuSectionDiagnostics is not null) _menuSectionDiagnostics.Text = Localization.MenuSectionDiagnostics;
        if (_menuSectionTroubleshooting is not null) _menuSectionTroubleshooting.Text = Localization.MenuSectionTroubleshooting;
        if (_menuSectionAbout is not null) _menuSectionAbout.Text = Localization.MenuSectionAbout;
        if (_menuSectionFreeConfigs is not null) _menuSectionFreeConfigs.Text = Localization.MenuSectionFreeConfigs;
        if (_menuFreeConfigsItem is not null) _menuFreeConfigsItem.Content = Localization.MenuItemOpenFreeConfigs;
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
        // v2.32.0 — refresh Subscribe overlay strings (title, add form,
        // refresh-all button, empty-state hint, per-card text).
        RefreshSubsLocalizedStrings();
        UpdateConfigSummary();
    }

    /// <summary>Pre-Phase-4 entry point retained so any stale subscribers
    /// don't break — delegates to the new ToggleLanguageAndRefresh.</summary>
    private void OnLanguageToggleClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ToggleLanguageAndRefresh();
}
