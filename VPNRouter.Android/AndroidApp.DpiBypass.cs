using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace VPNRouter.Android;

public partial class AndroidApp
{
    private int _dpiSelectedTab;
    private Avalonia.Controls.Button? _dpiTabStatus;
    private Avalonia.Controls.Button? _dpiTabStrategy;
    private Avalonia.Controls.Button? _dpiTabAdvanced;
    private Control? _dpiBodyStatus;
    private Control? _dpiBodyStrategy;
    private Control? _dpiBodyAdvanced;

    private Avalonia.Controls.ComboBox? _dpiStrategyComboBox;

    private Ellipse? _dpiFooterStatusDot;
    private TextBlock? _dpiFooterStatusText;
    private Avalonia.Controls.Button? _dpiFooterToggleBtn;

    private Ellipse? _dpiStatusBarDot;
    private TextBlock? _dpiStatusBarText;

    private Control BuildDpiBypassTabContent()
    {
        var bg          = GetBrush("SurfaceAppBrush");
        var subtle      = GetBrush("BorderSubtleBrush");
        var defaultB    = GetBrush("BorderDefaultBrush");
        var sunken      = GetBrush("SurfaceSunkenBrush");
        var textP       = GetBrush("TextPrimaryBrush");
        var textS       = GetBrush("TextSecondaryBrush");
        var textM       = GetBrush("TextMutedBrush");
        var warningBg   = GetBrush("WarningBgBrush");
        var warningBd   = GetBrush("WarningBorderBrush");
        var warningFg   = GetBrush("WarningFgBrush");
        var accentSolid = GetBrush("AccentSolidBrush");
        var accentOnSolid = GetBrush("AccentOnSolidBrush");
        var radiusSm    = GetRadius("RadiusSm");

        _dpiTabStatus   = MakeAdvancedSubTabButton(Localization.ZapretSecStatus,   active: true,
                                                   (_, _) => SelectDpiTab(0));
        _dpiTabStrategy = MakeAdvancedSubTabButton(Localization.ZapretSecStrategy, active: false,
                                                   (_, _) => SelectDpiTab(1));
        _dpiTabAdvanced = MakeAdvancedSubTabButton(Localization.ZapretSecAdvanced, active: false,
                                                   (_, _) => SelectDpiTab(2));

        var tabRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(6, 4, 6, 4),
            Children = { _dpiTabStatus, _dpiTabStrategy, _dpiTabAdvanced },
        };

        var statusTitle = new TextBlock
        {
            Text = Localization.ZapretSecStatus,
            FontWeight = FontWeight.Bold,
            FontSize = 13,
            Foreground = textP,
        };
        var statusDesc = new TextBlock
        {
            Text = Localization.SettingsDpiBypassHint,
            FontSize = 10,
            Opacity = 0.7,
            Foreground = textS,
            TextWrapping = TextWrapping.Wrap,
        };

        _dpiStatusBarDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _dpiStatusBarText = new TextBlock
        {
            Text = ZapretStatusLabelForCurrentMode(),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = textP,
        };
        var statusInnerRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            Children = { _dpiStatusBarDot, _dpiStatusBarText },
        };
        var statusBar = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(radiusSm),
            Background = sunken,
            Child = statusInnerRow,
        };

        var warningIcon = new TextBlock
        {
            Text = "⚠",
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            Foreground = warningFg,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var warningText = new TextBlock
        {
            Text = Localization.SettingsDpiBypassWarning,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Foreground = warningFg,
        };
        var warningGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 8,
        };
        Grid.SetColumn(warningIcon, 0);
        Grid.SetColumn(warningText, 1);
        warningGrid.Children.Add(warningIcon);
        warningGrid.Children.Add(warningText);
        var warningBanner = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(radiusSm),
            Background = warningBg,
            BorderBrush = warningBd,
            BorderThickness = new Thickness(1),
            Child = warningGrid,
        };

        var statusStack = new StackPanel
        {
            Spacing = 10,
            Children = { statusTitle, statusDesc, statusBar, warningBanner },
        };
        _dpiBodyStatus = new ScrollViewer
        {
            Content = new Border
            {
                Padding = new Thickness(12, 10, 12, 12),
                Child = statusStack,
            },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = bg,
        };

        var strategyTitle = new TextBlock
        {
            Text = Localization.ZapretSecStrategy,
            FontWeight = FontWeight.Bold,
            FontSize = 13,
            Foreground = textP,
        };
        var strategyDesc = new TextBlock
        {
            Text = Localization.ZapretSecStrategyDesc,
            FontSize = 10,
            Opacity = 0.7,
            Foreground = textS,
            TextWrapping = TextWrapping.Wrap,
        };

        _dpiStrategyComboBox = new Avalonia.Controls.ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 4),
            FontSize = 11,
            ItemsSource = new List<string>
            {
                Localization.SettingsDpiBypassOff,
                Localization.SettingsDpiBypassStandard,
                Localization.SettingsDpiBypassAggressive,
            },
            SelectedIndex = AndroidStorage.GetDpiBypassMode() switch
            {
                "standard"   => 1,
                "aggressive" => 2,
                _            => 0,
            },
        };
        _dpiStrategyComboBox.SelectionChanged += OnDpiStrategyChanged;

        var versionRow = new TextBlock
        {
            Text = IsRu()
                ? "Встроенный sing-box (tls_fragment) — без отдельной службы"
                : "Built-in sing-box (tls_fragment) — no separate service",
            FontSize = 10,
            Opacity = 0.5,
            Foreground = textM,
            TextWrapping = TextWrapping.Wrap,
        };

        var strategyStack = new StackPanel
        {
            Spacing = 10,
            Children = { strategyTitle, strategyDesc, _dpiStrategyComboBox, versionRow },
        };
        _dpiBodyStrategy = new ScrollViewer
        {
            Content = new Border
            {
                Padding = new Thickness(12, 10, 12, 12),
                Child = strategyStack,
            },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = bg,
            IsVisible = false,
        };

        var advTitle = new TextBlock
        {
            Text = Localization.ZapretSecAdvanced,
            FontWeight = FontWeight.Bold,
            FontSize = 13,
            Foreground = textP,
        };
        var advDesc = new TextBlock
        {
            Text = Localization.ZapretSecAdvancedDesc,
            FontSize = 10,
            Opacity = 0.7,
            Foreground = textS,
            TextWrapping = TextWrapping.Wrap,
        };

        var advHealthBtn = new Avalonia.Controls.Button
        {
            Content = Localization.AndroidToolsRunHealthCheck,
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(radiusSm),
            Background = Brushes.Transparent,
            BorderBrush = defaultB,
            BorderThickness = new Thickness(1),
            Foreground = textP,
        };
        advHealthBtn.Click += (s, e) =>
        {
            CloseAdvancedShell();
            OnMenuHealthCheckClicked(s, e);
        };

        var advLogBtn = new Avalonia.Controls.Button
        {
            Content = Localization.AndroidToolsOpenLog,
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(radiusSm),
            Background = Brushes.Transparent,
            BorderBrush = defaultB,
            BorderThickness = new Thickness(1),
            Foreground = textP,
        };
        advLogBtn.Click += (s, e) =>
        {
            CloseAdvancedShell();
            OnMenuOpenLogClicked(s, e);
        };

        var advLeakBtn = new Avalonia.Controls.Button
        {
            Content = Localization.AndroidToolsCheckLeak,
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(radiusSm),
            Background = Brushes.Transparent,
            BorderBrush = defaultB,
            BorderThickness = new Thickness(1),
            Foreground = textP,
        };
        advLeakBtn.Click += (s, e) =>
        {
            CloseAdvancedShell();
            OnMenuCheckLeaksClicked(s, e);
        };

        var notApplicable = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(radiusSm),
            Background = sunken,
            BorderBrush = subtle,
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = Localization.AndroidZapretSectionNotApplicable,
                FontSize = 10,
                Opacity = 0.7,
                Foreground = textS,
                TextWrapping = TextWrapping.Wrap,
            },
        };

        var advStack = new StackPanel
        {
            Spacing = 6,
            Children = { advTitle, advDesc, advHealthBtn, advLogBtn, advLeakBtn, notApplicable },
        };
        _dpiBodyAdvanced = new ScrollViewer
        {
            Content = new Border
            {
                Padding = new Thickness(12, 10, 12, 12),
                Child = advStack,
            },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = bg,
            IsVisible = false,
        };

        var bodyArea = new Grid
        {
            Children = { _dpiBodyStatus, _dpiBodyStrategy, _dpiBodyAdvanced },
        };

        _dpiFooterStatusDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _dpiFooterStatusText = new TextBlock
        {
            Text = ZapretStatusLabelForCurrentMode(),
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = textS,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var footerStatusRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _dpiFooterStatusDot, _dpiFooterStatusText },
        };

        _dpiFooterToggleBtn = new Avalonia.Controls.Button
        {
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(10, 5),
            CornerRadius = new CornerRadius(radiusSm),
            Background = accentSolid,
            Foreground = accentOnSolid,
            BorderThickness = new Thickness(0),
            Content = AndroidStorage.GetDpiBypassMode() == "off"
                ? Localization.AndroidDpiBypassFooterToggleOn
                : Localization.AndroidDpiBypassFooterToggleOff,
        };
        _dpiFooterToggleBtn.Click += OnDpiFooterToggleClicked;

        var footerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(footerStatusRow, 0);
        Grid.SetColumn(_dpiFooterToggleBtn, 1);
        footerGrid.Children.Add(footerStatusRow);
        footerGrid.Children.Add(_dpiFooterToggleBtn);

        var footer = new Border
        {
            Padding = new Thickness(14, 7, 14, 8),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = defaultB,
            Background = sunken,
            Child = footerGrid,
        };

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(tabRow, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(tabRow);
        dock.Children.Add(footer);
        dock.Children.Add(bodyArea);

        UpdateDpiFooterState();

        return new Border
        {
            Background = bg,
            Child = dock,
        };
    }

    private void SelectDpiTab(int index)
    {
        _dpiSelectedTab = index;
        if (_dpiBodyStatus   is not null) _dpiBodyStatus.IsVisible   = index == 0;
        if (_dpiBodyStrategy is not null) _dpiBodyStrategy.IsVisible = index == 1;
        if (_dpiBodyAdvanced is not null) _dpiBodyAdvanced.IsVisible = index == 2;
        if (_dpiTabStatus   is not null) StyleAdvShellTab(_dpiTabStatus,   index == 0);
        if (_dpiTabStrategy is not null) StyleAdvShellTab(_dpiTabStrategy, index == 1);
        if (_dpiTabAdvanced is not null) StyleAdvShellTab(_dpiTabAdvanced, index == 2);
    }

    private void ReseedDpiBypassTabState()
    {
        var mode = AndroidStorage.GetDpiBypassMode();
        if (_dpiStrategyComboBox is not null)
        {
            _dpiStrategyComboBox.SelectedIndex = mode switch
            {
                "standard"   => 1,
                "aggressive" => 2,
                _            => 0,
            };
        }
        if (_dpiStatusBarText is not null)
            _dpiStatusBarText.Text = ZapretStatusLabelForCurrentMode();
        if (_dpiStatusBarDot is not null)
            _dpiStatusBarDot.Fill = GetBrush(mode != "off"
                ? "SuccessSolidBrush" : "TextMutedBrush");
        UpdateDpiFooterState();
    }

    private void OnDpiStrategyChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
    {
        if (_dpiStrategyComboBox is null) return;
        var value = _dpiStrategyComboBox.SelectedIndex switch
        {
            1 => "standard",
            2 => "aggressive",
            _ => "off",
        };
        AndroidStorage.SetDpiBypassMode(value);

        if (_settingsDpiBypassMode is not null)
        {
            _settingsLoading = true;
            try
            {
                _settingsDpiBypassMode.SelectedIndex = _dpiStrategyComboBox.SelectedIndex;
            }
            finally { _settingsLoading = false; }
        }

        UpdateZapretChipFromState();
        UpdateDpiFooterState();
    }

    private void OnDpiFooterToggleClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var current = AndroidStorage.GetDpiBypassMode();
        var next = current == "off" ? "standard" : "off";
        AndroidStorage.SetDpiBypassMode(next);

        if (_dpiStrategyComboBox is not null)
        {
            _dpiStrategyComboBox.SelectedIndex = next switch
            {
                "standard"   => 1,
                "aggressive" => 2,
                _            => 0,
            };
        }
        if (_settingsDpiBypassMode is not null)
        {
            _settingsLoading = true;
            try { _settingsDpiBypassMode.SelectedIndex = _dpiStrategyComboBox?.SelectedIndex ?? 0; }
            finally { _settingsLoading = false; }
        }

        UpdateZapretChipFromState();
        UpdateDpiFooterState();
    }

    private void UpdateDpiFooterState()
    {
        var mode = AndroidStorage.GetDpiBypassMode();
        var enabled = mode != "off";
        if (_dpiFooterStatusText is not null)
            _dpiFooterStatusText.Text = ZapretStatusLabelForCurrentMode();
        if (_dpiFooterStatusDot is not null)
            _dpiFooterStatusDot.Fill = GetBrush(enabled
                ? "SuccessSolidBrush" : "TextMutedBrush");
        if (_dpiFooterToggleBtn is not null)
            _dpiFooterToggleBtn.Content = enabled
                ? Localization.AndroidDpiBypassFooterToggleOff
                : Localization.AndroidDpiBypassFooterToggleOn;
    }

    private static bool IsRu()
    {
        return Localization.ZapretSecStrategy.StartsWith("Стр", StringComparison.Ordinal);
    }
}
