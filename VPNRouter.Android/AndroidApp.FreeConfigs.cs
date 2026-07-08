using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
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
    // DEFCT-7.3-A — replaced Avalonia NumericUpDown (default ▲▼ RepeatButtons
    // were ~22 dp tall, well below Android 48 dp touch-target spec — most taps
    // missed and fell through to the underlying Find CTA). Now a hand-rolled
    // [−][value][+] row with each step Button at 44 dp + an int-field source
    // of truth so the readers in OnFreeConfigsFindClicked don't have to know
    // about the spinner template.
    private int _fcTargetValue = 10;
    private int _fcMaxPingValue = 400;
    private TextBlock? _fcTargetValueText;
    private TextBlock? _fcMaxPingValueText;
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
        // DEFCT-7.5-A (2026-05-10) — On Avalonia 11.3 / Android, Button.Click
        // on this specific button doesn't fire on a tap (verified live: row
        // auto-selects, Connect styles blue + IsEnabled=true, but tap does
        // not invoke OnFreeConfigsUseClicked — no toast, no shell-close, no
        // SetVlessUri side effect). The button sits inside a `bottomBar`
        // Border docked to Bottom of an inner DockPanel; the Border or one
        // of the ancestor handlers in the Advanced shell appears to swallow
        // the standard Click route. Tapped routes through Avalonia's gesture
        // recognizer pipeline rather than the Button's pointer-capture path
        // and reaches us reliably under the same conditions. We subscribe
        // both events; OnFreeConfigsUseClicked is debounced (200 ms window)
        // so the rare case where both fire from a single tap on a working
        // surface only does the work once. handledEventsToo:true so any
        // ancestor that marks the event Handled cannot suppress us.
        // Wave 23 (2026-05-18) — Avalonia 12 made Gestures internal; same
        // routed event reachable through InputElement (publicly re-exposed).
        _fcUseButton.AddHandler(InputElement.TappedEvent, OnFreeConfigsUseClicked,
            RoutingStrategies.Bubble, handledEventsToo: true);

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
        var targetSpinner = BuildSpinnerControl(
            initial: _fcTargetValue, min: 1, max: 50, step: 1, successFg,
            display: out _fcTargetValueText,
            onChanged: v => _fcTargetValue = v);
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
            Children = { targetLabel, targetSpinner, configsWord }
        };

        var pingLabel = new TextBlock
        {
            Text = Localization.FcWithPingUnder,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = successFg,
        };
        var pingSpinner = BuildSpinnerControl(
            initial: _fcMaxPingValue, min: 50, max: 2000, step: 50, successFg,
            display: out _fcMaxPingValueText,
            onChanged: v => _fcMaxPingValue = v);
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
            Children = { pingLabel, pingSpinner, msUnit }
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
    /// DEFCT-7.3-A — replacement for NumericUpDown that meets Android's
    /// 48 dp touch-target spec. Renders as [ − ][ value ][ + ] in a
    /// horizontal StackPanel where each step button is 44×44 dp (close
    /// enough to spec; the surrounding panel adds spacing). Min/max
    /// clamp inside the click handlers so callers only see valid values.
    /// </summary>
    /// <param name="display">
    /// Output param — the value-display TextBlock so the caller can keep
    /// a reference for later updates (eg. after Find run resets the value).
    /// </param>
    /// <param name="onChanged">
    /// Invoked with the post-step clamped int after each − / + tap.
    /// Caller owns the field that stores the value.
    /// </param>
    private StackPanel BuildSpinnerControl(
        int initial, int min, int max, int step, IBrush successFg,
        out TextBlock display,
        System.Action<int> onChanged)
    {
        var current = initial;
        display = new TextBlock
        {
            Text = current.ToString(System.Globalization.CultureInfo.InvariantCulture),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            MinWidth = 48,
            Foreground = successFg,
        };
        var displayLocal = display; // capture for the closures below
        var minus = MakeSpinnerStepButton("−");
        var plus  = MakeSpinnerStepButton("+");
        minus.Click += (_, _) =>
        {
            current = System.Math.Max(min, current - step);
            displayLocal.Text = current.ToString(System.Globalization.CultureInfo.InvariantCulture);
            onChanged(current);
        };
        plus.Click += (_, _) =>
        {
            current = System.Math.Min(max, current + step);
            displayLocal.Text = current.ToString(System.Globalization.CultureInfo.InvariantCulture);
            onChanged(current);
        };
        return new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { minus, display, plus },
        };
    }

    private Avalonia.Controls.Button MakeSpinnerStepButton(string glyph)
    {
        var btn = new Avalonia.Controls.Button
        {
            Content = glyph,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Width = 44,
            Height = 44,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
        };
        btn.BindToken(Avalonia.Controls.Button.BackgroundProperty, "SurfaceSunkenBrush");
        btn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "TextPrimaryBrush");
        return btn;
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
            _fcOrchestrator.OnStatus         += OnFcStatus;
            _fcOrchestrator.OnProgress       += OnFcProgress;
            _fcOrchestrator.OnFound          += OnFcFound;
            _fcOrchestrator.OnFinished       += OnFcFinished;
            _fcOrchestrator.OnFailed         += OnFcFailed;
            _fcOrchestrator.OnEntryUpgraded  += OnFcEntryUpgraded;
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

        // DEFCT-7.3-A — read from int-field source of truth instead of the
        // pre-fix NumericUpDown's Value (decimal?). Clamp guards stay since
        // the spinner buttons already clamp at min/max but defensive is cheap.
        var target = _fcTargetValue;
        if (target < 1) target = 1;
        if (target > 50) target = 50;
        _fcTargetSnapshot = target;

        var maxPing = _fcMaxPingValue;
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

    /// <summary>
    /// v2.39.0 (public-configs audit P1): the Connect CTA is enabled ONLY for a
    /// deep-verified (✓✓) public config. A TCP/TLS candidate (single ✓) can be
    /// selected to inspect it, but Connect stays disabled until deep verify
    /// confirms real connectivity. Saved-tab rows are persisted Verified-only so
    /// they pass; a legacy Ok row from an older cache stays gated until it is
    /// re-verified. Returns the resulting enabled state.
    /// </summary>
    private bool ApplyFcConnectGate()
    {
        var verified = _fcSelectedEntry is { } e &&
                       e.Status == FreeConfigStatus.Verified;
        if (_fcUseButton is not null) _fcUseButton.IsEnabled = verified;
        return verified;
    }

    private void OnFreeConfigsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Remember which list the selection came from so the other one's
        // SelectedItem can be cleared (single-CTA semantics).
        if (sender is ListBox box && box.SelectedItem is FreeConfigEntry entry)
        {
            _fcSelectedEntry = entry;
            // Gate Connect on deep-verify status (audit P1) instead of
            // enabling on any selection.
            ApplyFcConnectGate();

            if (ReferenceEquals(box, _fcSearchList) && _fcSavedList is not null)
                _fcSavedList.SelectedItem = null;
            else if (ReferenceEquals(box, _fcSavedList) && _fcSearchList is not null)
                _fcSearchList.SelectedItem = null;
        }
    }

    // DEFCT-7.5-A (2026-05-10) — last-fire timestamp for the dual Click +
    // Tapped subscription on _fcUseButton. A single tap can deliver both
    // events; the 200 ms window collapses them to one handler invocation.
    // Static so the timestamp survives across Public-tab rebuilds (it's
    // shared with the per-instance buttons that are rebuilt on tab switch).
    private static DateTime _fcUseClickedAt = DateTime.MinValue;

    private void OnFreeConfigsUseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var now = DateTime.UtcNow;
        if ((now - _fcUseClickedAt).TotalMilliseconds < 200) return;
        _fcUseClickedAt = now;

        var entry = _fcSelectedEntry;
        if (entry is null) return;
        // v2.40.0 (contracts B1 #5): don't adopt+connect while a search/recheck
        // owns the orchestrator — Apply stops+starts the VPN, racing the verifier
        // processes (libbox) + the TUN lifecycle. Wait until the search is idle.
        if (_fcOrchestrator?.IsBusy == true)
        {
            ShowMenuFeedback(Localization.FcConnectBusySearch);
            return;
        }
        // v2.39.0 (audit P1) backstop for the Verified-only Connect gate: never
        // connect to a public config that hasn't passed deep verify, even if a
        // UI path re-enabled the button. The button should already be disabled
        // for non-verified rows (ApplyFcConnectGate), this is defence-in-depth.
        if (entry.Status != FreeConfigStatus.Verified)
        {
            ShowMenuFeedback(Localization.FcConnectNeedsVerify);
            return;
        }
        if (string.IsNullOrEmpty(entry.RawUri)) return;

        // Bug-AND-021 (2026-05-17, user-reported "после подключения не
        // переносит в список серверов"): pre-fix this cleared the Servers
        // list entirely (SetServers(null)) and stashed the URI as a
        // manual VLESS config. Result: chosen free config wasn't visible
        // in Advanced > Servers, and any other servers the user had
        // collected got wiped.
        //
        // Now we mirror desktop MainWindowViewModel.ApplyFreeConfigAsync:
        //   - Parse URI into VlessServerEntry.
        //   - Check if same host:port:uuid already exists in Servers.
        //   - If new: add with unique display name (collision-safe).
        //   - Set as SelectedServerName so GetActiveServer picks it up.
        //   - Keep existing servers (no wipe).
        // Subscription gets cleared because free config = manual mode.
        try
        {
            var vless = VPNRouter.Core.Services.VlessUriParser.Parse(entry.RawUri);
            var servers = AndroidStorage.GetServers() ?? new System.Collections.Generic.List<VPNRouter.Core.Models.VlessServerEntry>();
            // Match by host:port:uuid (display name can vary). Reuse the
            // existing entry's name so the user's curated label sticks.
            var existing = servers.FirstOrDefault(s =>
                string.Equals(s.Server, vless.Server, System.StringComparison.OrdinalIgnoreCase) &&
                s.Port == vless.Port &&
                string.Equals(s.Uuid, vless.Uuid, System.StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                AndroidStorage.SetSelectedServerName(existing.Name);
            }
            else
            {
                // Unique-name pass — "⚡ free" + #2, #3, … if collisions.
                var baseName = string.IsNullOrWhiteSpace(vless.Name) ? "⚡ free" : vless.Name!;
                var displayName = baseName;
                int suffix = 2;
                while (servers.Any(s => string.Equals(s.Name, displayName, System.StringComparison.OrdinalIgnoreCase)))
                    displayName = $"{baseName} #{suffix++}";
                vless.Name = displayName;
                servers.Add(vless);
                AndroidStorage.SetServers(servers);
                AndroidStorage.SetSelectedServerName(displayName);
            }
        }
        catch (System.Exception ex)
        {
            // applications-page-audit P0: NEVER wipe the user's curated Servers
            // list on a parse/persist failure — that fallback was more
            // destructive than the failed operation itself. Leave ALL prior
            // settings untouched, surface an error, and abort the apply (a URI
            // that failed to parse must not be routed).
            global::Android.Util.Log.Warn("VpnRouter.FC",
                $"Apply public config failed — {ex.GetType().Name}: {ex.Message}; servers left unchanged");
            ShowMenuFeedback(Localization.FcApplyFailed);
            return;
        }
        // Manual mode stores the raw URI as legacy fallback. The new
        // active-server resolver (GetActiveServer) reads the Servers
        // list + SelectedServerName first; the URI is only used if both
        // are null (which we just made false above on the happy path).
        AndroidStorage.SetVlessUri(entry.RawUri);
        AndroidStorage.SetSubscriptionUrl(null);

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

    /// <summary>
    /// Bug&#x202F;#1 (2026-05-11): orchestrator's Deep Verify pass promoted
    /// an entry from <see cref="FreeConfigStatus.Ok"/> to
    /// <see cref="FreeConfigStatus.Verified"/>. We need to force the row
    /// to re-render — FreeConfigEntry doesn't implement
    /// INotifyPropertyChanged, so the ItemTemplate's Status snapshot is
    /// stale. The cheapest reliable refresh is replacing the entry at the
    /// same index, which raises ObservableCollection's CollectionChanged
    /// (NotifyCollectionChangedAction.Replace) and triggers a re-template.
    /// Also restores selection if this was the selected row (re-tap-free
    /// path, the user shouldn't have to re-pick the row they just chose).
    /// </summary>
    private void OnFcEntryUpgraded(FreeConfigEntry entry)
    {
        if (entry is null || string.IsNullOrEmpty(entry.Id)) return;
        Dispatcher.UIThread.Post(() =>
        {
            // Search tab — main source of truth during a Find run.
            for (int i = 0; i < _fcSearchResults.Count; i++)
            {
                if (string.Equals(_fcSearchResults[i].Id, entry.Id, StringComparison.OrdinalIgnoreCase))
                {
                    var wasSelected = ReferenceEquals(_fcSelectedEntry, _fcSearchResults[i]);
                    _fcSearchResults[i] = entry;
                    if (wasSelected)
                    {
                        _fcSelectedEntry = entry;
                        if (_fcSearchList is not null)
                            _fcSearchList.SelectedItem = entry;
                    }
                    break;
                }
            }
            // Saved tab — keep it consistent in case the upgrade arrived
            // while the user was browsing Saved between Find runs (rare,
            // but cheap to handle).
            for (int i = 0; i < _fcSavedResults.Count; i++)
            {
                if (string.Equals(_fcSavedResults[i].Id, entry.Id, StringComparison.OrdinalIgnoreCase))
                {
                    var wasSelected = ReferenceEquals(_fcSelectedEntry, _fcSavedResults[i]);
                    _fcSavedResults[i] = entry;
                    if (wasSelected)
                    {
                        _fcSelectedEntry = entry;
                        if (_fcSavedList is not null)
                            _fcSavedList.SelectedItem = entry;
                    }
                    break;
                }
            }

            // v2.39.0 (audit P1): a row just became connectable (✓✓). If the
            // user has no CONNECTABLE row selected yet — nothing selected, OR the
            // selection is still a non-verified candidate (OnFcFound auto-selects
            // the lowest-latency Ok row, and THAT one can fail deep verify while a
            // later one passes) — promote selection to this freshly verified row
            // so the Connect CTA lights up without a manual tap. We never steal a
            // selection that is already Verified.
            var selectedIsConnectable = _fcSelectedEntry is { } sel &&
                                        sel.Status == FreeConfigStatus.Verified;
            if (!selectedIsConnectable && _fcSearchList is not null &&
                _fcSearchResults.Contains(entry))
                _fcSearchList.SelectedItem = entry; // fires SelectionChanged -> gate
            else
                ApplyFcConnectGate();
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
        if (_fcSavedHint is not null)
            // DEFCT-7.6-A — toolbar-row hint mirrored the empty-state TextBlock so
            // the misleading "No saved configs yet" line stayed visible above a
            // populated list. Hide once results land; same predicate as the inner
            // hint keeps the two TextBlocks in lockstep.
            _fcSavedHint.IsVisible = _fcSavedResults.Count == 0;
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
