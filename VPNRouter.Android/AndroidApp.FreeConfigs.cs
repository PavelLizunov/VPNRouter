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
using UiIcons = VPNRouter.Core.Services.UiIcons;
using IconView = VPNRouter.UI.Controls.IconView;

namespace VPNRouter.Android;

public partial class AndroidApp
{
    private AndroidFreeConfigsOrchestrator? _fcOrchestrator;

    private int _fcSelectedTab;
    private Avalonia.Controls.Button? _fcTabSearch;
    private Avalonia.Controls.Button? _fcTabSaved;

    private Control? _fcSearchBody;
    private Control? _fcSavedBody;

    private Avalonia.Controls.Button? _fcFindButton;
    private Avalonia.Controls.Button? _fcStopButton;
    private int _fcTargetValue = 10;
    private int _fcMaxPingValue = 400;
    private TextBlock? _fcTargetValueText;
    private TextBlock? _fcMaxPingValueText;
    private Avalonia.Controls.CheckBox? _fcExcludeRu;
    private Border? _fcAdvancedPanel;
    private Avalonia.Controls.Button? _fcAdvancedToggle;
    private bool _fcAdvancedExpanded;
    private IconView? _fcAdvancedChevron;
    private TextBlock? _fcAdvancedToggleText;
    private ListBox? _fcSearchList;
    private TextBlock? _fcSearchEmptyHint;

    private ListBox? _fcSavedList;
    private TextBlock? _fcSavedEmptyHint;
    private Avalonia.Controls.Button? _fcClearAllButton;

    private TextBlock? _fcStatusText;
    private Avalonia.Controls.ProgressBar? _fcProgress;
    private TextBlock? _fcProgressLabel;
    private Avalonia.Controls.Button? _fcUseButton;
    private TextBlock? _fcConnectHint;

    private FreeConfigEntry? _fcSelectedEntry;

    private readonly ObservableCollection<FreeConfigEntry> _fcSearchResults = new();
    private readonly ObservableCollection<FreeConfigEntry> _fcSavedResults = new();

    private int _fcTargetSnapshot = 10;

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

        _fcTabSearch = MakeAdvancedSubTabButton(UiIcons.StripSymbols(Localization.FcTabSearch),
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

        var searchHint = new TextBlock
        {
            Text = Localization.FcSearchHint,
            FontSize = UiScale.Fs(10),
            Foreground = successFg,
            Opacity = 0.85,
            TextWrapping = TextWrapping.Wrap,
        };

        _fcFindButton = new Avalonia.Controls.Button
        {
            Content = IconText(UiIcons.SearchCheck, Localization.FcFindButton, 18),
            FontSize = UiScale.Fs(13),
            FontWeight = FontWeight.Bold,
            Padding = new Thickness(0, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            MinHeight = 48,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = GetBrush("AccentSolidBrush"),
            Foreground = GetBrush("AccentOnSolidBrush"),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(radiusSm),
        };
        _fcFindButton.Click += OnFreeConfigsFindClicked;

        _fcStopButton = new Avalonia.Controls.Button
        {
            Content = IconText(UiIcons.Square, Localization.FcStopButton, 16),
            FontSize = UiScale.Fs(13),
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

        // A labelled toggle like the config row of the main screen: "Settings" on the left, a "Show" or "Hide" pill with a
        // chevron that flips on the right, so the row reads as something that opens and closes.
        _fcAdvancedChevron = IconContent(UiIcons.ChevronDown, UiScale.Ic(14));
        _fcAdvancedChevron.Foreground = successFg;
        _fcAdvancedToggleText = new TextBlock
        {
            FontSize = UiScale.Fs(10),
            FontWeight = FontWeight.SemiBold,
            Foreground = successFg,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var advPill = new Border
        {
            Padding = new Thickness(10, 5, 8, 5),
            CornerRadius = new CornerRadius(radiusSm),
            Background = GetBrush("SurfaceBaseBrush"),
            BorderBrush = GetBrush("SuccessBorderBrush"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 4,
                Children = { _fcAdvancedToggleText, _fcAdvancedChevron },
            },
        };
        var advLabel = new TextBlock
        {
            Text = UiIcons.StripSymbols(Localization.FcAdvancedSettings),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var advRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(advPill, 1);
        advRow.Children.Add(advLabel);
        advRow.Children.Add(advPill);
        _fcAdvancedToggle = new Avalonia.Controls.Button
        {
            Content = advRow,
            FontSize = UiScale.Fs(11),
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            MinHeight = 44,
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
        UpdateFcAdvancedToggle();

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

        var headerRow = BuildFcListHeader(textM, sunken);

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
            FontSize = UiScale.Fs(10),
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

        _fcClearAllButton = new Avalonia.Controls.Button
        {
            Content = IconText(UiIcons.Trash, Localization.FcSavedClearAll, 14),
            FontSize = UiScale.Fs(10),
            Padding = new Thickness(10, 4),
            MinHeight = 44,
            VerticalContentAlignment = VerticalAlignment.Center,
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
        Grid.SetColumn(_fcClearAllButton, 1);
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
            FontSize = UiScale.Fs(10),
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

        _fcStatusText = new TextBlock
        {
            Text = Localization.FcStatusEmpty,
            FontSize = UiScale.Fs(10),
            Opacity = 0.7,
            Foreground = textS,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _fcProgressLabel = new TextBlock
        {
            FontSize = UiScale.Fs(10),
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
            Text = Localization.FcConnectHintTouch,
            FontSize = UiScale.Fs(10),
            Opacity = 0.55,
            Foreground = textM,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };

        _fcUseButton = new Avalonia.Controls.Button
        {
            Content = Localization.FcUseSelected,
            FontSize = UiScale.Fs(13),
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

    private Border BuildAdvancedSettingsPanel(IBrush successFg)
    {
        var targetLabel = new TextBlock
        {
            Text = Localization.FcTargetNLabel,
            FontSize = UiScale.Fs(11),
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
            FontSize = UiScale.Fs(11),
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
            FontSize = UiScale.Fs(11),
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
            FontSize = UiScale.Fs(11),
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
                FontSize = UiScale.Fs(11),
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
            FontSize = UiScale.Fs(9),
            Opacity = 0.55,
            Foreground = textM,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var col1 = new TextBlock
        {
            Text = Localization.FcColEndpoint,
            FontWeight = FontWeight.SemiBold,
            FontSize = UiScale.Fs(9),
            Opacity = 0.55,
            Foreground = textM,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        var col2 = new TextBlock
        {
            Text = Localization.FcColLatency,
            FontWeight = FontWeight.SemiBold,
            FontSize = UiScale.Fs(9),
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
            FontSize = UiScale.Fs(9),
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

    private StackPanel BuildSpinnerControl(
        int initial, int min, int max, int step, IBrush successFg,
        out TextBlock display,
        System.Action<int> onChanged)
    {
        var current = initial;
        display = new TextBlock
        {
            Text = current.ToString(System.Globalization.CultureInfo.InvariantCulture),
            FontSize = UiScale.Fs(13),
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            MinWidth = 48,
            Foreground = successFg,
        };
        var displayLocal = display;
        var minus = MakeSpinnerStepButton(UiIcons.Minus);
        var plus  = MakeSpinnerStepButton(UiIcons.Plus);
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

    private Avalonia.Controls.Button MakeSpinnerStepButton(string icon)
    {
        var btn = new Avalonia.Controls.Button
        {
            Content = IconContent(icon, 18),
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

    private Control BuildFcRow(FreeConfigEntry entry, bool isSavedTab)
    {
        var textP = GetBrush("TextPrimaryBrush");
        var textM = GetBrush("TextMutedBrush");
        var dangerSolid = GetBrush("DangerSolidBrush");

        var country = new TextBlock
        {
            Text = string.IsNullOrEmpty(entry.CountryCode)
                ? "—"
                : $"{FlagFor(entry.CountryCode)} {entry.CountryCode}".Trim(),
            FontSize = UiScale.Fs(10),
            Foreground = textP,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var endpoint = new TextBlock
        {
            Text = $"{entry.Host}:{entry.Port}",
            FontSize = UiScale.Fs(10),
            Foreground = textP,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var latencyText = entry.Status switch
        {
            FreeConfigStatus.Verified or FreeConfigStatus.Ok when entry.LatencyMs <= 0 => "—",
            FreeConfigStatus.Verified or FreeConfigStatus.Ok => $"{entry.LatencyMs} ms",
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
            Child = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 2,
                Children =
                {
                    new TextBlock
                    {
                        Text = latencyText,
                        Foreground = Brushes.White,
                        FontSize = UiScale.Fs(9),
                        FontWeight = FontWeight.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    new IconView
                    {
                        Icon = entry.Status == FreeConfigStatus.Verified ? UiIcons.CheckCheck : UiIcons.Check,
                        Size = UiScale.Ic(12),
                        Foreground = Brushes.White,
                        IsVisible = entry.Status is FreeConfigStatus.Verified or FreeConfigStatus.Ok,
                    },
                },
            },
        };

        var transport = new TextBlock
        {
            Text = entry.Transport ?? "tcp",
            FontSize = UiScale.Fs(9),
            Opacity = 0.6,
            Foreground = textM,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var grid = new Grid
        {
            ColumnDefinitions = isSavedTab
                ? new ColumnDefinitions("44,*,72,68,48")
                : new ColumnDefinitions("44,*,72,68"),
            MinHeight = 48,
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
                Content = IconContent(UiIcons.Trash, UiScale.Ic(18)),
                FontSize = UiScale.Fs(11),
                Padding = new Thickness(8),
                MinWidth = 48,
                MinHeight = 48,
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

    private static string FlagFor(string? cc)
    {
        if (string.IsNullOrEmpty(cc) || cc.Length != 2 || !char.IsAsciiLetter(cc[0]) || !char.IsAsciiLetter(cc[1]))
            return string.Empty;
        var upper = cc.ToUpperInvariant();
        var c0 = 0x1F1E6 + (upper[0] - 'A');
        var c1 = 0x1F1E6 + (upper[1] - 'A');
        return char.ConvertFromUtf32(c0) + char.ConvertFromUtf32(c1);
    }

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

        var persistedSaved = AndroidStorage.GetPublicActiveSubTabIsSaved();
        if (persistedSaved && _fcSavedResults.Count > 0)
            SelectFreeConfigsTab(1);
        else
            SelectFreeConfigsTab(0);
    }

    private void StopFreeConfigsBackgroundWork()
    {
        _fcOrchestrator?.Cancel();
    }

    private void SelectFreeConfigsTab(int index)
    {
        _fcSelectedTab = index;
        AndroidStorage.SetPublicActiveSubTabIsSaved(index == 1);
        if (_fcSearchBody is not null) _fcSearchBody.IsVisible = index == 0;
        if (_fcSavedBody is not null)  _fcSavedBody.IsVisible  = index == 1;
        if (_fcTabSearch is not null)
        {
            StyleAdvShellTab(_fcTabSearch, index == 0);
            _fcTabSearch.Content = UiIcons.StripSymbols(Localization.FcTabSearch);
        }
        if (_fcTabSaved is not null)
        {
            StyleAdvShellTab(_fcTabSaved, index == 1);
            _fcTabSaved.Content = SavedTabHeaderText();
        }

        _fcSelectedEntry = null;
        if (_fcUseButton is not null) _fcUseButton.IsEnabled = false;
    }

    private string SavedTabHeaderText()
    {
        var n = _fcSavedResults.Count;
        if (n <= 0) return Localization.FcTabSaved;
        return Localization.FcTabSavedWithCount(n);
    }

    private async void OnFreeConfigsFindClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_fcOrchestrator is null) return;
        if (_fcOrchestrator.IsBusy) return;

        var target = _fcTargetValue;
        if (target < 1) target = 1;
        if (target > 50) target = 50;
        _fcTargetSnapshot = target;

        var maxPing = _fcMaxPingValue;
        if (maxPing < 50) maxPing = 50;
        if (maxPing > 2000) maxPing = 2000;

        var excludeRu = _fcExcludeRu?.IsChecked == true;

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
        UpdateFcAdvancedToggle();
    }

    private void UpdateFcAdvancedToggle()
    {
        SetDisclosureChevron(_fcAdvancedChevron, _fcAdvancedExpanded);
        if (_fcAdvancedToggleText is not null)
            _fcAdvancedToggleText.Text = _fcAdvancedExpanded ? Localization.AdvPublicSettingsHide : Localization.AdvPublicSettingsShow;
    }

    private bool ApplyFcConnectGate()
    {
        var verified = _fcSelectedEntry is { } e &&
                       e.Status == FreeConfigStatus.Verified;
        if (_fcUseButton is not null) _fcUseButton.IsEnabled = verified;
        return verified;
    }

    private void OnFreeConfigsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox box && box.SelectedItem is FreeConfigEntry entry)
        {
            _fcSelectedEntry = entry;
            ApplyFcConnectGate();

            if (ReferenceEquals(box, _fcSearchList) && _fcSavedList is not null)
                _fcSavedList.SelectedItem = null;
            else if (ReferenceEquals(box, _fcSavedList) && _fcSearchList is not null)
                _fcSearchList.SelectedItem = null;
        }
    }

    private static DateTime _fcUseClickedAt = DateTime.MinValue;

    private void OnFreeConfigsUseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var now = DateTime.UtcNow;
        if ((now - _fcUseClickedAt).TotalMilliseconds < 200) return;
        _fcUseClickedAt = now;

        var entry = _fcSelectedEntry;
        if (entry is null) return;
        if (_fcOrchestrator?.IsBusy == true)
        {
            ShowMenuFeedback(Localization.FcConnectBusySearch);
            return;
        }
        if (entry.Status != FreeConfigStatus.Verified)
        {
            ShowMenuFeedback(Localization.FcConnectNeedsVerifyTouch);
            return;
        }
        if (string.IsNullOrEmpty(entry.RawUri)) return;

        try
        {
            var vless = VPNRouter.Core.Services.ServerUriParser.Parse(entry.RawUri);
            var servers = AndroidStorage.GetServers() ?? new System.Collections.Generic.List<VPNRouter.Core.Models.VlessServerEntry>();
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
            // Never wipe the user's server list on a parse or persist failure: keep prior settings, surface the error and abort the apply.
            global::Android.Util.Log.Warn("VpnRouter.FC",
                $"Apply public config failed — {ex.GetType().Name}: {ex.Message}; servers left unchanged");
            ShowMenuFeedback(Localization.FcApplyFailed);
            return;
        }
        AndroidStorage.SetVlessUri(entry.RawUri);
        AndroidStorage.SetSubscriptionUrl(null);

        ShowMenuFeedback(Localization.FcUsedToast);

        CloseAdvancedShell();

        UpdateConfigSummary();

        Dispatcher.UIThread.Post(() =>
        {
            MainActivity.Instance?.RequestConnect();
        }, DispatcherPriority.Background);
    }

    private void OnFreeConfigsClearAllClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _fcOrchestrator?.ClearSaved();
        ReloadFreeConfigsLists();
    }

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
            for (int i = 0; i < _fcSearchResults.Count; i++)
            {
                if (string.Equals(_fcSearchResults[i].Id, entry.Id, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            _fcSearchResults.Add(entry);
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

    private void OnFcEntryUpgraded(FreeConfigEntry entry)
    {
        if (entry is null || string.IsNullOrEmpty(entry.Id)) return;
        Dispatcher.UIThread.Post(() =>
        {
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

            var selectedIsConnectable = _fcSelectedEntry is { } sel &&
                                        sel.Status == FreeConfigStatus.Verified;
            if (!selectedIsConnectable && _fcSearchList is not null &&
                _fcSearchResults.Contains(entry))
                _fcSearchList.SelectedItem = entry;
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

    private void ReloadFreeConfigsLists()
    {
        if (_fcOrchestrator is null) return;
        var saved = _fcOrchestrator.Saved;

        _fcSavedResults.Clear();
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
