using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using VPNRouter.Core.Services.FreeConfigs;

namespace VPNRouter.Android;

/// <summary>
/// v2.32.0 — Android Free Configs overlay. Mirrors desktop's
/// <c>VPNRouter.App/Views/Pages/FreeConfigsPage.axaml</c> master-detail
/// layout (Поиск + Сохранённые sub-tabs, list rows with country flag /
/// endpoint / latency badge, bottom Connect CTA). Wires to
/// <see cref="AndroidFreeConfigsOrchestrator"/> for the actual fetch /
/// test pipeline.
///
/// <para>See <c>plans/v2.32.0-android-free-configs.md</c> for the
/// element-by-element delta vs desktop and the explicit "not ported" list
/// (Deep Verify, bandwidth column, per-row Recheck — all rely on
/// desktop-only sing-box.exe spawning).</para>
/// </summary>
public partial class AndroidApp
{
    // AND-MIGRATE-OVERLAYS (2026-05-09): the standalone Free-Configs
    // overlay is gone — content moves into the Advanced shell as the
    // "Public configs" tab.
    private AndroidFreeConfigsOrchestrator? _fcOrchestrator;

    // Sub-tab state. 0 = Search, 1 = Saved.
    private int _fcSelectedTab;
    private Avalonia.Controls.Button? _fcTabSearch;
    private Avalonia.Controls.Button? _fcTabSaved;

    // Body containers (one per tab; toggled IsVisible).
    private Control? _fcSearchBody;
    private Control? _fcSavedBody;

    // Search-tab widgets
    private Avalonia.Controls.Button? _fcFindButton;
    private Avalonia.Controls.Button? _fcStopButton;
    private NumericUpDown? _fcTargetInput;
    private NumericUpDown? _fcMaxPingInput;
    private Avalonia.Controls.CheckBox? _fcExcludeRu;
    private Border? _fcAdvancedPanel;
    private Avalonia.Controls.Button? _fcAdvancedToggle;
    private bool _fcAdvancedExpanded;
    private ListBox? _fcSearchList;
    private TextBlock? _fcSearchEmptyHint;

    // Saved-tab widgets
    private ListBox? _fcSavedList;
    private TextBlock? _fcSavedEmptyHint;
    private Avalonia.Controls.Button? _fcClearAllButton;
    private TextBlock? _fcSavedHint;

    // Bottom action bar
    private TextBlock? _fcStatusText;
    private Avalonia.Controls.ProgressBar? _fcProgress;
    private TextBlock? _fcProgressLabel;
    private Avalonia.Controls.Button? _fcUseButton;
    private TextBlock? _fcConnectHint;

    // Selection state — single SelectedEntry shared across both lists so
    // the bottom CTA always knows what to apply.
    private FreeConfigEntry? _fcSelectedEntry;

    // Live results bound to the Search-tab list.
    private readonly ObservableCollection<FreeConfigEntry> _fcSearchResults = new();
    private readonly ObservableCollection<FreeConfigEntry> _fcSavedResults = new();

    // Current target / max-ping snapshot — captured at Find click so the
    // status messages can quote what the user actually asked for.
    private int _fcTargetSnapshot = 10;

    /// <summary>
    /// AND-ADV-TOOLS-PUBLIC (2026-05-10) — Phase E rename. Body content
    /// for the Public tab inside the Advanced shell. Mirrors desktop
    /// FreeConfigsPage.axaml: sub-tab strip (Search | Saved) + scrollable
    /// bodies + bottom action bar. The shell provides the title bar /
    /// close button / outer chrome.
    ///
    /// <para>Renamed from <c>BuildFreeConfigsTabContent</c> to align with
    /// the desktop "Public" tab label. The legacy name is kept as a thin
    /// alias below so the AdvancedShell dispatch keeps working until
    /// Phase A retires the wrapper alongside the chrome rebuild.</para>
    /// </summary>
    private Control BuildPublicTabContent()
    {
        var bg        = GetBrush("SurfaceAppBrush");
        var card      = GetBrush("SurfaceBaseBrush");
        var sunken    = GetBrush("SurfaceSunkenBrush");
        var subtle    = GetBrush("BorderSubtleBrush");
        var defaultB  = GetBrush("BorderDefaultBrush");
        var textP     = GetBrush("TextPrimaryBrush");
        var textS     = GetBrush("TextSecondaryBrush");
        var textM     = GetBrush("TextMutedBrush");
        var successBg = GetBrush("SuccessBgBrush");
        var successFg = GetBrush("SuccessFgBrush");
        var successSolid = GetBrush("SuccessSolidBrush");
        var dangerSolid  = GetBrush("DangerSolidBrush");
        var accentSolid  = GetBrush("AccentSolidBrush");
        var accentOnSolid = GetBrush("AccentOnSolidBrush");
        var radiusXs = GetRadius("RadiusXs");
        var radiusSm = GetRadius("RadiusSm");

        // ── Sub-tab strip (Search | Saved) ─────────────────────────────────
        // POL-1: matches desktop FreeConfigsPage.axaml lines 22-37
        // (`ListBox Padding="6,2" + ListBoxItem Padding="10,4" FontSize="11"`).
        // Pre-POL-1 used MakeFcTabButton (Padding="0,6" FontSize=12 RadiusXs)
        // which inflated the chips relative to desktop.
        _fcTabSearch = MakeAdvancedSubTabButton(Localization.FcTabSearch,
                                                _fcSelectedTab == 0,
                                                (_, _) => SelectFreeConfigsTab(0));
        _fcTabSaved = MakeAdvancedSubTabButton(SavedTabHeaderText(),
                                               _fcSelectedTab == 1,
                                               (_, _) => SelectFreeConfigsTab(1));

        var tabRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(6, 4, 6, 4),
            Children = { _fcTabSearch, _fcTabSaved },
        };

        // ── Search tab body ────────────────────────────────────────────────
        // 1) Green action card (Find / Stop + advanced settings expander)
        var searchHint = new TextBlock
        {
            Text = Localization.FcSearchHint,
            FontSize = 10,
            Foreground = successFg,
            Opacity = 0.85,
            TextWrapping = TextWrapping.Wrap,
        };

        _fcFindButton = new Avalonia.Controls.Button
        {
            Content = Localization.FcFindButton,
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            Padding = new Thickness(0, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = successSolid,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(radiusSm),
        };
        _fcFindButton.Click += OnFreeConfigsFindClicked;

        _fcStopButton = new Avalonia.Controls.Button
        {
            Content = Localization.FcStopButton,
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            Padding = new Thickness(0, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = dangerSolid,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(radiusSm),
            IsVisible = false,
        };
        _fcStopButton.Click += OnFreeConfigsStopClicked;

        // POL-1: BorderBrush uses SuccessBorderBrush to match desktop
        // FreeConfigsPage.axaml line 98 (Expander
        // `BorderBrush="{DynamicResource SuccessBorderBrush}"`).
        // Pre-POL-1 used neutral BorderSubtleBrush which broke the green
        // card chrome.
        _fcAdvancedToggle = new Avalonia.Controls.Button
        {
            Content = Localization.FcAdvancedSettings,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(0, 6),
            Background = Brushes.Transparent,
            Foreground = successFg,
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = GetBrush("SuccessBorderBrush"),
            CornerRadius = new CornerRadius(0),
        };
        _fcAdvancedToggle.Click += OnFreeConfigsAdvancedToggle;

        _fcAdvancedPanel = BuildAdvancedSettingsPanel(successFg);
        _fcAdvancedPanel.IsVisible = _fcAdvancedExpanded;

        var greenCardStack = new StackPanel
        {
            Spacing = 10,
            Children = { searchHint, _fcFindButton, _fcStopButton, _fcAdvancedToggle, _fcAdvancedPanel }
        };

        var greenCard = new Border
        {
            Padding = new Thickness(14, 12),
            CornerRadius = new CornerRadius(radiusSm),
            Background = successBg,
            BorderBrush = successSolid,
            BorderThickness = new Thickness(1),
            Child = greenCardStack,
        };

        // 2) List header (Country | Endpoint | Latency | Transport)
        var headerRow = BuildFcListHeader(textM, sunken);

        // 3) Search list + empty hint
        // DEFCT-7.4-A: AlwaysSelected so that tapping the currently-selected
        // row on Android touch is a no-op (Avalonia's default Single mode
        // deselects on re-tap, which dropped the Connect CTA into a greyed
        // state with _fcSelectedEntry == null). At least one row must stay
        // selected once results exist — the bottom CTA depends on it.
        _fcSearchList = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            SelectionMode = SelectionMode.AlwaysSelected,
            ItemsSource = _fcSearchResults,
            ItemTemplate = new FuncDataTemplate<FreeConfigEntry>(
                (entry, _) => BuildFcRow(entry, isSavedTab: false),
                supportsRecycling: true),
        };
        _fcSearchList.SelectionChanged += OnFreeConfigsSelectionChanged;

        _fcSearchEmptyHint = new TextBlock
        {
            Text = Localization.FcSearchListEmptyHint,
            FontSize = 10,
            Opacity = 0.55,
            Foreground = textM,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 320,
            Margin = new Thickness(20),
        };

        var searchListBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radiusSm),
            BorderBrush = subtle,
            Background = card,
            ClipToBounds = true,
            Child = new Grid
            {
                Children = { _fcSearchList, _fcSearchEmptyHint }
            }
        };

        var searchGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            Margin = new Thickness(10, 8, 10, 4),
            RowSpacing = 6,
        };
        Grid.SetRow(greenCard, 0);
        Grid.SetRow(headerRow, 1);
        Grid.SetRow(searchListBorder, 2);
        searchGrid.Children.Add(greenCard);
        searchGrid.Children.Add(headerRow);
        searchGrid.Children.Add(searchListBorder);
        _fcSearchBody = searchGrid;

        // ── Saved tab body ─────────────────────────────────────────────────
        _fcSavedHint = new TextBlock
        {
            Text = Localization.FcSavedEmptyHint,
            FontSize = 9,
            Opacity = 0.55,
            Foreground = textM,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _fcClearAllButton = new Avalonia.Controls.Button
        {
            Content = Localization.FcSavedClearAll,
            FontSize = 10,
            Padding = new Thickness(10, 4),
            CornerRadius = new CornerRadius(radiusSm),
            Background = Brushes.Transparent,
            BorderBrush = defaultB,
            BorderThickness = new Thickness(1),
            Foreground = dangerSolid,
            IsVisible = false,
        };
        _fcClearAllButton.Click += OnFreeConfigsClearAllClicked;

        var savedToolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 6,
            Margin = new Thickness(2, 0, 2, 8),
        };
        Grid.SetColumn(_fcSavedHint, 0);
        Grid.SetColumn(_fcClearAllButton, 1);
        savedToolbar.Children.Add(_fcSavedHint);
        savedToolbar.Children.Add(_fcClearAllButton);

        var savedHeaderRow = BuildFcListHeader(textM, sunken);

        _fcSavedList = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ItemsSource = _fcSavedResults,
            ItemTemplate = new FuncDataTemplate<FreeConfigEntry>(
                (entry, _) => BuildFcRow(entry, isSavedTab: true),
                supportsRecycling: true),
        };
        _fcSavedList.SelectionChanged += OnFreeConfigsSelectionChanged;

        _fcSavedEmptyHint = new TextBlock
        {
            Text = Localization.FcSavedEmptyHint,
            FontSize = 10,
            Opacity = 0.55,
            Foreground = textM,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 320,
            Margin = new Thickness(20),
        };

        var savedListBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radiusSm),
            BorderBrush = subtle,
            Background = card,
            ClipToBounds = true,
            Child = new Grid
            {
                Children = { _fcSavedList, _fcSavedEmptyHint }
            }
        };

        var savedGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            Margin = new Thickness(10, 8, 10, 4),
            RowSpacing = 0,
        };
        Grid.SetRow(savedToolbar, 0);
        Grid.SetRow(savedHeaderRow, 1);
        Grid.SetRow(savedListBorder, 2);
        savedGrid.Children.Add(savedToolbar);
        savedGrid.Children.Add(savedHeaderRow);
        savedGrid.Children.Add(savedListBorder);
        savedGrid.IsVisible = false;
        _fcSavedBody = savedGrid;

        var bodyArea = new Grid
        {
            Children = { _fcSearchBody, _fcSavedBody }
        };

        // ── Bottom action bar (status + progress + Use CTA) ────────────────
        _fcStatusText = new TextBlock
        {
            Text = Localization.FcStatusEmpty,
            FontSize = 10,
            Opacity = 0.7,
            Foreground = textS,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _fcProgressLabel = new TextBlock
        {
            FontSize = 10,
            Opacity = 0.7,
            Foreground = textM,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };
        var statusRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };
        Grid.SetColumn(_fcStatusText, 0);
        Grid.SetColumn(_fcProgressLabel, 1);
        statusRow.Children.Add(_fcStatusText);
        statusRow.Children.Add(_fcProgressLabel);

        _fcProgress = new Avalonia.Controls.ProgressBar
        {
            Height = 4,
            CornerRadius = new CornerRadius(radiusXs),
            Minimum = 0,
            Maximum = 1,
            Value = 0,
            IsVisible = false,
        };

        _fcConnectHint = new TextBlock
        {
            Text = Localization.FcConnectHint,
            FontSize = 10,
            Opacity = 0.55,
            Foreground = textM,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };

        _fcUseButton = new Avalonia.Controls.Button
        {
            Content = Localization.FcUseSelected,
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            Padding = new Thickness(0, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = accentSolid,
            Foreground = accentOnSolid,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(radiusSm),
            IsEnabled = false,
        };
        _fcUseButton.Click += OnFreeConfigsUseClicked;

        var bottomStack = new StackPanel
        {
            Spacing = 6,
            Children = { statusRow, _fcProgress, _fcConnectHint, _fcUseButton }
        };

        var bottomBar = new Border
        {
            Padding = new Thickness(10, 6),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = defaultB,
            Background = bg,
            Child = bottomStack,
        };

        // ── Compose: tab strip + body + bottom bar ────────────────────────
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(tabRow, Dock.Top);
        DockPanel.SetDock(bottomBar, Dock.Bottom);
        dock.Children.Add(tabRow);
        dock.Children.Add(bottomBar);
        dock.Children.Add(bodyArea);

        return new Border
        {
            Background = bg,
            Child = dock,
        };
    }

    /// <summary>
    /// AND-ADV-TOOLS-PUBLIC (2026-05-10) — backwards-compat alias for the
    /// AdvancedShell dispatch. Phase A's chrome rebuild will switch the
    /// dispatch to call <see cref="BuildPublicTabContent"/> directly and
    /// retire this wrapper. Until then, both names resolve to the same
    /// widget tree.
    /// </summary>
    private Control BuildFreeConfigsTabContent() => BuildPublicTabContent();

    private Avalonia.Controls.Button MakeFcTabButton(string label, bool active)
    {
        return new Avalonia.Controls.Button
        {
            Content = label,
            FontSize = 12,
            FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 6),
            Background = active ? GetBrush("AccentBgSubtleBrush") : GetBrush("SurfaceSunkenBrush"),
            Foreground = active ? GetBrush("AccentFgBrush") : GetBrush("TextSecondaryBrush"),
            BorderBrush = active ? GetBrush("BorderAccentBrush") : GetBrush("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
        };
    }

    private Border BuildAdvancedSettingsPanel(IBrush successFg)
    {
        var targetLabel = new TextBlock
        {
            Text = Localization.FcTargetNLabel,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = successFg,
        };
        _fcTargetInput = new NumericUpDown
        {
            Value = 10,
            Minimum = 1,
            Maximum = 50,
            Increment = 1,
            FormatString = "0",
            MinWidth = 84,
            Padding = new Thickness(4, 2),
            FontSize = 11,
        };
        var configsWord = new TextBlock
        {
            Text = Localization.FcConfigsWord,
            FontSize = 11,
            Opacity = 0.75,
            Foreground = successFg,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var targetRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { targetLabel, _fcTargetInput, configsWord }
        };

        var pingLabel = new TextBlock
        {
            Text = Localization.FcWithPingUnder,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = successFg,
        };
        _fcMaxPingInput = new NumericUpDown
        {
            Value = 400,
            Minimum = 50,
            Maximum = 2000,
            Increment = 50,
            FormatString = "0",
            MinWidth = 92,
            Padding = new Thickness(4, 2),
            FontSize = 11,
        };
        var msUnit = new TextBlock
        {
            Text = Localization.FcMsUnit,
            FontSize = 11,
            Opacity = 0.75,
            Foreground = successFg,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var pingRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
            Children = { pingLabel, _fcMaxPingInput, msUnit }
        };

        _fcExcludeRu = new Avalonia.Controls.CheckBox
        {
            Content = new TextBlock
            {
                Text = Localization.FcExcludeRu,
                FontSize = 11,
                Foreground = successFg,
                TextWrapping = TextWrapping.Wrap,
            },
            IsChecked = true,
            MinHeight = 0,
            Padding = new Thickness(4, 0),
            Margin = new Thickness(0, 6, 0, 0),
        };

        var stack = new StackPanel
        {
            Spacing = 0,
            Margin = new Thickness(0, 4, 0, 0),
            Children = { targetRow, pingRow, _fcExcludeRu }
        };

        return new Border
        {
            Padding = new Thickness(0, 4),
            Child = stack,
        };
    }

    private Grid BuildFcListHeader(IBrush textM, IBrush sunken)
    {
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("44,*,72,68"),
            Height = 22,
            Background = sunken,
        };
        var col0 = new TextBlock
        {
            Text = Localization.FcColCountry,
            FontWeight = FontWeight.SemiBold,
            FontSize = 9,
            Opacity = 0.55,
            Foreground = textM,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var col1 = new TextBlock
        {
            Text = Localization.FcColEndpoint,
            FontWeight = FontWeight.SemiBold,
            FontSize = 9,
            Opacity = 0.55,
            Foreground = textM,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        var col2 = new TextBlock
        {
            Text = Localization.FcColLatency,
            FontWeight = FontWeight.SemiBold,
            FontSize = 9,
            Opacity = 0.55,
            Foreground = textM,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 4, 0),
        };
        var col3 = new TextBlock
        {
            Text = Localization.FcColTransport,
            FontWeight = FontWeight.SemiBold,
            FontSize = 9,
            Opacity = 0.55,
            Foreground = textM,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Grid.SetColumn(col0, 0);
        Grid.SetColumn(col1, 1);
        Grid.SetColumn(col2, 2);
        Grid.SetColumn(col3, 3);
        header.Children.Add(col0);
        header.Children.Add(col1);
        header.Children.Add(col2);
        header.Children.Add(col3);
        return header;
    }

    /// <summary>
    /// Per-row template — mirrors desktop's FreeConfigItemViewModel
    /// rendering: country flag emoji + endpoint + colored latency badge +
    /// transport label. Saved-tab variant adds a trailing ✕ remove button.
    /// </summary>
    private Control BuildFcRow(FreeConfigEntry entry, bool isSavedTab)
    {
        var textP = GetBrush("TextPrimaryBrush");
        var textM = GetBrush("TextMutedBrush");
        var dangerSolid = GetBrush("DangerSolidBrush");

        // Country column — "🇷🇺 RU" or "—" (mirrors FreeConfigItemViewModel.CountryDisplay).
        var country = new TextBlock
        {
            Text = string.IsNullOrEmpty(entry.CountryCode)
                ? "—"
                : $"{FlagFor(entry.CountryCode)} {entry.CountryCode}",
            FontSize = 10,
            Foreground = textP,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var endpoint = new TextBlock
        {
            Text = $"{entry.Host}:{entry.Port}",
            FontSize = 10,
            Foreground = textP,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // Latency badge — mirrors desktop's color + label logic. We
        // reproduce the switch inline (no FreeConfigItemViewModel
        // dependency on Android — the App-layer VM pulls SkiaSharp etc.).
        var latencyText = entry.Status switch
        {
            FreeConfigStatus.Verified when entry.LatencyMs <= 0 => "— ✓✓",
            FreeConfigStatus.Verified                            => $"{entry.LatencyMs} ms ✓✓",
            FreeConfigStatus.Ok       when entry.LatencyMs <= 0 => "— ✓",
            FreeConfigStatus.Ok                                  => $"{entry.LatencyMs} ms ✓",
            FreeConfigStatus.Slow                                => $"{entry.LatencyMs} ms slow",
            FreeConfigStatus.Implausible                         => "fake (<5ms)",
            FreeConfigStatus.TlsFailed                           => "TLS failed",
            FreeConfigStatus.Timeout                             => "timeout",
            FreeConfigStatus.Unreachable                         => "unreachable",
            FreeConfigStatus.ParseError                          => "parse error",
            _                                                     => "—",
        };
        var latencyHex = entry.Status switch
        {
            FreeConfigStatus.Verified                          => "#059669",
            FreeConfigStatus.Ok when entry.LatencyMs < 100    => "#22C55E",
            FreeConfigStatus.Ok when entry.LatencyMs < 300    => "#65A30D",
            FreeConfigStatus.Ok                                => "#F59E0B",
            FreeConfigStatus.Slow                              => "#EF4444",
            FreeConfigStatus.Implausible                       => "#DC2626",
            FreeConfigStatus.TlsFailed                         => "#F97316",
            _                                                   => "#9CA3AF",
        };
        IBrush latencyBg = TryParseBrush(latencyHex) ?? new SolidColorBrush(Color.Parse("#9CA3AF"));
        var latencyBadge = new Border
        {
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            Padding = new Thickness(4, 1),
            Background = latencyBg,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = latencyText,
                Foreground = Brushes.White,
                FontSize = 9,
                FontWeight = FontWeight.SemiBold,
            }
        };

        var transport = new TextBlock
        {
            Text = entry.Transport ?? "tcp",
            FontSize = 9,
            Opacity = 0.6,
            Foreground = textM,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        // POL-1: row Height matches desktop FreeConfigsPage.axaml row
        // template `Height="22"` (lines 215, 352). Pre-POL-1 used 26 dp
        // which made each Public-tab row ~18% taller than desktop.
        var grid = new Grid
        {
            ColumnDefinitions = isSavedTab
                ? new ColumnDefinitions("44,*,72,68,32")
                : new ColumnDefinitions("44,*,72,68"),
            Height = 22,
        };
        Grid.SetColumn(country, 0);
        Grid.SetColumn(endpoint, 1);
        Grid.SetColumn(latencyBadge, 2);
        Grid.SetColumn(transport, 3);
        grid.Children.Add(country);
        grid.Children.Add(endpoint);
        grid.Children.Add(latencyBadge);
        grid.Children.Add(transport);

        if (isSavedTab)
        {
            var removeBtn = new Avalonia.Controls.Button
            {
                Content = Localization.FcSavedRemoveOne,
                FontSize = 10,
                Padding = new Thickness(4, 1),
                MinWidth = 22,
                MinHeight = 0,
                CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
                Background = Brushes.Transparent,
                Foreground = dangerSolid,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            removeBtn.Click += (_, _) =>
            {
                _fcOrchestrator?.RemoveSaved(entry);
                ReloadFreeConfigsLists();
            };
            Grid.SetColumn(removeBtn, 4);
            grid.Children.Add(removeBtn);
        }

        return grid;
    }

    private static IBrush? TryParseBrush(string hex)
    {
        try { return new SolidColorBrush(Color.Parse(hex)); }
        catch { return null; }
    }

    /// <summary>
    /// Convert ISO-2 country code to a regional indicator emoji.
    /// Inlined here to avoid the App-layer VM dependency.
    /// </summary>
    private static string FlagFor(string? cc)
    {
        if (string.IsNullOrEmpty(cc) || cc.Length != 2) return "🌐";
        var upper = cc.ToUpperInvariant();
        var c0 = 0x1F1E6 + (upper[0] - 'A');
        var c1 = 0x1F1E6 + (upper[1] - 'A');
        return char.ConvertFromUtf32(c0) + char.ConvertFromUtf32(c1);
    }

    // ── Show / hide tab ──────────────────────────────────────────────────
    //
    // AND-MIGRATE-OVERLAYS (2026-05-09): the standalone overlay open/close
    // pair is gone. The Advanced shell calls ReseedFreeConfigsTabState on
    // tab activation and StopFreeConfigsBackgroundWork when leaving.

    private async void ReseedFreeConfigsTabState()
    {
        if (_fcOrchestrator is null)
        {
            var logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .CreateLogger();
            _fcOrchestrator = new AndroidFreeConfigsOrchestrator(logger);
            _fcOrchestrator.OnStatus    += OnFcStatus;
            _fcOrchestrator.OnProgress  += OnFcProgress;
            _fcOrchestrator.OnFound     += OnFcFound;
            _fcOrchestrator.OnFinished  += OnFcFinished;
            _fcOrchestrator.OnFailed    += OnFcFailed;
        }

        await _fcOrchestrator.EnsureCacheLoadedAsync();
        ReloadFreeConfigsLists();

        // AND-ADV-TOOLS-PUBLIC (2026-05-10) — Phase E persistence. Restore
        // the last-active sub-tab via AndroidStorage. Pre-Phase-E this
        // was implicit ("default to Saved if non-empty"); the explicit
        // KeyPublicActiveSubTab survives across overlay opens so a user
        // who prefers Search keeps Search even after the Saved list grows
        // populated. Fallback: if the persisted sub-tab is Saved but the
        // saved list is empty (first launch / cleared cache), drop to
        // Search so we don't show a blank pane.
        var persistedSaved = AndroidStorage.GetPublicActiveSubTabIsSaved();
        if (persistedSaved && _fcSavedResults.Count > 0)
            SelectFreeConfigsTab(1);
        else
            SelectFreeConfigsTab(0);
    }

    /// <summary>
    /// Cancel any in-flight find when the Advanced shell closes or
    /// switches off the Public Configs tab — we don't want to keep
    /// burning battery on TCP probes the user can no longer see.
    /// </summary>
    private void StopFreeConfigsBackgroundWork()
    {
        _fcOrchestrator?.Cancel();
    }

    private void SelectFreeConfigsTab(int index)
    {
        _fcSelectedTab = index;
        // AND-ADV-TOOLS-PUBLIC (2026-05-10) — persist the user's pick.
        // Stored as bool: false = Search, true = Saved.
        AndroidStorage.SetPublicActiveSubTabIsSaved(index == 1);
        if (_fcSearchBody is not null) _fcSearchBody.IsVisible = index == 0;
        if (_fcSavedBody is not null)  _fcSavedBody.IsVisible  = index == 1;
        // POL-1: re-painted via StyleAdvShellTab (matches sibling sub-tab
        // strips). Pre-POL-1 used StyleSegmentButton — kebab pill shape.
        if (_fcTabSearch is not null)
        {
            StyleAdvShellTab(_fcTabSearch, index == 0);
            _fcTabSearch.Content = Localization.FcTabSearch;
        }
        if (_fcTabSaved is not null)
        {
            StyleAdvShellTab(_fcTabSaved, index == 1);
            _fcTabSaved.Content = SavedTabHeaderText();
        }

        // Selection is per-tab — clear when tab changes so the bottom CTA
        // doesn't try to use a stale entry from the other tab.
        _fcSelectedEntry = null;
        if (_fcUseButton is not null) _fcUseButton.IsEnabled = false;
    }

    private string SavedTabHeaderText()
    {
        var n = _fcSavedResults.Count;
        if (n <= 0) return Localization.FcTabSaved;
        return Localization.FcTabSavedWithCount(n);
    }

    // ── Find / Stop ───────────────────────────────────────────────────────

    private async void OnFreeConfigsFindClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_fcOrchestrator is null) return;
        if (_fcOrchestrator.IsBusy) return;

        var target = (int)(_fcTargetInput?.Value ?? 10m);
        if (target < 1) target = 1;
        if (target > 50) target = 50;
        _fcTargetSnapshot = target;

        var maxPing = (int)(_fcMaxPingInput?.Value ?? 400m);
        if (maxPing < 50) maxPing = 50;
        if (maxPing > 2000) maxPing = 2000;

        var excludeRu = _fcExcludeRu?.IsChecked == true;

        // Empty the live results list so we can show the new run's
        // findings as they trickle in.
        _fcSearchResults.Clear();
        if (_fcUseButton is not null) _fcUseButton.IsEnabled = false;
        _fcSelectedEntry = null;
        SetFcBusy(true);

        try
        {
            await _fcOrchestrator.FindAsync(target, maxPing, excludeRu);
        }
        finally
        {
            SetFcBusy(false);
            ReloadFreeConfigsLists();
        }
    }

    private void OnFreeConfigsStopClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _fcOrchestrator?.Cancel();
    }

    private void SetFcBusy(bool busy)
    {
        if (_fcFindButton is not null) _fcFindButton.IsVisible = !busy;
        if (_fcStopButton is not null) _fcStopButton.IsVisible = busy;
        if (_fcProgress is not null) _fcProgress.IsVisible = busy;
        if (_fcProgressLabel is not null) _fcProgressLabel.IsVisible = busy;
    }

    private void OnFreeConfigsAdvancedToggle(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _fcAdvancedExpanded = !_fcAdvancedExpanded;
        if (_fcAdvancedPanel is not null) _fcAdvancedPanel.IsVisible = _fcAdvancedExpanded;
    }

    // ── Selection + Use ───────────────────────────────────────────────────

    private void OnFreeConfigsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Remember which list the selection came from so the other one's
        // SelectedItem can be cleared (single-CTA semantics).
        if (sender is ListBox box && box.SelectedItem is FreeConfigEntry entry)
        {
            _fcSelectedEntry = entry;
            if (_fcUseButton is not null) _fcUseButton.IsEnabled = true;

            if (ReferenceEquals(box, _fcSearchList) && _fcSavedList is not null)
                _fcSavedList.SelectedItem = null;
            else if (ReferenceEquals(box, _fcSavedList) && _fcSearchList is not null)
                _fcSearchList.SelectedItem = null;
        }
    }

    private void OnFreeConfigsUseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var entry = _fcSelectedEntry;
        if (entry is null) return;
        if (string.IsNullOrEmpty(entry.RawUri)) return;

        // Persist the chosen URI as a manual VLESS config — clears any
        // subscription-mode state so MainActivity.StartTunnelService picks
        // up the new URI via AndroidStorage.GetActiveServer.
        AndroidStorage.SetVlessUri(entry.RawUri);
        AndroidStorage.SetSubscriptionUrl(null);
        AndroidStorage.SetServers(null);
        AndroidStorage.SetSelectedServerName(null);

        ShowMenuFeedback(Localization.FcUsedToast);

        // AND-MIGRATE-OVERLAYS (2026-05-09): close the Advanced shell so
        // the consent dialog (first-launch only) and the Simple-page
        // status card become visible — the user just chose a public
        // config, the next visible step is "Connect".
        CloseAdvancedShell();

        // Refresh the config-row summary on the main page.
        UpdateConfigSummary();

        // Defer the connect call one beat so the close animation gets a
        // render frame before the system VPN consent dialog (if any)
        // pops in. Pure UX nicety; no behavioural effect.
        Dispatcher.UIThread.Post(() =>
        {
            MainActivity.Instance?.RequestConnect();
        }, DispatcherPriority.Background);
    }

    // ── Clear all (Saved tab) ─────────────────────────────────────────────

    private void OnFreeConfigsClearAllClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _fcOrchestrator?.ClearSaved();
        ReloadFreeConfigsLists();
    }

    // ── Live update from orchestrator ─────────────────────────────────────

    private void OnFcStatus(string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_fcStatusText is not null) _fcStatusText.Text = text;
        });
    }

    private void OnFcProgress(int done, int total)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_fcProgress is not null)
            {
                _fcProgress.Maximum = Math.Max(1, total);
                _fcProgress.Value = Math.Clamp(done, 0, total);
            }
            if (_fcProgressLabel is not null)
            {
                _fcProgressLabel.Text = $"{done}/{total}";
            }
        });
    }

    private void OnFcFound(FreeConfigEntry entry)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // De-dup by Id in case two batches happen to land the same row.
            for (int i = 0; i < _fcSearchResults.Count; i++)
            {
                if (string.Equals(_fcSearchResults[i].Id, entry.Id, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            _fcSearchResults.Add(entry);
            // Auto-select first row so user can connect immediately if
            // they're in a hurry.
            if (_fcSelectedEntry is null && _fcSearchList is not null)
            {
                _fcSearchList.SelectedItem = entry;
            }
        });
    }

    private void OnFcFinished(int verifiedCount)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ReloadFreeConfigsLists();
        });
    }

    private void OnFcFailed(string error)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_fcStatusText is not null)
                _fcStatusText.Text = Localization.FcStatusFailed(error);
        });
    }

    /// <summary>
    /// Repopulate both lists from the orchestrator's current state. Fired
    /// after the overlay opens, after a Find run finishes, and after a
    /// Saved-tab mutation (Remove / ClearAll).
    /// </summary>
    private void ReloadFreeConfigsLists()
    {
        if (_fcOrchestrator is null) return;
        var saved = _fcOrchestrator.Saved;

        _fcSavedResults.Clear();
        // Sort: tested ones first (lower latency wins), unknown last.
        var ordered = saved
            .OrderBy(c => c.Status switch
            {
                FreeConfigStatus.Verified => 0,
                FreeConfigStatus.Ok       => 1,
                FreeConfigStatus.Slow     => 2,
                _                          => 9,
            })
            .ThenBy(c => c.LatencyMs > 0 ? c.LatencyMs : int.MaxValue)
            .ToList();
        foreach (var c in ordered)
            _fcSavedResults.Add(c);

        if (_fcSavedEmptyHint is not null)
            _fcSavedEmptyHint.IsVisible = _fcSavedResults.Count == 0;
        if (_fcSavedList is not null)
            _fcSavedList.IsVisible = _fcSavedResults.Count > 0;
        if (_fcClearAllButton is not null)
            _fcClearAllButton.IsVisible = _fcSavedResults.Count > 0;

        if (_fcSearchEmptyHint is not null)
            _fcSearchEmptyHint.IsVisible = _fcSearchResults.Count == 0;
        if (_fcSearchList is not null)
            _fcSearchList.IsVisible = _fcSearchResults.Count > 0;

        // Saved tab counter on the tab strip.
        if (_fcTabSaved is not null && _fcSelectedTab != 1)
        {
            _fcTabSaved.Content = SavedTabHeaderText();
        }
        else if (_fcTabSaved is not null)
        {
            _fcTabSaved.Content = SavedTabHeaderText();
        }
    }
}
