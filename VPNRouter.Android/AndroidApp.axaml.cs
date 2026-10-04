using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
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
using VPNRouter.UI.Controls;

namespace VPNRouter.Android;

internal static class StyledElementResourceExtensions
{
    public static T BindToken<T>(this T element, AvaloniaProperty prop, string key)
        where T : AvaloniaObject
    {
        element.Bind(prop, new DynamicResourceExtension(key));
        return element;
    }
}

public partial class AndroidApp : Avalonia.Application
{
    private StatusCard? _statusCard;

    private TextBlock? _statusHealthCheck;
    private TextBlock? _statusErrorOneLiner;
    private IconView? _statusHealthIcon;
    private DispatcherTimer? _diagnosticsTimer;
    private DateTime? _connectionStartedAt;
    private string? _lastError;
    private DateTime _lastErrorAt;
    private static readonly TimeSpan ErrorDisplayWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HealthProbeInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HealthStaleThreshold = TimeSpan.FromSeconds(60);
    private DateTime _lastHealthProbeAt;
    private long _lastHealthLogSize = -1;
    private DateTime _lastHealthLogMTime;
    private bool _lastHealthOk;
    private bool _firstProbePending;
    private string? _lastFormattedUptimeTitle;

    private TextBlock? _configRowLabel;
    private TextBlock? _configRowValue;
    private IconView? _configRowChevron;
    private TextBlock? _configRowToggleText;

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

    private TextBlock? _serverListHeader;
    private ListBox? _serverList;

    private Avalonia.Controls.Button? _ccModeSubBtn;
    private Avalonia.Controls.Button? _ccModeManualBtn;
    private Avalonia.Controls.Button? _ccModeCustomBtn;
    private StackPanel? _ccModeRow;
    private StackPanel? _ccCustomSection;
    private StackPanel? _ccUriSection;
    private TextBlock? _ccCustomLabel;
    private TextBlock? _ccCustomHint;
    private TextBox? _ccCustomInput;
    private TextBlock? _ccCustomStatus;
    private Avalonia.Controls.Button? _ccValidateBtn;
    private Avalonia.Controls.Button? _ccSaveCustomBtn;
    private Avalonia.Controls.Button? _ccClearCustomBtn;
    private string _ccMode = "manual";

    private Avalonia.Controls.Button? _ctaConnect;
    private Avalonia.Controls.Button? _ctaConnecting;
    private Avalonia.Controls.Button? _ctaDisconnect;

    private TextBlock? _advCardTitle;
    private TextBlock? _advCardSubtitle;

    private TextBlock? _brandTitle;
    private TextBlock? _vpnChip;
    private TextBlock? _zapretChip;
    private Image? _mascotImage;
    private Avalonia.Controls.Button? _kebabMenuButton;
    private Popup? _kebabPopup;
    private Avalonia.Controls.Button? _menuLangRu;
    private Avalonia.Controls.Button? _menuLangEn;
    private Avalonia.Controls.Button? _menuThemeLight;
    private Avalonia.Controls.Button? _menuThemeDark;
    private Avalonia.Controls.Button? _menuOpenLogItem;
    private Avalonia.Controls.Button? _menuExportDiagItem;
    private Avalonia.Controls.Button? _menuCopyLogPathItem;
    private Avalonia.Controls.Button? _menuViewCrashLogItem;
    private Avalonia.Controls.Button? _menuUpdateCheckItem;
    private Avalonia.Controls.Button? _menuResetSettingsItem;
    private Avalonia.Controls.Button? _menuVersionItem;
    private TextBlock? _menuAboutLabel;
    private TextBlock? _menuVersionPill;
    private Avalonia.Controls.Button? _menuAdvancedToggleBtn;
    private Avalonia.Controls.Button? _menuCheckLeaksItem;
    private Avalonia.Controls.Button? _menuHealthCheckItem;
    private Avalonia.Controls.Button? _menuAddTileItem;
    private Avalonia.Controls.Button? _menuRestartSafeModeItem;
    private TextBlock? _menuSectionView;
    private TextBlock? _menuSectionDiagnostics;
    private TextBlock? _menuSectionTroubleshooting;
    private TextBlock? _menuSectionAbout;
    private bool _resetConfirmPending = false;
    private TextBlock? _menuFeedback;

    private Thickness _currentSafeArea;
    private ScrollViewer? _mainScroller;
    private Grid? _mainContentGrid;
    private Grid? _pageRoot;
    private double _imeBottom;
    private Grid? _updateBannerFloating;

    private Border? _updateBanner;
    private TextBlock? _updateBannerTitle;
    private TextBlock? _updateBannerSubtitle;
    private Avalonia.Controls.Button? _updateBannerAction;
    private Avalonia.Controls.Button? _updateBannerDismiss;
    private global::VPNRouter.Core.Services.UpdateSources.UpdateSourceInfo? _pendingUpdate;
    private string? _downloadedApkPath;
    private bool _updateInFlight;

    private Border? _logOverlay;
    private TextBlock? _logViewerContent;
    private TextBlock? _logViewerEmptyState;
    private ScrollViewer? _logViewerScroller;
    private TextBlock? _logViewerTitle;
    private Avalonia.Controls.Button? _logViewerCloseBtn;
    private Avalonia.Controls.Button? _logViewerRefreshBtn;

    private Avalonia.Controls.RadioButton? _settingsSplitRadio;
    private Avalonia.Controls.RadioButton? _settingsFullRadio;
    private Avalonia.Controls.CheckBox? _settingsBypassRu;
    private Avalonia.Controls.ComboBox? _settingsDpiBypassMode;
    private Avalonia.Controls.ComboBox? _settingsDnsStrategy;
    private Avalonia.Controls.CheckBox? _settingsBlockAds;
    private Avalonia.Controls.CheckBox? _settingsReceivePrereleases;
    private TextBlock? _settingsCurrentVersion;
    private Avalonia.Controls.Button? _menuSettingsItem;
    private TextBlock? _reliabilityBatteryStatusLabel;
    private IconView? _reliabilityBatteryStatusIcon;
    private Avalonia.Controls.Button? _reliabilityBatteryButton;
    private Avalonia.Controls.CheckBox? _reliabilityAutoReconnect;
    private Avalonia.Controls.CheckBox? _externalControlToggle;
    private bool _settingsLoading = false;

    private readonly Avalonia.Controls.Button?[] _settingsSubSectionButtons = new Avalonia.Controls.Button?[6];
    private readonly Control?[] _settingsSubSectionPanels = new Control?[6];
    private int _settingsSelectedSubSection = 0;

    private bool _settingsDirty = false;
    private Border? _settingsAutoSavedBadge;
    private IconView? _settingsAutoSavedCheck;
    private Avalonia.Controls.Button? _settingsApplyButton;

    private Border? _profilesOverlay;
    private TextBlock? _profilesOverlayTitle;
    private TextBlock? _profilesOverlayIntro;
    private Avalonia.Controls.Button? _profilesCloseBtn;
    private StackPanel? _profilesList;

    private TextBox? _appPickerSearch;
    private Avalonia.Controls.CheckBox? _appPickerSystemToggle;
    private TextBlock? _appPickerCount;
    private TextBlock? _appPickerShowingCount;
    private ListBox? _appPickerList;
    private Avalonia.Controls.Button? _appPickerSaveBtn;
    private Avalonia.Controls.Button? _perAppPickButton;
    private TextBlock? _perAppCountLabel;
    private TextBlock? _autostartCardTitleText;
    private TextBlock? _autostartCardSubText;
    private List<AppListLoader.AppEntry> _appPickerCache = new();
    private HashSet<string> _appPickerSelected = new(System.StringComparer.OrdinalIgnoreCase);
    private bool _appPickerSystemAppsVisible = false;
    private Avalonia.Controls.Button? _appPickerModeIncludeBtn;
    private Avalonia.Controls.Button? _appPickerModeExcludeBtn;
    private TextBlock? _appPickerModeLabel;
    private TextBlock? _appPickerModeHint;
    private string _appPickerMode = "include";

    private string? _advAppsActiveCategoryId;
    private WrapPanel? _advAppsCategoryWrapHost;
    private TextBox? _advAppsNewCategoryInput;
    private Avalonia.Controls.Button? _advAppsAddCategoryBtn;
    private Border? _advAppsRightPaneScopeContainer;
    private List<VPNRouter.Core.Models.CustomCategory> _advAppsCustomCategories = new();
    private readonly Dictionary<string, Border> _advAppsCategoryRowMap = new(System.StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _advAppsCategoryCountMap = new(System.StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _advAppsCategoryNameMap = new(System.StringComparer.OrdinalIgnoreCase);

    private bool _formExpanded = true;
    private List<VlessServerEntry> _cachedServers = new();

    private enum ChipState { Off, Connecting, On }
    private ChipState _vpnChipState = ChipState.Off;
    private ChipState _zapretChipState = ChipState.Off;
    private System.Threading.CancellationTokenSource? _zapretPulseCts;
    private System.Threading.CancellationTokenSource? _vpnPulseCts;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        try { AndroidStorage.RepairAllOnLoad(); }
        catch (Exception ex)
        {
            try
            {
                global::Android.Util.Log.Warn("VpnRouter.SelfRepair",
                    $"RepairAllOnLoad in OnFrameworkInitialization failed: {ex.GetType().Name}: {ex.Message}");
            }
            catch { }
        }

        try
        {
            global::Java.Lang.JavaSystem.LoadLibrary("slipstream_jni");
            ServerUriParser.SlipstreamRuntimeAvailable = true;
            global::Android.Util.Log.Info("VpnRouter",
                "dns-tunnel: native Slipstream library available — dns-tunnel:// enabled");
        }
        catch (Exception ex)
        {
            ServerUriParser.SlipstreamRuntimeAvailable = false;
            global::Android.Util.Log.Info("VpnRouter",
                $"dns-tunnel: native Slipstream library unavailable ({ex.GetType().Name}) — dns-tunnel:// disabled");
        }

        Localization.LoadFromStorage();
        ApplyTheme();

        if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime singleView)
        {
            var view = BuildSimplePageView();
            singleView.MainView = view;
            AttachLifecycleEvents();
            UpdateConnectionState(MainActivity.IntendedConnected);
            ReloadServerList();

            _ = Task.Run(() => RunUpdateCheckAsync(manual: false));

            MainActivity.SafeAreaChanged += OnMainActivitySafeAreaChanged;
            MainActivity.ImeChanged += ime => Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyImeInset(ime));
            ApplySafeArea(MainActivity.CurrentSafeArea);

            view.AttachedToVisualTree += (sender, _) =>
            {
                try
                {
                    var topLevel = TopLevel.GetTopLevel(sender as Visual);
                    if (topLevel?.InsetsManager is { } insetsMgr)
                    {
                        // SafeAreaPadding is in dp only once the top level has its real render scaling. At attach time
                        // it is still 1.0, so the first value is in pixels and no insets event follows to correct it
                        // (a Pixel on Android 15+ showed a 134 dp gap at the top). Always read the property again when
                        // the insets, the scaling or the size change.
                        _avaloniaInsetsActive = true;
                        void Reapply() => ApplySafeArea(insetsMgr.SafeAreaPadding);
                        insetsMgr.SafeAreaChanged += (_, _) => Reapply();
                        topLevel.ScalingChanged += (_, _) => Reapply();
                        topLevel.PropertyChanged += (_, e) =>
                        {
                            if (e.Property == TopLevel.ClientSizeProperty) Reapply();
                        };
                        Reapply();
                    }
                }
                catch { }
            };

            EventHandler<Avalonia.VisualTreeAttachmentEventArgs>? attachHandler = null;
            attachHandler = (sender, _) =>
            {
                if (attachHandler != null && sender is Control c)
                    c.AttachedToVisualTree -= attachHandler;
                try
                {
                    if (!string.IsNullOrEmpty(MainActivity.LaunchCounterPath))
                        VPNRouter.Core.Services.LaunchFailureCounter.MarkStable(MainActivity.LaunchCounterPath);
                }
                catch { }

                try { ConsumeAndSurfaceRecoveryNotice(); }
                catch (Exception ex)
                {
                    global::Android.Util.Log.Warn("VpnRouter.SelfRepair",
                        $"recovery notice surfacing failed: {ex.GetType().Name}: {ex.Message}");
                }
            };
            view.AttachedToVisualTree += attachHandler;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private bool _avaloniaInsetsActive;

    private void OnMainActivitySafeAreaChanged(Thickness insets)
    {
        if (_avaloniaInsetsActive) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplySafeArea(insets));
    }

    /// <summary>
    /// Makes room for the on-screen keyboard: the whole page ends above it, and the bottom system-bar inset (which the
    /// keyboard covers) is not added on top of that.
    /// </summary>
    internal void ApplyImeInset(double imeBottom)
    {
        _imeBottom = Math.Max(0.0, imeBottom);
        if (_pageRoot is not null) _pageRoot.Margin = new Thickness(0, 0, 0, _imeBottom);
        ApplySafeArea(_currentSafeArea);
    }

    internal void ApplySafeArea(Thickness rawInsets)
    {
        _currentSafeArea = rawInsets;
        var insets = new Thickness(rawInsets.Left, rawInsets.Top, rawInsets.Right, _imeBottom > 0 ? 0.0 : rawInsets.Bottom);
        if (_mainContentGrid is not null)
        {
            var top = Math.Max(12.0, insets.Top + 6.0);
            var bottom = Math.Max(16.0, insets.Bottom + 16.0);
            _mainContentGrid.Margin = new Thickness(16, top, 16, bottom);
        }

        if (_updateBannerFloating is not null)
        {
            var top = Math.Max(16.0, insets.Top + 8.0);
            _updateBannerFloating.Margin = new Thickness(16, top, 16, 0);
        }

        ApplyAdvancedShellSafeArea(insets);
        ApplyOverlaySafeArea(insets);
    }

    private void ApplyOverlaySafeArea(Thickness insets)
    {
        var pad = new Thickness(0, Math.Max(0.0, insets.Top), 0, Math.Max(0.0, insets.Bottom));
        if (_logOverlay is not null) _logOverlay.Padding = pad;
        if (_cfgExportOverlay is not null) _cfgExportOverlay.Padding = pad;
        if (_cfgImportOverlay is not null) _cfgImportOverlay.Padding = pad;
        if (_profilesOverlay is not null) _profilesOverlay.Padding = pad;
    }

    private void ApplyTheme()
    {
        var pref = AndroidStorage.GetTheme();
        RequestedThemeVariant = pref switch
        {
            "dark" => ThemeVariant.Dark,
            "system" => ThemeVariant.Default,
            _ => ThemeVariant.Light,
        };

        try
        {
            MainActivity.Instance?.SetSystemBarsAppearance(RequestedThemeVariant == ThemeVariant.Dark);
        }
        catch { }
    }

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

    private Control BuildSimplePageView()
    {
        var radiusXs = GetRadius("RadiusXs");
        var radiusSm = GetRadius("RadiusSm");
        var radiusMd = GetRadius("RadiusMd");

        _formExpanded = string.IsNullOrWhiteSpace(AndroidStorage.GetSubscriptionUrl())
                        && string.IsNullOrWhiteSpace(AndroidStorage.GetVlessUri());

        var headerRow = BuildSimpleHeaderRow();

        var statusCard = BuildSimpleStatusCard();

        var configRowButton = BuildConfigRowButton(radiusXs, radiusSm);

        _ccMode = AndroidStorage.GetConfigMode();
        if (_ccMode != "subscribe" && _ccMode != "manual" && _ccMode != "custom")
            _ccMode = "manual";

        var inputSection = BuildServerInputSection(radiusXs);

        var tunnelSection = BuildTunnelModeSection();

        var autostartCard = BuildAutostartInlineCard(radiusSm);

        var inputCard = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radiusSm),
            Padding = new Thickness(10),
            Child = inputSection,
        };
        inputCard.BindToken(Border.BackgroundProperty, "SurfaceBaseBrush");
        inputCard.BindToken(Border.BorderBrushProperty, "BorderSubtleBrush");

        _formCard = new Border
        {
            IsVisible = _formExpanded,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radiusSm),
            Padding = new Thickness(10),
            Child = new StackPanel
            {
                Spacing = 11,
                Children = { tunnelSection, autostartCard }
            }
        };
        _formCard.BindToken(Border.BackgroundProperty, "SurfaceBaseBrush");
        _formCard.BindToken(Border.BorderBrushProperty, "BorderSubtleBrush");

        BuildConnectButtons();

        var advCardButton = BuildAdvancedCardButton(radiusSm, radiusMd);

        _menuFeedback = new TextBlock
        {
            Text = string.Empty,
            FontSize = UiScale.Fs(11),
            Padding = new Thickness(12, 8),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        _menuFeedback.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
        _menuFeedback.BindToken(TextBlock.BackgroundProperty, "SurfaceSunkenBrush");

        BuildUpdateBanner(radiusMd);

        var innerStack = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                headerRow,
                statusCard,
                _menuFeedback,
                _ctaConnect,
                _ctaConnecting,
                _ctaDisconnect,
                inputCard,
                configRowButton,
                _formCard,
                advCardButton,
            }
        };

        // Stretch with a maximum width is centered by the layout system; unlike Center it does not shrink the column to
        // the natural width of its content, which collapsed to a narrow strip once (all texts measured empty).
        var innerWrapper = new Grid
        {
            MaxWidth = 420,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children = { innerStack }
        };

        // The vertical room for the system bars is a margin of the content, not the ScrollViewer's Padding: the
        // scroll extent includes a child's margin but not the viewer's padding, so with Padding the last cards could
        // not be scrolled into view.
        var outerGrid = new Grid
        {
            Margin = new Thickness(16, 12, 16, 16),
            Background = Brushes.Transparent,
            Children = { innerWrapper }
        };
        _mainContentGrid = outerGrid;

        _mainScroller = new ScrollViewer
        {
            Content = outerGrid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Focusable = true,
        };
        _mainScroller.BindToken(ScrollViewer.BackgroundProperty, "SurfaceAppBrush");
        var mainScroller = _mainScroller;

        AttachTouchScrolling(mainScroller);

        _logOverlay = BuildLogOverlay();

        _cfgExportOverlay = BuildExportOverlay();
        _cfgImportOverlay = BuildImportOverlay();

        _profilesOverlay = BuildProfilesOverlay();

        _advShellOverlay = BuildAdvancedShellOverlay();

        _updateBannerFloating = new Grid
        {
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(16, 16, 16, 0),
            Background = Brushes.Transparent,
            Children = { _updateBanner! },
        };
        var updateBannerFloating = _updateBannerFloating;

        _pageRoot = new Grid
        {
            Children = { mainScroller, _logOverlay,
                         _cfgExportOverlay, _cfgImportOverlay, _profilesOverlay,
                         _advShellOverlay,
                         updateBannerFloating }
        };
        return _pageRoot;
    }

    private Grid BuildSimpleHeaderRow()
    {
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
            FontSize = UiScale.Fs(12),
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _brandTitle.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _vpnChip = MakeChip("VPN", "SurfaceSunkenBrush", "TextMutedBrush");
        _zapretChip = MakeChip("Zapret", "SurfaceSunkenBrush", "TextMutedBrush");

        var chipRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _vpnChip }
        };

        var brandStack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _brandTitle, chipRow }
        };

        _kebabMenuButton = new Avalonia.Controls.Button
        {
            Content = IconContent(UiIcons.EllipsisVertical, 22),
            Width = 44,
            Height = 44,
            Margin = new Thickness(0, -6, -8, -6),
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _kebabMenuButton.BindToken(Avalonia.Controls.Button.ForegroundProperty, "TextSecondaryBrush");
        _kebabMenuButton.Click += OnKebabMenuClicked;

        var isDark = AndroidStorage.GetTheme() == "dark";
        _menuThemeLight = MakeSegmentButton(Localization.MenuSegLight, !isDark, OnMenuThemeLightClicked);
        _menuThemeDark  = MakeSegmentButton(Localization.MenuSegDark,   isDark,  OnMenuThemeDarkClicked);
        var themeRow = MakeSegmentRow(_menuThemeLight, _menuThemeDark);

        _menuLangRu = MakeSegmentButton(Localization.MenuSegRu, Localization.Ru, OnMenuLangRuClicked);
        _menuLangEn = MakeSegmentButton(Localization.MenuSegEn, !Localization.Ru, OnMenuLangEnClicked);
        var langRow = MakeSegmentRow(_menuLangRu, _menuLangEn);

        _menuSettingsItem = null;
        _menuOpenLogItem  = MakeMenuItem(Localization.MenuItemOpenLogs,
                                         "TextPrimaryBrush", OnMenuOpenLogClicked, UiIcons.FileText);
        _menuExportDiagItem = MakeMenuItem(Localization.MenuItemExportDiag,
                                           "TextPrimaryBrush", OnMenuExportDiagClicked, UiIcons.Upload);
        _menuCopyLogPathItem = null;
        _menuViewCrashLogItem = null;
        _menuUpdateCheckItem = MakeMenuItem(Localization.MenuItemUpdateCheck,
                                            "TextPrimaryBrush", OnMenuUpdateCheckClicked, UiIcons.RefreshCw);
        _menuExportConfigItem = null;
        _menuImportConfigItem = null;
        _menuResetSettingsItem = MakeMenuItem(Localization.MenuItemResetSettings,
                                              "DangerSolidBrush", OnMenuResetSettingsClicked, UiIcons.Trash, closeMenu: false);
        _menuCheckLeaksItem = MakeMenuItem(Localization.MenuItemCheckLeaks,
                                           "TextPrimaryBrush", OnMenuCheckLeaksClicked, UiIcons.ShieldCheck);
        _menuHealthCheckItem = MakeMenuItem(Localization.MenuItemHealthCheck,
                                            "TextPrimaryBrush", OnMenuHealthCheckClicked, UiIcons.Activity);
        _menuRestartSafeModeItem = MakeMenuItem(Localization.MenuItemSafeMode,
                                                "TextPrimaryBrush", OnMenuRestartSafeModeClicked, UiIcons.LifeBuoy);
        _menuAboutLabel = new TextBlock
        {
            Text = Localization.SmpMenuAbout,
            FontSize = UiScale.Fs(11),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _menuAboutLabel.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _menuVersionPill = new TextBlock
        {
            Text = VPNRouter.Core.AppVersion.Version,
            FontSize = UiScale.Fs(9),
            FontFamily = new FontFamily("Consolas, 'SF Mono', 'Cascadia Code', 'Ubuntu Mono', monospace"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _menuVersionPill.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
        var aboutIcon = IconContent(UiIcons.Info, UiScale.Ic(16));
        aboutIcon.BindToken(IconView.ForegroundProperty, "TextPrimaryBrush");
        aboutIcon.VerticalAlignment = VerticalAlignment.Center;
        var aboutGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 6,
        };
        Grid.SetColumn(aboutIcon, 0);
        Grid.SetColumn(_menuAboutLabel, 1);
        Grid.SetColumn(_menuVersionPill, 2);
        aboutGrid.Children.Add(aboutIcon);
        aboutGrid.Children.Add(_menuAboutLabel);
        aboutGrid.Children.Add(_menuVersionPill);
        _menuVersionItem = new Avalonia.Controls.Button
        {
            Content = aboutGrid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            MinHeight = 44,
            Padding = new Thickness(12, 8),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
        };
        _menuVersionItem.Click += (s, e) =>
        {
            if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
            OnMenuRepoClicked(s, e);
        };

        var menuStack = new StackPanel
        {
            Spacing = 1,
        };

        AppendMenuSectionWithControls(menuStack, Localization.MenuSectionView,
                                      new Control[] { themeRow, langRow });
        AppendMenuSection(menuStack, Localization.MenuSectionDiagnostics,
                          new[] { _menuOpenLogItem, _menuExportDiagItem, _menuCheckLeaksItem,
                                  _menuUpdateCheckItem });
        AppendMenuSection(menuStack, Localization.MenuSectionTroubleshooting,
                          new[] { _menuHealthCheckItem, _menuRestartSafeModeItem,
                                  _menuResetSettingsItem });
        _menuAddTileItem = MakeMenuItem(Localization.MenuItemAddTile,
                                        "TextPrimaryBrush", OnMenuAddTileClicked, UiIcons.SquarePlus);
        menuStack.Children.Add(_menuAddTileItem);
        menuStack.Children.Add(_menuVersionItem);

        var advancedToggleBtn = new Avalonia.Controls.Button
        {
            Content = IconText(UiIcons.ChevronRight, Localization.SmpToggleToAdvanced, 14, iconAfter: true),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 8),
            Margin = new Thickness(0, 6, 0, 0),
            FontSize = UiScale.Fs(11),
            FontWeight = FontWeight.SemiBold,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
        };
        advancedToggleBtn.BindToken(Avalonia.Controls.Button.BackgroundProperty, "AccentSolidBrush");
        advancedToggleBtn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "AccentOnSolidBrush");
        advancedToggleBtn.Click += (_, _) =>
        {
            if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
            if (_advShellOverlay is { IsVisible: true }) CloseAdvancedShell();
            else OpenAdvancedShell(AdvancedTab.Servers);
        };
        _menuAdvancedToggleBtn = advancedToggleBtn;
        menuStack.Children.Add(advancedToggleBtn);

        var menuPanel = new Border
        {
            Width = 288,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(GetRadius("RadiusMd")),
            Padding = new Thickness(6),
            Child = menuStack,
        };
        menuPanel.BindToken(Border.BackgroundProperty, "SurfaceBaseBrush");
        menuPanel.BindToken(Border.BorderBrushProperty, "BorderDefaultBrush");
        menuPanel.BindToken(Border.BoxShadowProperty, "ShadowMd");

        _kebabPopup = new Popup
        {
            PlacementTarget = _kebabMenuButton,
            Placement = PlacementMode.BottomEdgeAlignedRight,
            Child = menuPanel,
            IsLightDismissEnabled = true,
        };

        HideSubtreeFromAccessibility(menuPanel);

        var headerRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("28,*,Auto"),
            ColumnSpacing = 10,
        };
        Grid.SetColumn(mascot, 0);
        Grid.SetColumn(brandStack, 1);
        Grid.SetColumn(_kebabMenuButton, 2);
        headerRow.Children.Add(mascot);
        headerRow.Children.Add(brandStack);
        headerRow.Children.Add(_kebabMenuButton);
        headerRow.Children.Add(_kebabPopup);

        return headerRow;
    }

    private StackPanel BuildSimpleStatusCard()
    {
        _statusCard = new StatusCard
        {
            IsOff = true,
            IsOn = false,
            IsWarn = false,
            Title = Localization.SimpleStatusTitleOff,
            Subtitle = Localization.SimpleStatusDescOff,
            Emblem = LoadMascot(),
        };
        _statusCard.Clicked += (s, e) => OnConnectClicked(s, new Avalonia.Interactivity.RoutedEventArgs());

        _statusHealthCheck = new TextBlock
        {
            Text = string.Empty,
            FontSize = UiScale.Fs(10),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
            LineHeight = UiScale.Lh(14),
            IsVisible = false,
        };
        _statusHealthCheck.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
        var healthRow = WithStatusIcon(_statusHealthCheck, out var healthIcon, centered: true);
        _statusHealthIcon = healthIcon;

        _statusErrorOneLiner = new TextBlock
        {
            Text = string.Empty,
            FontSize = UiScale.Fs(10),
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
            LineHeight = UiScale.Lh(14),
            IsVisible = false,
        };
        _statusErrorOneLiner.BindToken(TextBlock.ForegroundProperty, "DangerFgBrush");

        var statusCard = new StackPanel
        {
            Spacing = 6,
            Children = { _statusCard, healthRow, _statusErrorOneLiner },
        };

        return statusCard;
    }

    private Avalonia.Controls.Button BuildConfigRowButton(double radiusXs, double radiusSm)
    {
        var flagGlyph = IconContent(UiIcons.Flag, 14);
        flagGlyph.BindToken(IconView.ForegroundProperty, "AccentFgBrush");
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
            FontSize = UiScale.Fs(9),
            FontWeight = FontWeight.SemiBold,
        };
        _configRowLabel.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        _configRowValue = new TextBlock
        {
            Text = Localization.SimpleConfigSummary,
            FontSize = UiScale.Fs(11),
            FontWeight = FontWeight.SemiBold,
        };
        _configRowValue.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        // A pill with a word and a chevron instead of a bare grey arrow, so the row reads as something to open and close.
        _configRowChevron = IconContent(UiIcons.ChevronDown, UiScale.Ic(14));
        SetDisclosureChevron(_configRowChevron, _formExpanded);
        _configRowChevron.BindToken(IconView.ForegroundProperty, "AccentFgBrush");
        _configRowToggleText = new TextBlock
        {
            Text = _formExpanded ? Localization.SmpConfigRowHide : Localization.SmpConfigRowChange,
            FontSize = UiScale.Fs(10),
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _configRowToggleText.BindToken(TextBlock.ForegroundProperty, "AccentFgBrush");
        var togglePill = new Border
        {
            Padding = new Thickness(10, 5, 8, 5),
            CornerRadius = new CornerRadius(radiusSm),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 4,
                Children = { _configRowToggleText, _configRowChevron },
            },
        };
        togglePill.BindToken(Border.BackgroundProperty, "AccentBgSubtleBrush");

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
        Grid.SetColumn(togglePill, 2);
        configRowGrid.Children.Add(togglePill);

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

        return configRowButton;
    }

    private StackPanel BuildServerInputSection(double radiusXs)
    {
        _serverInputLabel = new TextBlock
        {
            Text = Localization.SmpInputLabel,
            FontSize = UiScale.Fs(11),
            FontWeight = FontWeight.SemiBold,
        };
        _serverInputLabel.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _serverInput = new TextBox
        {
            FontSize = UiScale.Fs(11),
            Padding = new Thickness(10, 7),
            AcceptsReturn = false,
            CornerRadius = new CornerRadius(radiusXs),
            Watermark = Localization.SmpInputWatermark,
        };
        var existingSub = AndroidStorage.GetSubscriptionUrl();
        var existingUri = AndroidStorage.GetVlessUri();
        _serverInput.Text = existingSub ?? existingUri ?? string.Empty;

        var smpQrButton = new Avalonia.Controls.Button
        {
            Content = IconContent(UiIcons.ScanQrCode, 22),
            Width = 44,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(radiusXs),
        };
        smpQrButton.BindToken(Avalonia.Controls.Button.BackgroundProperty, "AccentBgSubtleBrush");
        smpQrButton.BindToken(Avalonia.Controls.Button.ForegroundProperty, "AccentFgBrush");
        ToolTip.SetTip(smpQrButton, Localization.SmpScanQrButton);
        smpQrButton.Click += OnSimpleQrScanClicked;

        var inputRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 6,
        };
        Grid.SetColumn(_serverInput, 0);
        Grid.SetColumn(smpQrButton, 1);
        inputRow.Children.Add(_serverInput);
        inputRow.Children.Add(smpQrButton);

        _serverInputHint = new TextBlock
        {
            Text = Localization.SmpInputHint,
            FontSize = UiScale.Fs(9),
            TextWrapping = TextWrapping.Wrap,
        };
        _serverInputHint.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        _serverInputError = new TextBlock
        {
            FontSize = UiScale.Fs(10),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        _serverInputError.BindToken(TextBlock.ForegroundProperty, "DangerFgBrush");

        var inputSection = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                _serverInputLabel,
                inputRow,
                _serverInputHint,
                _serverInputError,
            },
        };

        return inputSection;
    }

    private StackPanel BuildTunnelModeSection()
    {
        _tunnelModeLabel = new TextBlock
        {
            Text = Localization.SmpTunnelModeLabel,
            FontSize = UiScale.Fs(11),
            FontWeight = FontWeight.SemiBold,
        };
        _tunnelModeLabel.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _splitLabel = new TextBlock
        {
            Text = Localization.SmpSplitOption,
            FontSize = UiScale.Fs(11),
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _splitLabel.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _splitRadio = new Avalonia.Controls.RadioButton
        {
            GroupName = "TunnelMode",
            IsChecked = AndroidStorage.GetPerAppMode() != "off",
            Content = _splitLabel,
            FontSize = UiScale.Fs(11),
            FontWeight = FontWeight.SemiBold,
            MinHeight = 0,
            Padding = new Thickness(4, 0),
        };
        _splitRadio.IsCheckedChanged += OnTunnelModeRadioChanged;
        _splitHint = new TextBlock
        {
            Text = Localization.SmpSplitHint,
            FontSize = UiScale.Fs(9),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, 0, 0, 0),
        };
        _splitHint.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
        _fullLabel = new TextBlock
        {
            Text = Localization.SmpFullOption,
            FontSize = UiScale.Fs(11),
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _fullLabel.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _fullRadio = new Avalonia.Controls.RadioButton
        {
            GroupName = "TunnelMode",
            IsChecked = AndroidStorage.GetPerAppMode() == "off",
            Content = _fullLabel,
            FontSize = UiScale.Fs(11),
            FontWeight = FontWeight.SemiBold,
            MinHeight = 0,
            Padding = new Thickness(4, 0),
        };
        _fullRadio.IsCheckedChanged += OnTunnelModeRadioChanged;
        _fullHint = new TextBlock
        {
            Text = Localization.SmpFullHint,
            FontSize = UiScale.Fs(9),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, 0, 0, 0),
        };
        _fullHint.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

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
            FontSize = UiScale.Fs(9),
            Margin = new Thickness(24, 2, 0, 0),
        };
        _perAppCountLabel.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        var perAppStack = new StackPanel
        {
            Spacing = 0,
            IsVisible = AndroidStorage.GetPerAppMode() != "off",
            Children = { _perAppPickButton, _perAppCountLabel },
        };
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

        return tunnelSection;
    }

    private void BuildConnectButtons()
    {
        const double ctaRadius = 14;
        const double ctaMinHeight = 52;

        _ctaConnect = new Avalonia.Controls.Button
        {
            Content = Localization.ButtonConnect,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            MinHeight = ctaMinHeight,
            Padding = new Thickness(0, 12),
            FontSize = UiScale.Fs(14),
            FontWeight = FontWeight.Bold,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(ctaRadius),
            IsVisible = true,
        };
        _ctaConnect.BindToken(Avalonia.Controls.Button.BackgroundProperty, "AccentSolidBrush");
        _ctaConnect.BindToken(Avalonia.Controls.Button.ForegroundProperty, "AccentOnSolidBrush");
        _ctaConnect.Click += OnConnectClicked;

        _ctaConnecting = new Avalonia.Controls.Button
        {
            Content = Localization.ButtonConnecting,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            MinHeight = ctaMinHeight,
            Padding = new Thickness(0, 12),
            FontSize = UiScale.Fs(14),
            FontWeight = FontWeight.Bold,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(ctaRadius),
            IsEnabled = false,
            IsVisible = false,
        };
        _ctaConnecting.BindToken(Avalonia.Controls.Button.BackgroundProperty, "SurfaceSunkenBrush");
        _ctaConnecting.BindToken(Avalonia.Controls.Button.ForegroundProperty, "TextSecondaryBrush");

        _ctaDisconnect = new Avalonia.Controls.Button
        {
            Content = Localization.ButtonDisconnect,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            MinHeight = ctaMinHeight,
            Padding = new Thickness(0, 12),
            FontSize = UiScale.Fs(14),
            FontWeight = FontWeight.Bold,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(ctaRadius),
            IsVisible = false,
        };
        _ctaDisconnect.BindToken(Avalonia.Controls.Button.BackgroundProperty, "SurfaceBaseBrush");
        _ctaDisconnect.BindToken(Avalonia.Controls.Button.ForegroundProperty, "TextPrimaryBrush");
        _ctaDisconnect.BindToken(Avalonia.Controls.Button.BorderBrushProperty, "BorderStrongBrush");
        _ctaDisconnect.Click += OnConnectClicked;
    }

    private Avalonia.Controls.Button BuildAdvancedCardButton(double radiusSm, double radiusMd)
    {
        _advCardTitle = new TextBlock
        {
            Text = Localization.SmpAdvCardTitle,
            FontSize = UiScale.Fs(12),
            FontWeight = FontWeight.SemiBold,
        };
        _advCardTitle.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _advCardSubtitle = new TextBlock
        {
            Text = Localization.SmpAdvCardSubtitle,
            FontSize = UiScale.Fs(9),
            TextWrapping = TextWrapping.Wrap,
        };
        _advCardSubtitle.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
        var chevronGlyph = IconContent(UiIcons.ChevronRight, 18);
        chevronGlyph.BindToken(IconView.ForegroundProperty, "AccentFgBrush");
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

        return advCardButton;
    }

    private void AttachTouchScrolling(ScrollViewer mainScroller)
    {
        const double scrollStartDistance = 8.0;
        bool isDragging = false;
        double dragStartY = 0;
        double lastY = 0;
        IPointer? activePointer = null;
        mainScroller.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            var p = e.GetCurrentPoint(mainScroller);
            if (p.Pointer.Type != PointerType.Touch && p.Pointer.Type != PointerType.Pen)
                return;
            isDragging = false;
            dragStartY = p.Position.Y;
            lastY = p.Position.Y;
            activePointer = e.Pointer;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        mainScroller.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            var p = e.GetCurrentPoint(mainScroller);
            if (p.Pointer.Type != PointerType.Touch && p.Pointer.Type != PointerType.Pen)
                return;
            if (activePointer is null) return;
            var totalDeltaY = p.Position.Y - dragStartY;
            if (!isDragging)
            {
                if (System.Math.Abs(totalDeltaY) < scrollStartDistance) return;
                isDragging = true;
                e.Pointer.Capture(mainScroller);
                try
                {
                    var topLevel = global::Avalonia.Controls.TopLevel.GetTopLevel(mainScroller);
                    var focused = topLevel?.FocusManager?.GetFocusedElement();
                    if (focused is Avalonia.Controls.TextBox)
                    {
                        mainScroller.Focus();
                        HideAndroidSoftKeyboard();
                    }
                }
                catch
                {
                }
            }
            var dy = p.Position.Y - lastY;
            lastY = p.Position.Y;
            var maxOffset = System.Math.Max(0,
                mainScroller.Extent.Height - mainScroller.Viewport.Height);
            var newY = System.Math.Max(0,
                System.Math.Min(mainScroller.Offset.Y - dy, maxOffset));
            mainScroller.Offset = new Vector(mainScroller.Offset.X, newY);
            e.Handled = true;
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        mainScroller.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (isDragging)
            {
                e.Pointer.Capture(null);
                e.Handled = true;
            }
            isDragging = false;
            activePointer = null;
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        mainScroller.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) =>
        {
            isDragging = false;
            activePointer = null;
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static Bitmap? _mascotLight;
    private static Bitmap? _mascotDark;

    private Bitmap LoadMascot()
    {
        if (_mascotLight is null)
        {
            try
            {
                using var stream = AssetLoader.Open(new Uri("avares://VPNRouter.Android/Assets/penguin_mascot.png"));
                _mascotLight = new Bitmap(stream);
            }
            catch
            {
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
            for (int i = 0; i + 3 < bytes.Length; i += 4)
            {
                bytes[i + 0] = (byte)(255 - bytes[i + 0]);
                bytes[i + 1] = (byte)(255 - bytes[i + 1]);
                bytes[i + 2] = (byte)(255 - bytes[i + 2]);
            }
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, fb.Address, byteCount);
            return wb;
        }
        catch
        {
            return null;
        }
    }

    private TextBlock MakeChip(string label, string bgKey, string fgKey)
    {
        var tb = new TextBlock
        {
            Text = label,
            FontSize = UiScale.Fs(9),
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(7, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        tb.BindToken(TextBlock.ForegroundProperty, fgKey);
        tb.BindToken(TextBlock.BackgroundProperty, bgKey);
        AddChipTransitions(tb);
        return tb;
    }

    private Avalonia.Controls.Button StyledSecondaryButton(string label)
    {
        var btn = new Avalonia.Controls.Button
        {
            Content = label,
            FontSize = UiScale.Fs(12),
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

    /// <summary>A down chevron that points up while its section is open (the "Hide" state).</summary>
    private static void SetDisclosureChevron(IconView? chevron, bool open)
    {
        if (chevron is null) return;
        if (chevron.Transitions is null && UiMotion.Enabled)
        {
            chevron.Transitions = new Avalonia.Animation.Transitions
            {
                new Avalonia.Animation.TransformOperationsTransition
                {
                    Property = Visual.RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(180),
                    Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
                },
            };
        }
        chevron.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse(open ? "rotate(180deg)" : "rotate(0deg)");
    }

    private void OnConfigRowClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _formExpanded = !_formExpanded;
        if (_formCard is not null) _formCard.IsVisible = _formExpanded;
        SetDisclosureChevron(_configRowChevron, _formExpanded);
        if (_configRowToggleText is not null)
            _configRowToggleText.Text = _formExpanded ? Localization.SmpConfigRowHide : Localization.SmpConfigRowChange;
    }

    private void OnSimpleQrScanClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var activity = MainActivity.Instance;
        if (activity is null) return;
        MainActivity.PendingQrScanCallback = (success, text) =>
        {
            Dispatcher.UIThread.Post(async () =>
            {
                if (!success)
                {
                    if (text == "cancelled") return;
                    var msg = text switch
                    {
                        "permission_denied" => Localization.SmpQrPermissionDenied,
                        _                    => Localization.SmpQrNotRecognized,
                    };
                    ShowMenuFeedback(msg);
                    return;
                }
                await ApplyScannedTextAsync(text);
            });
        };
        activity.RequestQrCodeScan();
    }

    private static void HideAndroidSoftKeyboard()
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            if (ctx is null) return;
            var imm = (global::Android.Views.InputMethods.InputMethodManager?)
                ctx.GetSystemService(global::Android.Content.Context.InputMethodService);
            if (imm is null) return;
            var activity = global::VPNRouter.Android.MainActivity.Instance;
            var token = activity?.Window?.DecorView?.WindowToken;
            if (token is null) return;
            imm.HideSoftInputFromWindow(token, global::Android.Views.InputMethods.HideSoftInputFlags.None);
        }
        catch
        {
        }
    }

    private void UpdateConfigSummary()
    {
        if (_configRowValue is null) return;
        var mode = _fullRadio?.IsChecked == true ? Localization.SmpFullOption : Localization.SmpSplitOption;
        string src;
        switch (AndroidStorage.GetConfigMode())
        {
            case "subscribe":
                src = Localization.SmpSourceSubscription;
                break;
            case "custom":
                src = Localization.CcSourceCustom;
                break;
            default:
                src = Localization.SmpSourceManual;
                break;
        }
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
                AndroidStorage.SetConfigMode("manual");
                _ccMode = "manual";
                ApplyCcModeVisuals();
                _cachedServers = new List<VlessServerEntry>();
                UpdateServerListView();
                UpdateConfigSummary();
            }
            catch (PlaceholderConfigException ex)
            {
                global::Android.Util.Log.Warn("VpnRouter.Simple",
                    $"Paste: placeholder rejected — field={ex.OffendingField}");
                _serverInputError.Text = Localization.PlaceholderCredentialRejected;
                _serverInputError.IsVisible = true;
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
            AndroidStorage.SetConfigMode("subscribe");
            _ccMode = "subscribe";
            ApplyCcModeVisuals();
            UpdateConfigSummary();
            return;
        }

        _serverInputError.Text = Localization.SaveStatusUnknown;
        _serverInputError.IsVisible = true;
    }


    private void OnMenuCheckLeaksClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        try
        {
            var intent = new global::Android.Content.Intent(
                global::Android.Content.Intent.ActionView,
                global::Android.Net.Uri.Parse("https://ipleak.net/"));
            intent.SetFlags(global::Android.Content.ActivityFlags.NewTask);
            global::Android.App.Application.Context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            ShowMenuFeedback($"Error: {ex.GetType().Name}");
        }
    }

    private async void OnMenuHealthCheckClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        if (_menuHealthCheckItem is not null) _menuHealthCheckItem.IsEnabled = false;
        try
        {
            var report = await System.Threading.Tasks.Task.Run(() =>
            {
                var results = VPNRouter.Core.Services.HealthCheck.RunAll();
                var formatted = VPNRouter.Core.Services.HealthCheck.FormatReport(results);

                var ctx = global::Android.App.Application.Context;
                var filesDir = ctx.FilesDir?.AbsolutePath
                               ?? VPNRouter.Core.AppPaths.DataDir;
                var reportPath = System.IO.Path.Combine(filesDir, "last-health-check.txt");
                try
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(reportPath)!);
                    System.IO.File.WriteAllText(reportPath, formatted);
                }
                catch { }

                return formatted;
            });

            if (_logOverlay is null) return;
            if (_logViewerTitle is not null)
                _logViewerTitle.Text = Localization.MenuItemHealthCheck;
            if (_logViewerContent is not null)
            {
                _logViewerContent.Text = report;
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
            _logOverlay.IsVisible = true;
        }
        catch (Exception ex)
        {
            ShowMenuFeedback($"Error: {ex.GetType().Name}");
        }
        finally
        {
            if (_menuHealthCheckItem is not null) _menuHealthCheckItem.IsEnabled = true;
        }
    }

    private void OnMenuRestartSafeModeClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        try
        {
            try { AndroidStorage.SetSafeModeOnNextLaunch(true); }
            catch { }

            var ctx = global::Android.App.Application.Context;
            var pkg = ctx.PackageName;
            if (string.IsNullOrEmpty(pkg)) return;
            var launchIntent = ctx.PackageManager?.GetLaunchIntentForPackage(pkg);
            if (launchIntent is null) return;
            launchIntent.AddFlags(global::Android.Content.ActivityFlags.ClearTop
                                | global::Android.Content.ActivityFlags.NewTask);
            ctx.StartActivity(launchIntent);
            global::Java.Lang.JavaSystem.Exit(0);
        }
        catch (Exception ex)
        {
            ShowMenuFeedback($"Error: {ex.GetType().Name}");
        }
    }

    private void ToggleLanguageAndRefresh()
    {
        Localization.ToggleAndPersist();
        if (_brandTitle is not null) _brandTitle.Text = Localization.BrandTitle;
        if (_menuThemeLight is not null) _menuThemeLight.Content = Localization.MenuSegLight;
        if (_menuThemeDark is not null) _menuThemeDark.Content = Localization.MenuSegDark;
        if (_menuSettingsItem is not null) SetMenuItemText(_menuSettingsItem, Localization.MenuItemSettings);
        if (_menuOpenLogItem is not null) SetMenuItemText(_menuOpenLogItem, Localization.MenuItemOpenLogs);
        if (_menuExportDiagItem is not null) SetMenuItemText(_menuExportDiagItem, Localization.MenuItemExportDiag);
        if (_menuCopyLogPathItem is not null) SetMenuItemText(_menuCopyLogPathItem, Localization.MenuItemCopyLogPath);
        if (_menuViewCrashLogItem is not null) SetMenuItemText(_menuViewCrashLogItem, Localization.MenuItemViewCrashLog);
        if (_menuUpdateCheckItem is not null) SetMenuItemText(_menuUpdateCheckItem, Localization.MenuItemUpdateCheck);
        if (_menuResetSettingsItem is not null && !_resetConfirmPending)
            SetMenuItemText(_menuResetSettingsItem, Localization.MenuItemResetSettings);
        if (_menuAboutLabel is not null) _menuAboutLabel.Text = Localization.SmpMenuAbout;
        if (_menuAdvancedToggleBtn is not null)
            UpdateMenuAdvancedToggle();
        if (_autostartCardTitleText is not null)
            _autostartCardTitleText.Text = Localization.SmpAutostartCardTitle;
        if (_autostartCardSubText is not null)
            _autostartCardSubText.Text = Localization.SmpAutostartCardSubtitle;
        if (_perAppPickButton is not null)
            _perAppPickButton.Content = Localization.PerAppPickButton;
        if (_menuSectionView is not null) _menuSectionView.Text = Localization.MenuSectionView;
        if (_menuSectionDiagnostics is not null) _menuSectionDiagnostics.Text = Localization.MenuSectionDiagnostics;
        if (_menuSectionTroubleshooting is not null) _menuSectionTroubleshooting.Text = Localization.MenuSectionTroubleshooting;
        if (_menuSectionAbout is not null) _menuSectionAbout.Text = Localization.MenuSectionAbout;
        RefreshAdvancedShellStrings();
        if (_menuCheckLeaksItem is not null) SetMenuItemText(_menuCheckLeaksItem, Localization.MenuItemCheckLeaks);
        if (_menuHealthCheckItem is not null) SetMenuItemText(_menuHealthCheckItem, Localization.MenuItemHealthCheck);
        if (_menuAddTileItem is not null) SetMenuItemText(_menuAddTileItem, Localization.MenuItemAddTile);
        if (_menuRestartSafeModeItem is not null) SetMenuItemText(_menuRestartSafeModeItem, Localization.MenuItemSafeMode);
        if (_profilesOverlayTitle is not null) _profilesOverlayTitle.Text = Localization.ProfilesOverlayTitle;
        if (_profilesOverlayIntro is not null) _profilesOverlayIntro.Text = Localization.ProfilesIntro;
        RefreshConfigShareLocalization();
        if (_statusCard is not null)
        {
            _statusCard.Title = MainActivity.IntendedConnected ? Localization.SimpleStatusTitleOn : Localization.SimpleStatusTitleOff;
            _statusCard.Subtitle = MainActivity.IntendedConnected ? Localization.SimpleStatusDescOn : Localization.SimpleStatusDescOff;
        }
        ApplyHealthCheckDisplay();
        ApplyErrorOneLinerDisplay();
        if (_configRowLabel is not null) _configRowLabel.Text = Localization.SmpConfigRowLabel;
        if (_configRowToggleText is not null)
            _configRowToggleText.Text = _formExpanded ? Localization.SmpConfigRowHide : Localization.SmpConfigRowChange;
        if (_serverInputLabel is not null) _serverInputLabel.Text = Localization.SmpInputLabel;
        if (_serverInput is not null) _serverInput.Watermark = Localization.SmpInputWatermark;
        if (_serverInputHint is not null) _serverInputHint.Text = Localization.SmpInputHint;
        if (_ccModeSubBtn is not null) _ccModeSubBtn.Content = Localization.CcModeSubscription;
        if (_ccModeManualBtn is not null) _ccModeManualBtn.Content = Localization.CcModeManual;
        if (_ccModeCustomBtn is not null) _ccModeCustomBtn.Content = Localization.CcModeCustom;
        if (_ccCustomLabel is not null) _ccCustomLabel.Text = Localization.CcCustomLabel;
        if (_ccCustomHint is not null) _ccCustomHint.Text = Localization.CcCustomHint;
        if (_ccCustomInput is not null) _ccCustomInput.Watermark = Localization.CcCustomWatermark;
        if (_ccValidateBtn is not null) _ccValidateBtn.Content = Localization.CcValidateButton;
        if (_ccSaveCustomBtn is not null) _ccSaveCustomBtn.Content = Localization.CcSaveButton;
        if (_ccClearCustomBtn is not null) _ccClearCustomBtn.Content = Localization.CcClearButton;
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
        RefreshSubsLocalizedStrings();
        if (_updateBannerDismiss is not null) _updateBannerDismiss.Content = Localization.UpdateButtonDismiss;
        if (_updateBannerSubtitle is not null && _updateBannerSubtitle.IsVisible)
            _updateBannerSubtitle.Text = Localization.UpdateBannerSubtitle;
        RefreshServerListLocalizedStrings();
        UpdateConfigSummary();
    }

}
